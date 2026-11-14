using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirMic.App;

// Only installs the package embedded at PUBLISH time. Never downloads a driver or changes boot/security policy.
internal static class DriverInstaller
{
    private const string Resource = "AirMic.DriverPackage.zip", HardwareId = @"Root\AirMic";
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    public static bool HasEmbeddedPackage => Assembly.GetExecutingAssembly().GetManifestResourceInfo(Resource) is not null;
    private sealed record PackageManifest(int SchemaVersion, string HardwareId, string Inf, string Catalog,
        Dictionary<string, string> Files);
    public static string InstallEmbedded()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("드라이버 설치에는 관리자 승인이 필요합니다.");
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("이 개발 빌드에는 서명된 AirMic 드라이버가 없습니다. 가상 마이크 설치를 완료할 수 없습니다.");
        // Administrators/OS own Program Files. Do not execute an INF chosen from an untrusted download/temp folder.
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AirMic", "DriverPackages");
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("드라이버 경로는 reparse point일 수 없습니다.");
        string stage = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
        {
            if (zip.Entries.Count is < 4 or > 64 || zip.Entries.Sum(e => e.Length) > 64 * 1024 * 1024)
                throw new InvalidDataException("드라이버 패키지 크기/파일 수가 잘못됐습니다.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!Regex.IsMatch(entry.FullName, "^[A-Za-z0-9_.-]+$") || !names.Add(entry.FullName))
                    throw new InvalidDataException("드라이버 패키지 경로/중복 파일 오류");
                entry.ExtractToFile(Path.Combine(stage, entry.FullName), false);
            }
        }
        string manifestPath = Path.Combine(stage, "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65536) throw new InvalidDataException("드라이버 manifest 없음");
        PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 }) ?? throw new InvalidDataException("드라이버 manifest 오류");
        if (manifest.SchemaVersion != 1 || manifest.HardwareId != HardwareId || manifest.Inf != "AirMic.inf" || manifest.Catalog != "AirMic.cat"
            || manifest.Files is null || !manifest.Files.ContainsKey("AirMic.inf") || !manifest.Files.ContainsKey("AirMic.cat") || !manifest.Files.ContainsKey("AirMic.sys"))
            throw new InvalidDataException("지원하지 않는 드라이버 패키지입니다.");
        foreach (var item in manifest.Files)
        {
            if (!Regex.IsMatch(item.Key, "^[A-Za-z0-9_.-]+$")) throw new InvalidDataException("파일 이름 오류");
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage, item.Key))));
            if (!digest.Equals(item.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("드라이버 파일 해시 불일치: " + item.Key);
        }
        string inf = Path.Combine(stage, manifest.Inf);
        if (!File.ReadAllText(inf).Contains(HardwareId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("AirMic 하드웨어 ID가 없습니다.");
        var processInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        processInfo.ArgumentList.Add("/add-driver"); processInfo.ArgumentList.Add(inf);
        using (Process process = Process.Start(processInfo) ?? throw new IOException("Windows 드라이버 설치 도구 시작 실패"))
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) throw new TimeoutException("Windows 설치 작업이 진행 중일 수 있습니다. 장치 관리자에서 상태를 확인하세요.");
            string log = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(stage, "install.log"), log);
            if (process.ExitCode != 0 && process.ExitCode != 3010) throw new InvalidOperationException("Windows가 드라이버를 거부했습니다. 서명/호환성을 확인하세요.\n" + log);
        }
        bool created = false;
        IntPtr set = IntPtr.Zero;
        var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
        try
        {
            if (!DeviceExists())
            {
                Guid category = MediaClass;
                set = SetupDiCreateDeviceInfoList(ref category, IntPtr.Zero);
                if (set == new IntPtr(-1)) throw new Win32Exception();
                if (!SetupDiCreateDeviceInfo(set, "AirMic", ref category, "AirMic Virtual Audio", IntPtr.Zero, 1, ref info)) throw new Win32Exception();
                byte[] ids = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(set, ref info, 1, ids, ids.Length)) throw new Win32Exception();
                if (!SetupDiCallClassInstaller(0x19, set, ref info)) throw new Win32Exception(); // DIF_REGISTERDEVICE
                created = true;
            }
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, 0, out bool reboot)) throw new Win32Exception();
            return reboot ? "AirMic 드라이버 설치 완료. Windows를 재부팅한 뒤 장치를 새로고침하세요."
                : "AirMic 드라이버 설치 처리 완료. 장치를 새로고침하고 실제 마이크 전달을 확인하세요.";
        }
        catch
        {
            // Roll back only the exact device this invocation created, never arbitrary audio devices.
            if (created) SetupDiCallClassInstaller(5, set, ref info); // DIF_REMOVE
            throw;
        }
        finally { if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set); }
    }
    private static bool DeviceExists()
    {
        Guid category = MediaClass;
        IntPtr set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, 2); // present MEDIA devices
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            byte[] ids = new byte[8192];
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) return false; throw new Win32Exception(); }
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, 1, out _, ids, ids.Length, out int used)) continue;
                string values = Encoding.Unicode.GetString(ids, 0, Math.Min(used, ids.Length));
                if (values.Split('\0').Any(v => v.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
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
