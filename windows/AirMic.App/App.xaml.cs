using System.Diagnostics;
using System.Windows;

namespace AirMic.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? mode = e.Args.FirstOrDefault(a => a is "--install" or "--uninstall" or "--install-driver");
        if (mode == "--install-driver") mode = "--install"; // Legacy alias now uses the complete tracked installation.
        if (mode is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (!AppDeployment.IsAdministrator)
                {
                    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    info.ArgumentList.Add(mode);
                    using Process child = Process.Start(info) ?? throw new InvalidOperationException("관리자 설치 프로세스 시작 실패");
                    await child.WaitForExitAsync(); Shutdown(child.ExitCode); return;
                }
                if (mode == "--uninstall" && MessageBox.Show(
                    "AirMic 앱과 전용 가상 오디오 장치를 제거할까요? 다른 오디오 장치는 건드리지 않고 사용자 설정은 보존합니다. Discord/게임 등에서 장치를 사용 중이면 먼저 닫아주세요.",
                    "AirMic 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { Shutdown(2); return; }
                Func<string> operation = mode == "--install" ? AppDeployment.Install : AppDeployment.Uninstall;
                string result = await Task.Run(operation);
                MessageBox.Show(result, "AirMic 설치 / 제거", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "AirMic 설치 / 제거 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        MainWindow = new MainWindow();
        if (e.Args.Contains("--smoke-ui"))
            MainWindow.Loaded += (_, _) => MainWindow.Dispatcher.BeginInvoke(new Action(() => MainWindow.Close()));
        MainWindow.Show();
    }
}
