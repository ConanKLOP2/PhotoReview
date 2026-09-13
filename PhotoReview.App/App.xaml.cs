using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;

namespace PhotoReview.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private InstanceLock? _instanceLock;
    private void App_Startup(object sender, StartupEventArgs e)
    {
        var initial = e.Args.FirstOrDefault(arg => File.Exists(arg));
        _instanceLock = new InstanceLock(initial is null ? null : Path.GetDirectoryName(initial));
        if (!_instanceLock.IsOwner)
        {
            System.Windows.MessageBox.Show("Folder này đang được mở trong một Photo Review khác.", "Photo Review", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }
}

