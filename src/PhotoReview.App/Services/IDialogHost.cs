using System.Windows;

namespace PhotoReview.App.Services;

/// <summary>
/// The Win32/WPF calls behind <see cref="WpfDialogService"/> and <see cref="WpfFolderPicker"/> (message box, folder dialog,
/// modal windows, the owner window). A seam only: the real host needs a desktop and blocks on a modal loop, so the
/// title/button/owner/result decisions are tested against a fake one.
/// </summary>
internal interface IDialogHost
{
    /// <summary>The window that owns dialogs: the application's main window, or null.</summary>
    Window? Owner { get; }

    MessageBoxResult ShowMessageBox(Window? owner, string message, string title, MessageBoxButton button, MessageBoxImage image);

    /// <summary>Shows the folder dialog starting at <paramref name="initialFolder"/> (null = default) and returns the chosen folder, or null when cancelled.</summary>
    string? PickFolder(Window? owner, string title, string? initialFolder);

    bool? ShowDialog(Window window);

    void Show(Window window);
}
