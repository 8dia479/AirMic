using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "AirMic.lnk");
    public static bool IsInstalled => File.Exists(ManifestPath);
    public static bool CanInstall => DriverInstaller.HasEmbeddedPackage && Assembly.GetExecutingAssembly().GetManifestResourceInfo(NoticesResource) is not null;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private sealed record VersionFiles(string Folder, string[] Files);
    private sealed record Installation(int SchemaVersion, string Current, List<VersionFiles> Versions);

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
    private static bool VersionName(string? name) => name is not null && name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static Installation ReadInstallation()
    {
        if (!File.Exists(ManifestPath)) return new(1, "", []);
        if (new FileInfo(ManifestPath).Length > 65536) throw new InvalidDataException("설치 기록 크기 오류");
        Installation saved = JsonSerializer.Deserialize<Installation>(File.ReadAllText(ManifestPath), new JsonSerializerOptions { MaxDepth = 8 })
            ?? throw new InvalidDataException("설치 기록 오류");
        if (saved.SchemaVersion != 1 || saved.Versions is null || saved.Versions.Count > 64
            || saved.Versions.Any(v => v is null || !VersionName(v.Folder) || v.Files is null || v.Files.Length > 64 || v.Files.Any(f => !DriverPackage.SafeName(f))))
            throw new InvalidDataException("안전하지 않은 설치 기록입니다. 자동 제거하지 않습니다.");
        return saved;
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
        if (!CanInstall) throw new InvalidOperationException("서명된 driver/라이선스 bundle이 없는 진단 빌드는 설치 제품이 아닙니다.");
        string source = Environment.ProcessPath ?? throw new IOException("현재 실행 파일 경로 없음");
        if (!Path.GetFileName(source).Equals("AirMic.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("self-contained AirMic.exe로 설치하세요.");
        Dictionary<string, byte[]> notices = ReadNotices();
        EnsureProtectedDirectory(ProductRoot);
        Installation previous = ReadInstallation();
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        string version = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string directory = Path.Combine(ProductRoot, "Versions", version);
        EnsureProtectedDirectory(directory);
        string executable = Path.Combine(directory, "AirMic.exe");
        if (!File.Exists(executable))
        {
            original.Position = 0;
            using var destination = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            original.CopyTo(destination);
        }
        using (var copy = File.OpenRead(executable))
            if (Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant() != version) throw new InvalidDataException("설치 실행 파일 해시 불일치");
        foreach (var file in notices) File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value);
        string driverResult = DriverInstaller.InstallEmbedded(); // PnP validates catalog/signature/coverage; never bypassed.
        var versions = previous.Versions.Where(v => v.Folder != version).Append(new VersionFiles(version, ["AirMic.exe", .. notices.Keys])).ToList();
        if (versions.Count > 64) throw new IOException("설치 버전이 너무 많습니다. 먼저 이전 설치를 제거하세요.");
        string temporary = ManifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Installation(1, version, versions)));
        File.Move(temporary, ManifestPath, true);
        CreateShortcut(executable);
        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath, writable: true))
        {
            key.SetValue("DisplayName", "AirMic"); key.SetValue("DisplayVersion", "0.2.0");
            key.SetValue("Publisher", "AirMic Project"); key.SetValue("InstallLocation", ProductRoot);
            key.SetValue("DisplayIcon", executable); key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        string firewall = "";
        try { SetFirewall(executable); }
        catch (COMException e) { firewall = "\nPrivate LAN 방화벽 자동 설정 실패: " + e.Message + " · Windows 방화벽에서 이 EXE를 허용하세요."; }
        return $"AirMic 설치 처리 완료. 시작 메뉴에서 AirMic을 실행하세요.\n{driverResult}{firewall}";
    }
    public static string Uninstall()
    {
        if (!IsAdministrator) throw new UnauthorizedAccessException("AirMic 제거에는 관리자 승인이 필요합니다.");
        if (!IsInstalled) throw new InvalidOperationException("설치된 AirMic 기록이 없습니다.");
        EnsureProtectedDirectory(ProductRoot);
        Installation saved = ReadInstallation(); // Refuse unsafe paths BEFORE any device/file removal.
        string driver = DriverInstaller.UninstallAirMicDevices();
        try { SetFirewall(null); } catch (COMException) { /* Disabled firewall service: stale rule cannot run the deleted program. */ }
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
        bool pending = false;
        foreach (VersionFiles version in saved.Versions)
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
                    if (DriverPackage.SafeName(Path.GetFileName(file))) DeleteOrSchedule(file, ref pending);
                DeleteDirectoryIfEmpty(stage, ref pending);
            }
            DeleteDirectoryIfEmpty(packages, ref pending);
        }
        DeleteOrSchedule(ManifestPath, ref pending);
        DeleteDirectoryIfEmpty(Path.Combine(ProductRoot, "Versions"), ref pending);
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
