using System.Windows;

namespace AirMic.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--install-driver"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                string result = await Task.Run(DriverInstaller.InstallEmbedded);
                MessageBox.Show(result, "AirMic 드라이버 설치", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "AirMic 설치 실패", MessageBoxButton.OK, MessageBoxImage.Error);
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
