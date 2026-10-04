using System.Windows;

namespace PhotoReview.App.Services;

/// <summary>Real <see cref="IDialogHost"/>: forwards to WPF/Win32 (modal, needs a desktop; not unit-testable).</summary>
// Stryker disable all : one-line forwarders to modal Win32/WPF calls; the decisions around them are tested through IDialogHost.
internal sealed class WpfDialogHost : IDialogHost
{
    public Window? Owner => System.Windows.Application.Current?.MainWindow;

    public MessageBoxResult ShowMessageBox(Window? owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        => owner is not null
            ? System.Windows.MessageBox.Show(owner, message, title, button, image)
            : System.Windows.MessageBox.Show(message, title, button, image);

    public string? PickFolder(Window? owner, string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        var result = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == true ? dialog.FolderName : null;
    }

    public bool? ShowDialog(Window window) => window.ShowDialog();

    public void Show(Window window) => window.Show();
}
