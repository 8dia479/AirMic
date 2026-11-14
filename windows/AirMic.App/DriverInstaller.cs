using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using AirMic.Setup;
using Microsoft.Win32;

namespace AirMic.App;

// Only installs the package embedded at PUBLISH time. Never downloads a driver or changes boot/security policy.
internal static class DriverInstaller
{
    private const string Resource = "AirMic.DriverPackage.zip", HardwareId = DriverPackage.HardwareId;
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    public static bool HasEmbeddedPackage => Assembly.GetExecutingAssembly().GetManifestResourceInfo(Resource) is not null;
    public static string InstallEmbedded()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("드라이버 설치에는 관리자 승인이 필요합니다.");
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("이 개발 빌드에는 서명된 AirMic 드라이버가 없습니다. 가상 마이크 설치를 완료할 수 없습니다.");
        DriverPackage package = DriverPackage.Read(input); // Validate EVERYTHING before privileged disk/PnP changes.
        string parent = Path.Combine(AppDeployment.ProductRoot, "DriverPackages");
        AppDeployment.EnsureProtectedDirectory(parent);
        string stage = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        foreach (var file in package.Files)
        {
            using var output = new FileStream(Path.Combine(stage, file.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(file.Value.Span);
        }
        string inf = Path.Combine(stage, DriverPackage.Inf);
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
    public static string UninstallAirMicDevices()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("AirMic 제거에는 관리자 승인이 필요합니다.");
        Guid category = MediaClass;
        IntPtr set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, 0); // Includes non-present AirMic devices.
        if (set == new IntPtr(-1)) throw new Win32Exception();
        bool restart = false;
        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            byte[] buffer = new byte[8192];
            // Snapshot device structs before removals, which can otherwise change enumeration indexes.
            var devices = new List<DeviceInfo>();
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception(); }
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, 1, out uint type, buffer, buffer.Length, out int used) || type != 7) continue;
                if (!Encoding.Unicode.GetString(buffer, 0, Math.Min(used, buffer.Length)).Split('\0').Any(v => v.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;
                if (SetupDiGetDeviceRegistryProperty(set, ref info, 9, out _, buffer, buffer.Length, out used)) // SPDRP_DRIVER
                {
                    string keyName = Encoding.Unicode.GetString(buffer, 0, Math.Min(used, buffer.Length)).TrimEnd('\0');
                    // Windows owns this class key, selected from an exact Root\AirMic hardware-ID match.
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + keyName);
                    if (key?.GetValue("InfPath") is string name && Regex.IsMatch(name, @"^oem[0-9]+\.inf$", RegexOptions.IgnoreCase)) published.Add(name);
                }
                devices.Add(info);
            }
            foreach (DeviceInfo found in devices)
            {
                DeviceInfo info = found;
                if (!DiUninstallDevice(IntPtr.Zero, set, ref info, 0, out bool reboot)) throw new Win32Exception();
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
            info.ArgumentList.Add("/delete-driver"); info.ArgumentList.Add(name); // Never /force, never other drivers.
            using Process process = Process.Start(info) ?? throw new IOException("Windows 제거 도구 시작 실패");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) throw new TimeoutException("Windows driver 제거가 진행 중일 수 있습니다.");
            string message = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            if (process.ExitCode == 3010) restart = true;
            else if (process.ExitCode != 0) throw new InvalidOperationException("AirMic 패키지 제거 실패: " + message);
        }
        return restart ? "전용 driver 제거 후 Windows 재부팅이 필요합니다." : "AirMic 전용 driver 제거 처리가 끝났습니다.";
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
