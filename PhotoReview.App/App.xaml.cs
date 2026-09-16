using System.Windows;
using System.IO;

namespace PhotoReview.App;

public partial class App : System.Windows.Application
{
    private InstanceLock? _instanceLock;
    private void App_Startup(object sender, StartupEventArgs e)
    {
        AppLog.Enabled = AppSettings.Load().LoggingEnabled;
        if (AppLog.Enabled) AppLog.Info($"Startup args={string.Join(" | ", e.Args)}");
        DispatcherUnhandledException += (_, a) => { AppLog.Error("Dispatcher exception", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppLog.Error("AppDomain exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Error("Unobserved task exception", a.Exception); a.SetObserved(); };
        Exit += (_, _) => { AppLog.Shutdown(); _instanceLock?.Dispose(); };
        var initial = e.Args.FirstOrDefault(arg => File.Exists(arg));
        var initialFolder = e.Args.FirstOrDefault(arg => Directory.Exists(arg));
        var lockFolder = initial is not null ? Path.GetDirectoryName(initial) : initialFolder;
        _instanceLock = new InstanceLock(lockFolder);
        if (!_instanceLock.IsOwner)
        {
            System.Windows.MessageBox.Show("Folder này đang được mở trong một Photo Review khác.", "Photo Review", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceLock.Dispose();
            _instanceLock = null;
            Shutdown();
            return;
        }
        var window = new MainWindow(initial ?? initialFolder);
        MainWindow = window;
        window.Show();
    }
}

