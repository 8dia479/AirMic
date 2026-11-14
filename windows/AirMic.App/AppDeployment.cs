using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AirMic.Setup;
using Microsoft.Win32;

namespace AirMic.App;

// Same self-contained EXE installs its own versioned app files AND embedded signed
// driver. No MSI engine download, helper audio application or separate .NET runtime.
// Install/uninstall are explicit elevated control paths, never audio-thread work.
internal static class AppDeployment
{
    private const string NoticesResource = "AirMic.RedistributionNotices.zip";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AirMic";
    private const string FirewallName = "AirMic (Private LAN UDP)";
    public static string ProductRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AirMic");
    private static string ManifestPath => Path.Combine(ProductRoot, "installation.json");
    private static string RemovalMarker => Path.Combine(ProductRoot, ".airmic-uninstall-pending");
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AirMic.lnk");
    public static bool IsInstalled => File.Exists(ManifestPath);
    public static bool CanInstall => DriverInstaller.HasEmbeddedPackage && Assembly.GetExecutingAssembly().GetManifestResourceInfo(NoticesResource) is not null;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static void EnsureProtectedDirectory(string directory)
    {
        string root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 경로 오류");
        var parents = new Stack<DirectoryInfo>();
        for (DirectoryInfo? item = new(full); item is not null && !item.FullName.Equals(root, StringComparison.OrdinalIgnoreCase); item = item.Parent)
            parents.Push(item);
        // Inspect existing ancestors before creating any child through them.
        foreach (DirectoryInfo item in parents)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("설치 경로의 reparse point는 지원하지 않습니다.");
            if (!item.Exists) item.Create();
        }
    }
    internal static void EnsureNotReparseFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("reparse point 파일은 설치/제거하지 않습니다.");
    }
    private static AppInstallState ReadInstallation()
    {
        if (!File.Exists(ManifestPath)) return AppInstallState.Empty;
        EnsureNotReparseFile(ManifestPath);
        if (new FileInfo(ManifestPath).Length > 65536) throw new InvalidDataException("설치 기록 크기 오류");
        return AppInstallState.Read(File.ReadAllBytes(ManifestPath));
    }
    private static void SaveInstallation(AppInstallState state)
    {
        byte[] data = state.Serialize();
        string temporary = Path.Combine(ProductRoot, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(data); file.Flush(flushToDisk: true); }
            File.Move(temporary, ManifestPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string InstallationWarning
    {
        get
        {
            if (File.Exists(RemovalMarker)) return "이전 제거가 완료되지 않았거나 재부팅 대기 중입니다. 제거 실패라면 다시 제거하고, 완료됐다면 Windows를 재부팅하세요.";
            if (!IsInstalled) return "";
            try { return ReadInstallation().Pending == "" ? "" : "이전 설치가 완료되지 않았습니다. 설치 / 복구 또는 제거를 다시 실행하세요."; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            { return "설치 기록을 확인할 수 없습니다: " + e.Message; }
        }
    }
    private sealed class InstallationLock : IDisposable
    {
        private readonly Mutex mutex = new(false, @"Global\AirMic.Installation.v1");
        public InstallationLock()
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; } // Durable pending journal survives the crashed owner.
            if (!acquired) { mutex.Dispose(); throw new IOException("다른 AirMic 설치/제거가 진행 중입니다."); }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private static Dictionary<string, byte[]> ReadNotices()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(NoticesResource)
            ?? throw new InvalidOperationException("배포 runtime 라이선스가 내장되지 않았습니다. 공식 publish script를 사용하세요.");
        using var zip = new ZipArchive(stream);
        if (zip.Entries.Count is < 4 or > 32 || zip.Entries.Sum(e => e.Length) > 16 * 1024 * 1024) throw new InvalidDataException("배포 고지 패키지 오류");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!DriverPackage.SafeName(entry.FullName) || !(entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("배포 고지 경로 오류");
            byte[] bytes = new byte[checked((int)entry.Length)];
            using Stream input = entry.Open(); input.ReadExactly(bytes);
            if (!files.TryAdd(entry.FullName, bytes)) throw new InvalidDataException("중복 배포 고지");
        }
        return files;
    }
    public static string Install()
    {
        if (!IsAdministrator) throw new UnauthorizedAccessException("AirMic 설치에는 관리자 승인이 필요합니다.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("전용 driver는 Windows 10 (19041+) / 11 x64 전용입니다. ARM64는 지원하지 않습니다.");
        if (!CanInstall) throw new InvalidOperationException("서명된 driver/라이선스 bundle이 없는 진단 빌드는 설치 제품이 아닙니다.");
        using var installationLock = new InstallationLock();
        string source = Environment.ProcessPath ?? throw new IOException("현재 실행 파일 경로 없음");
        if (!Path.GetFileName(source).Equals("AirMic.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("self-contained AirMic.exe로 설치하세요.");
        // Both archives are validated before any privileged file/PnP mutations.
        DriverPackage driverPackage = DriverInstaller.ReadEmbedded();
        Dictionary<string, byte[]> notices = ReadNotices();
        EnsureProtectedDirectory(ProductRoot);
        if (File.Exists(RemovalMarker)) throw new IOException("이전 제거가 완료되지 않았거나 재부팅 대기 중입니다. 제거를 완료하고 Windows 재부팅 후 설치하세요. 예약 삭제될 경로에 재설치하지 않습니다.");
        AppInstallState previous = ReadInstallation();
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        string version = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        AppInstallState prepared = previous.Prepare(version, ["AirMic.exe", .. notices.Keys]);
        _ = prepared.Serialize(); // Enforce journal bounds BEFORE copying/installing anything.
        string directory = Path.Combine(ProductRoot, "Versions", version);
        EnsureProtectedDirectory(directory);
        string executable = Path.Combine(directory, "AirMic.exe");
        var createdFiles = new List<string>();
        bool journalSaved = false;
        string fallback = Path.Combine(ProductRoot, "Versions", prepared.Current, "AirMic.exe");
        try
        {
            EnsureNotReparseFile(executable);
            if (!File.Exists(executable))
            {
                original.Position = 0;
                using var destination = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                createdFiles.Add(executable);
                original.CopyTo(destination); destination.Flush(flushToDisk: true);
            }
            using (var copy = File.OpenRead(executable))
                if (Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant() != version) throw new InvalidDataException("설치 실행 파일 해시 불일치");
            foreach (var file in notices)
            {
                string path = Path.Combine(directory, file.Key); EnsureNotReparseFile(path);
                if (!File.Exists(path)) createdFiles.Add(path);
                File.WriteAllBytes(path, file.Value);
            }
            // Record ALL candidate files first. Interrupted updates retain the old
            // current app; first installs retain an explicit repair/uninstall path.
            SaveInstallation(prepared); journalSaved = true;
            _ = RegisterIntegration(fallback);
            string driverResult = DriverInstaller.Install(driverPackage);
            string firewall = RegisterIntegration(executable);
            SaveInstallation(prepared.Complete(version)); // Final app-side commit only after PnP succeeds.
            return $"AirMic 설치 처리 완료. 시작 메뉴에서 AirMic을 실행하세요.\n{driverResult}{firewall}";
        }
        catch (Exception error)
        {
            string recovery = "";
            if (journalSaved)
            {
                // Do not silently downgrade a signed driver after a partial PnP
                // update. Restore the previous APP integration, retain candidate
                // files + pending state for explicit repair/uninstall instead.
                try { _ = RegisterIntegration(fallback); }
                catch (Exception integrationError) { recovery = "\n시작 메뉴/제거 등록 복구도 실패: " + integrationError.Message; }
                recovery += "\n설치 기록과 복구용 EXE를 보존했습니다. 설치 / 복구 또는 제거를 다시 실행하세요."
                    + "\nDriver 변경은 자동 downgrade하지 않았습니다. Windows 재부팅이 필요할 수 있습니다.";
            }
            else
            {
                foreach (string path in createdFiles)
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw new InvalidOperationException(error.Message + recovery, error);
        }
    }
    private static string RegisterIntegration(string executable)
    {
        CreateShortcut(executable);
        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath, writable: true))
        {
            key.SetValue("DisplayName", "AirMic"); key.SetValue("DisplayVersion", "0.2.0");
            key.SetValue("Publisher", "AirMic Project"); key.SetValue("InstallLocation", ProductRoot);
            key.SetValue("DisplayIcon", executable); key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        try { SetFirewall(executable); return ""; }
        catch (COMException e) { return "\nPrivate LAN 방화벽 자동 설정 실패: " + e.Message + " · Windows 방화벽에서 이 EXE를 허용하세요."; }
    }
    public static string Uninstall()
    {
        if (!IsAdministrator) throw new UnauthorizedAccessException("AirMic 제거에는 관리자 승인이 필요합니다.");
        if (!IsInstalled) throw new InvalidOperationException("설치된 AirMic 기록이 없습니다.");
        using var installationLock = new InstallationLock();
        EnsureProtectedDirectory(ProductRoot);
        if (!IsInstalled) throw new InvalidOperationException("설치 기록이 이미 제거되었습니다. 재부팅 대기 중인지 확인하세요.");
        AppInstallState saved = ReadInstallation(); // Refuse unsafe paths BEFORE any device/file removal.
        EnsureNotReparseFile(RemovalMarker);
        File.WriteAllText(RemovalMarker, "Uninstall started; do not reinstall into paths pending reboot deletion.");
        string driver = DriverInstaller.UninstallAirMicDevices();
        try { SetFirewall(null); } catch (COMException) { /* Disabled firewall service: stale rule cannot run the deleted program. */ }
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
        bool pending = false;
        foreach (InstalledVersion version in saved.Versions)
        {
            string directory = Path.Combine(ProductRoot, "Versions", version.Folder);
            if (!Directory.Exists(directory)) continue;
            EnsureProtectedDirectory(directory);
            foreach (string name in version.Files) DeleteOrSchedule(Path.Combine(directory, name), ref pending);
            DeleteDirectoryIfEmpty(directory, ref pending);
        }
        // Only flat, generated package directories of this product; never recurse through arbitrary content.
        string packages = Path.Combine(ProductRoot, "DriverPackages");
        if (Directory.Exists(packages))
        {
            EnsureProtectedDirectory(packages);
            foreach (string stage in Directory.EnumerateDirectories(packages))
            {
                if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
                EnsureProtectedDirectory(stage);
                if (Directory.EnumerateDirectories(stage).Any()) continue;
                foreach (string file in Directory.EnumerateFiles(stage))
                    if (DriverPackage.SafeName(Path.GetFileName(file)) || Path.GetFileName(file) == DriverInstaller.PackageRecordName)
                        DeleteOrSchedule(file, ref pending);
                DeleteDirectoryIfEmpty(stage, ref pending);
            }
            DeleteDirectoryIfEmpty(packages, ref pending);
        }
        DeleteOrSchedule(ManifestPath, ref pending);
        DeleteDirectoryIfEmpty(Path.Combine(ProductRoot, "Versions"), ref pending);
        if (pending)
        {
            // A running EXE can only be removed after reboot. Reinstalling the
            // same hash/path now would otherwise delete the NEW install at boot.
            if (!MoveFileEx(RemovalMarker, null, 4)) throw new Win32Exception();
        }
        else File.Delete(RemovalMarker);
        DeleteDirectoryIfEmpty(ProductRoot, ref pending);
        return driver + "\nAirMic 시작 메뉴/프로그램 등록을 제거했습니다. 사용자 설정은 보존합니다."
            + (pending ? "\n실행 중인 파일은 Windows 재부팅 시 정리됩니다. 재부팅은 자동 수행하지 않습니다." : "");
    }
    private static void DeleteOrSchedule(string path, ref bool pending)
    {
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse point 파일은 자동 제거하지 않습니다.");
        try { File.Delete(path); }
        catch (IOException)
        {
            if (!MoveFileEx(path, null, 4)) throw new Win32Exception(); // MOVEFILE_DELAY_UNTIL_REBOOT, exact owned path only.
            pending = true;
        }
    }
    private static void DeleteDirectoryIfEmpty(string path, ref bool pending)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path); }
        catch (IOException)
        {
            // Windows will remove it only if empty at reboot; unknown user files are never recursively deleted.
            if (MoveFileEx(path, null, 4)) pending = true;
        }
    }
    private static void SetFirewall(string? executable)
    {
        object policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        object? rule = null, rules = null;
        try
        {
            rules = ((dynamic)policy).Rules;
            try { ((dynamic)rules).Remove(FirewallName); }
            catch (COMException e) when (e.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070490)) { }
            if (executable is null) return;
            rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            dynamic configuration = rule;
            configuration.Name = FirewallName; configuration.Description = "AirMic UDP from Private LocalSubnet only";
            configuration.ApplicationName = executable; configuration.Protocol = 17; configuration.Direction = 1;
            configuration.RemoteAddresses = "LocalSubnet"; configuration.Profiles = 2; // NET_FW_PROFILE2_PRIVATE
            configuration.Action = 1; configuration.Enabled = true; configuration.EdgeTraversal = false;
            ((dynamic)rules).Add(configuration);
        }
        finally { WindowsAudio.Release(rule); WindowsAudio.Release(rules); WindowsAudio.Release(policy); }
    }
    private static void CreateShortcut(string executable)
    {
        object link = new ShellLinkObject();
        try
        {
            var shortcut = (IShellLink)link;
            shortcut.SetPath(executable); shortcut.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            shortcut.SetDescription("AirMic wireless microphone"); shortcut.SetIconLocation(executable, 0);
            ((IPersistFile)link).Save(ShortcutPath, true);
        }
        finally { WindowsAudio.Release(link); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string path, string? destination, uint flags);
    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)] private class ShellLinkObject { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath(StringBuilder path, int length, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription(StringBuilder description, int length); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(StringBuilder path, int length); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments(StringBuilder arguments, int length); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short key); void SetHotkey(short key); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation(StringBuilder path, int length, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved); void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
