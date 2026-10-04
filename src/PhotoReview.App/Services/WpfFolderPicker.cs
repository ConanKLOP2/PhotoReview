using System.IO;
using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Services;

/// <summary><see cref="IFolderPicker"/> backed by <see cref="Microsoft.Win32.OpenFolderDialog"/>, owned by the main window.</summary>
public sealed class WpfFolderPicker : IFolderPicker
{
    private readonly IDialogHost _host;

    public WpfFolderPicker() : this(new WpfDialogHost())
    {
    }

    internal WpfFolderPicker(IDialogHost host) => _host = host;

    public string? PickFolder(string title, string? initialFolder)
        => _host.PickFolder(_host.Owner, title, UsableInitialFolder(initialFolder));

    /// <summary>The start folder only when it exists on disk; a blank or missing one lets the dialog choose.</summary>
    internal static string? UsableInitialFolder(string? initialFolder)
        => !string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder) ? initialFolder : null;
}
