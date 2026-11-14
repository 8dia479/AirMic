using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AirMic.Core;

namespace AirMic.App;

public partial class MainWindow : Window
{
    private sealed record OutputChoice(string Label, AudioEndpoint? Endpoint)
    { public override string ToString() => Label; }
    private sealed record Settings(int Port = PacketCodec.DefaultPort, string AllowedIp = "", int Jitter = 0,
        string EndpointId = "", double Volume = 100);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirMic", "settings.json");
    private Settings settings = new();
    private UdpReceiver? receiver;
    private WasapiRenderer? renderer;
    private bool busy, closing, closed;
    private bool restartRequired;
    private bool virtualRoute;
    public MainWindow()
    {
        InitializeComponent();
        try
        {
            if (File.Exists(settingsFile) && new FileInfo(settingsFile).Length < 65536)
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile), new JsonSerializerOptions { MaxDepth = 8 }) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { Notice.Text = "설정을 읽지 못해 기본값을 사용합니다: " + e.Message; }
        Port.Text = settings.Port.ToString(CultureInfo.InvariantCulture); AllowedIp.Text = settings.AllowedIp;
        JitterMode.SelectedIndex = Math.Clamp(settings.Jitter, 0, 3);
        Volume.Value = double.IsFinite(settings.Volume) ? Math.Clamp(settings.Volume, 0, 150) : 100;
        RefreshDevices();
        timer.Tick += (_, _) => UpdateStatistics(); timer.Start();
        Closing += OnClosing;
    }
    private void RefreshDevices()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).Distinct().ToArray();
            Addresses.Text = addresses.Length == 0 ? "활성 LAN IPv4 없음" : string.Join(" / ", addresses);
            List<AudioEndpoint> render = WindowsAudio.Enumerate(false), capture = WindowsAudio.Enumerate(true);
            var choices = new List<OutputChoice> { new("수신 진단만 · 시스템 마이크 / 스피커 출력 없음", null) };
            choices.AddRange(render.Select(d => new OutputChoice((d.IsAirMic ? "[AirMic] " : "[청취 전용] ") + d.Name, d)));
            Output.ItemsSource = choices;
            Output.SelectedItem = choices.FirstOrDefault(c => c.Endpoint?.Id == settings.EndpointId)
                ?? choices.FirstOrDefault(c => c.Endpoint?.IsAirMic == true) ?? choices[0];
            bool found = render.Any(d => d.IsAirMic) && capture.Any(d => d.IsAirMic);
            DriverStatus.Text = found ? "AirMic 재생/녹음 endpoint 발견. 실제 마이크 전달은 소비 앱에서 확인하세요."
                : "가상 마이크 미준비 · AirMic 전용 드라이버/endpoint가 없습니다. 수신 진단만 가능합니다.";
            InstallDriverButton.IsEnabled = AppDeployment.CanInstall && !busy && receiver is null;
            UninstallButton.IsEnabled = AppDeployment.IsInstalled && !busy && receiver is null;
            if (!AppDeployment.CanInstall)
                DriverStatus.Text += "\n이 개발 빌드에는 정식 서명된 driver/배포 고지 설치 bundle이 없습니다.";
            if (AppDeployment.InstallationWarning is { Length: > 0 } warning) DriverStatus.Text += "\n" + warning;
        }
        catch (Exception e) { Notice.Text = "장치 조회 실패: " + e.Message; }
    }
    private static bool TryIPv4(string text, out uint value)
    {
        value = 0; string[] parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (string part in parts)
        {
            if (part.Length is < 1 or > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet)) return false;
            value = (value << 8) | octet;
        }
        return (value >> 24) is > 0 and < 224 && (value >> 24) != 127;
    }
    private void SetControls(bool running, bool changing = false)
    {
        busy = changing;
        StartButton.IsEnabled = !running && !changing && !restartRequired;
        StopButton.IsEnabled = running && !changing;
        Port.IsEnabled = AllowedIp.IsEnabled = JitterMode.IsEnabled = Output.IsEnabled = RefreshButton.IsEnabled = !running && !changing;
        InstallDriverButton.IsEnabled = !running && !changing && AppDeployment.CanInstall;
        UninstallButton.IsEnabled = !running && !changing && AppDeployment.IsInstalled;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || receiver is not null || restartRequired) return;
        if (!int.TryParse(Port.Text, out int port) || port is < 1 or > 65535)
        { Notice.Text = "포트는 1~65535 범위여야 합니다."; return; }
        uint allowed = 0;
        if (AllowedIp.Text.Trim().Length != 0 && !TryIPv4(AllowedIp.Text.Trim(), out allowed))
        { Notice.Text = "휴대폰 IPv4 주소를 확인하세요."; return; }
        var choice = Output.SelectedItem as OutputChoice;
        if (choice is null) return;
        if (choice.Endpoint is { IsAirMic: false } && MessageBox.Show(
            "일반 출력 장치는 청취용입니다. 시스템 마이크가 되지 않으며 하울링이 생길 수 있습니다. 계속할까요?",
            "AirMic 청취", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int jitterIndex = JitterMode.SelectedIndex;
        int minimum = jitterIndex switch { 2 => 4, 3 => 8, _ => 2 };
        int maximum = jitterIndex == 0 ? 8 : minimum;
        float volume = (float)(Volume.Value / 100);
        AudioEndpoint? endpoint = choice.Endpoint;
        SetControls(false, true); Notice.Text = ""; State.Text = "시작 중";
        try
        {
            var created = await Task.Run(() =>
            {
                var queue = new AudioQueue(); WasapiRenderer? audio = null;
                try
                {
                    if (endpoint is not null) audio = new(endpoint.Id, queue, minimum, maximum, volume, endpoint.IsAirMic);
                    var network = new UdpReceiver(queue, IPAddress.Any, port, allowed, enqueueAudio: endpoint is not null);
                    return (network, audio);
                }
                catch { audio?.Dispose(); throw; }
            });
            receiver = created.network; renderer = created.audio; virtualRoute = endpoint?.IsAirMic == true;
            settings = new(port, AllowedIp.Text.Trim(), jitterIndex, endpoint?.Id ?? "", Volume.Value);
            SaveSettings(); SetControls(true);
            Notice.Text = virtualRoute ? "소비 앱에서 AirMic Microphone을 선택하세요. 실제 전달은 별도 확인이 필요합니다."
                : endpoint is null ? "수신 진단 모드입니다. 다른 앱에서 마이크로 사용할 수 없습니다."
                : "청취 전용입니다. 다른 앱에 마이크로 전달하지 않습니다.";
        }
        catch (Exception error) { State.Text = "시작 실패"; Notice.Text = error.Message; SetControls(false); }
    }
    private async Task StopSession()
    {
        if (busy) return;
        SetControls(receiver is not null, true);
        UdpReceiver? network = receiver; WasapiRenderer? audio = renderer;
        try
        {
            await Task.Run(() => { try { network?.Dispose(); } finally { audio?.Dispose(); } });
            receiver = null; renderer = null; Meter.Value = 0; State.Text = "중지됨";
        }
        catch (Exception error) { restartRequired = true; Notice.Text = error.Message + " 앱을 다시 시작하세요."; }
        finally { SetControls(false); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSession();
    private void UpdateStatistics()
    {
        if (receiver is null) return;
        NetworkStats n = receiver.Stats;
        bool recent = n.LastArrivalUs != 0 && MonoClock.NowUs - (ulong)n.LastArrivalUs < SessionGate.IdleUs;
        State.Text = recent ? virtualRoute ? "마이크 경로로 수신 중" : "패킷 수신 중" : "휴대폰 대기";
        RenderStats r = renderer?.Stats ?? default;
        Meter.Value = r.PeakMilli;
        Statistics.Text = $"수신 {n.Accepted:N0}  · 잘못된 패킷 {n.Invalid}  · 송신자 거부 {n.SourceRejected}  · 타임라인 거부 {n.TimelineRejected}\n" +
            $"큐 손실 {n.QueueDrops}  · 재생 {r.Played:N0}  · PLC {r.Plc}  · 언더런 {r.Underruns}\n" +
            $"중복 {r.Duplicates}  · 지연 도착 {r.Late}  · 지터 {r.QueueBuffered}/{r.Target} 프레임  · ACK {n.Acks}\n" +
            $"장치 버퍼 {r.BufferFrames} samples  · endpoint 보고 지연 {r.EndpointLatencyMs:F1} ms (E2E 아님)";
        string? fault = n.Error.Length != 0 ? n.Error : renderer is not null && r.Error.Length != 0 ? r.Error : null;
        if (fault is not null) { State.Text = "오류 · 중지 후 다시 시작"; Notice.Text = fault; }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            string temporary = settingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, settingsFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice.Text = "설정 저장 실패: " + e.Message; }
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel is null) return;
        VolumeLabel.Text = $"출력 음량 · {e.NewValue:F0}%";
        if (renderer is not null) renderer.Volume = (float)(e.NewValue / 100);
        settings = settings with { Volume = e.NewValue };
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (!busy && receiver is null) RefreshDevices(); }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(Addresses.Text); } catch (Exception error) { Notice.Text = "복사 실패: " + error.Message; } }
    private async void InstallDriver_Click(object sender, RoutedEventArgs e) => await ChangeInstallation(false);
    private async void Uninstall_Click(object sender, RoutedEventArgs e) => await ChangeInstallation(true);
    private async Task ChangeInstallation(bool uninstall)
    {
        if (busy || receiver is not null || (uninstall ? !AppDeployment.IsInstalled : !AppDeployment.CanInstall)) return;
        SetControls(false, true);
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            info.ArgumentList.Add(uninstall ? "--uninstall" : "--install");
            using Process child = Process.Start(info) ?? throw new InvalidOperationException("설치 프로세스 시작 실패");
            await child.WaitForExitAsync();
            Notice.Text = child.ExitCode == 0 ? "설치/제거 처리가 끝났습니다. Windows 재부팅이 요구됐으면 먼저 재부팅하세요." : "설치/제거 실패 또는 취소됨";
            RefreshDevices();
        }
        catch (Exception error) { Notice.Text = "설치 시작 실패: " + error.Message; }
        finally { SetControls(false); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing || busy) return;
        closing = true; timer.Stop(); SaveSettings(); await StopSession(); closed = true; Close();
    }
}
