using System.IO;
using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Services;

/// <summary><see cref="IFolderPicker"/> backed by <see cref="Microsoft.Win32.OpenFolderDialog"/>, owned by the main window.</summary>
public sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == true ? dialog.FolderName : null;
    }
}
