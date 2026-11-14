using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirMic.Setup;
using Microsoft.Win32;

namespace AirMic.App;

// Only uses a package embedded at PUBLISH time. Never downloads a driver,
// disables signature enforcement, forces an in-use package removal or downgrades.
internal static class DriverInstaller
{
    private const string Resource = "AirMic.DriverPackage.zip", HardwareId = DriverPackage.HardwareId;
    internal const string PackageRecordName = ".airmic-package.json";
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    private static string PackageRoot => Path.Combine(AppDeployment.ProductRoot, "DriverPackages");
    private sealed record PackageRecord(int SchemaVersion, string PublishedInf);
    public static bool HasEmbeddedPackage => Assembly.GetExecutingAssembly().GetManifestResourceInfo(Resource) is not null;
    private static bool IsOemInf(string? name) => name is not null && name.Length <= 32
        && Regex.IsMatch(name, @"^oem[0-9]+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static DriverPackage ReadEmbedded()
    {
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("이 진단 빌드에는 서명된 AirMic 드라이버가 없습니다.");
        return DriverPackage.Read(input);
    }
    public static string Install(DriverPackage package)
    {
        if (!AppDeployment.IsAdministrator) throw new UnauthorizedAccessException("드라이버 설치에는 관리자 승인이 필요합니다.");
        AppDeployment.EnsureProtectedDirectory(PackageRoot);
        string stage = Path.Combine(PackageRoot, Guid.NewGuid().ToString("N"));
        AppDeployment.EnsureProtectedDirectory(stage);
        foreach (var file in package.Files)
        {
            using var output = new FileStream(Path.Combine(stage, file.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(file.Value.Span);
        }
        string inf = Path.Combine(stage, DriverPackage.Inf);
        var destination = new StringBuilder(1024);
        // Stage through Windows SetupAPI and obtain the actual published oem*.inf
        // without parsing locale-dependent pnputil output. PnP/signature policy is
        // still Windows-owned; these hashes do not substitute for catalog trust.
        if (!SetupCopyOEMInf(inf, stage, 1, 0, destination, (uint)destination.Capacity, out _, IntPtr.Zero))
            throw Failure("Windows가 driver package를 거부했습니다. 서명/호환성을 확인하세요.");
        string published = Path.GetFileName(destination.ToString());
        if (!IsOemInf(published)) throw new IOException("Windows published INF 이름 오류: " + destination);
        // Persist before registering a device, so a failed/partial install or a
        // failed package deletion can be retried even after all devices are gone.
        File.WriteAllText(Path.Combine(stage, PackageRecordName), JsonSerializer.Serialize(new PackageRecord(1, published)));
        bool created = false;
        IntPtr set = IntPtr.Zero;
        var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
        try
        {
            if (!DeviceExists())
            {
                Guid category = MediaClass;
                set = SetupDiCreateDeviceInfoList(ref category, IntPtr.Zero);
                if (set == new IntPtr(-1)) throw Failure("장치 목록 생성 실패");
                if (!SetupDiCreateDeviceInfo(set, "AirMic", ref category, "AirMic Virtual Audio", IntPtr.Zero, 1, ref info)) throw Failure("AirMic 장치 생성 실패");
                byte[] ids = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(set, ref info, 1, ids, ids.Length)) throw Failure("AirMic hardware ID 등록 실패");
                if (!SetupDiCallClassInstaller(0x19, set, ref info)) throw Failure("AirMic 장치 등록 실패"); // DIF_REGISTERDEVICE
                created = true;
            }
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, 0, out bool reboot)) throw Failure("AirMic driver 적용 실패");
            return reboot ? "AirMic 드라이버 설치 완료. Windows를 재부팅한 뒤 장치를 새로고침하세요."
                : "AirMic 드라이버 설치 처리 완료. 장치를 새로고침하고 실제 마이크 전달을 확인하세요.";
        }
        catch (Exception error)
        {
            // Roll back only the exact device created HERE, never an existing
            // driver/device during an upgrade. Keep its package record for retry.
            string rollback = "";
            if (created && !SetupDiCallClassInstaller(5, set, ref info)) // DIF_REMOVE
                rollback = "\n새 AirMic 장치 정리도 실패했습니다. 복구/제거를 다시 실행하세요.";
            throw new InvalidOperationException(error.Message + rollback + "\nWindows 진단: %WINDIR%\\INF\\setupapi.dev.log", error);
        }
        finally { if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set); }
    }
    private static HashSet<string> RecordedPackages()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(PackageRoot)) return result;
        AppDeployment.EnsureProtectedDirectory(PackageRoot);
        foreach (string stage in Directory.EnumerateDirectories(PackageRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            AppDeployment.EnsureProtectedDirectory(stage);
            string path = Path.Combine(stage, PackageRecordName);
            if (!File.Exists(path)) continue;
            AppDeployment.EnsureNotReparseFile(path);
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Driver 설치 기록 크기 오류");
            PackageRecord record = JsonSerializer.Deserialize<PackageRecord>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 4 })
                ?? throw new InvalidDataException("Driver 설치 기록 오류");
            if (record.SchemaVersion != 1 || !IsOemInf(record.PublishedInf)) throw new InvalidDataException("Driver 설치 기록의 경로 오류");
            result.Add(record.PublishedInf);
        }
        return result;
    }
    public static string UninstallAirMicDevices()
    {
        if (!AppDeployment.IsAdministrator) throw new UnauthorizedAccessException("AirMic 제거에는 관리자 승인이 필요합니다.");
        HashSet<string> published = RecordedPackages(); // Validate recovery records BEFORE any removal.
        var discovered = new List<string>();
        Guid category = MediaClass;
        IntPtr set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, 0); // Includes non-present MEDIA devices.
        if (set == new IntPtr(-1)) throw Failure("오디오 장치 조회 실패");
        bool restart = false;
        try
        {
            byte[] buffer = new byte[8192];
            // Snapshot before removals, which can otherwise change enumeration indexes.
            var devices = new List<DeviceInfo>();
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) break; throw Failure("장치 열거 실패"); }
                if (!IsAirMicDevice(set, ref info, buffer)) continue;
                if (SetupDiGetDeviceRegistryProperty(set, ref info, 9, out uint type, buffer, buffer.Length, out int used) && type == 1) // SPDRP_DRIVER
                {
                    string keyName = Encoding.Unicode.GetString(buffer, 0, Math.Min(used, buffer.Length)).TrimEnd('\0');
                    if (!Regex.IsMatch(keyName, @"^\{4d36e96c-e325-11ce-bfc1-08002be10318\}\\[0-9]{4,}$", RegexOptions.IgnoreCase))
                        throw new InvalidDataException("예상하지 못한 AirMic class key");
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + keyName);
                    if (key?.GetValue("InfPath") is string name && IsOemInf(name) && published.Add(name)) discovered.Add(name);
                }
                devices.Add(info);
            }
            foreach (string name in discovered)
            {
                AppDeployment.EnsureProtectedDirectory(PackageRoot);
                string recovery = Path.Combine(PackageRoot, Guid.NewGuid().ToString("N"));
                AppDeployment.EnsureProtectedDirectory(recovery);
                File.WriteAllText(Path.Combine(recovery, PackageRecordName), JsonSerializer.Serialize(new PackageRecord(1, name)));
            }
            foreach (DeviceInfo found in devices)
            {
                DeviceInfo info = found;
                if (!DiUninstallDevice(IntPtr.Zero, set, ref info, 0, out bool reboot)) throw Failure("AirMic 장치 제거 실패");
                restart |= reboot;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        foreach (string name in published)
        {
            string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", name);
            if (!File.Exists(inf)) continue;
            if (!File.ReadAllText(inf).Contains(HardwareId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AirMic가 아닌 드라이버 패키지는 제거하지 않습니다.");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("/delete-driver"); info.ArgumentList.Add(name); // NEVER /force or /uninstall other devices.
            using Process process = Process.Start(info) ?? throw new IOException("Windows 제거 도구 시작 실패");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); // Cannot safely cancel a PnP/store operation halfway. Runs off UI/audio threads.
            string message = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            if (process.ExitCode == 3010) restart = true;
            else if (process.ExitCode != 0) throw new InvalidOperationException("AirMic 패키지 제거 실패; 재부팅 후 다시 제거하세요: " + message);
        }
        return restart ? "전용 driver 제거 후 Windows 재부팅이 필요합니다." : "AirMic 전용 driver 제거 처리가 끝났습니다.";
    }
    private static bool IsAirMicDevice(IntPtr set, ref DeviceInfo info, byte[] ids)
    {
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, 1, out uint type, ids, ids.Length, out int used)
            || type != 7 || used < 0 || used > ids.Length || (used & 1) != 0) return false;
        return Encoding.Unicode.GetString(ids, 0, used).Split('\0').Any(v => v.Equals(HardwareId, StringComparison.OrdinalIgnoreCase));
    }
    private static bool DeviceExists()
    {
        Guid category = MediaClass;
        IntPtr set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, 2); // Present MEDIA devices.
        if (set == new IntPtr(-1)) throw Failure("오디오 장치 조회 실패");
        try
        {
            byte[] ids = new byte[8192];
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) return false; throw Failure("장치 열거 실패"); }
                if (IsAirMicDevice(set, ref info, ids)) return true;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    private static Win32Exception Failure(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new(code, operation + $" ({code}): " + new Win32Exception(code).Message);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupCopyOEMInfW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupCopyOEMInf(string source, string media, uint mediaType, uint style,
        StringBuilder destination, uint characters, out uint required, IntPtr component);
    [DllImport("newdev.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref DeviceInfo info, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid guid, IntPtr hwnd);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid guid, string description, IntPtr hwnd, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, byte[] bytes, int size);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint operation, IntPtr set, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, int size, out int required);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
