using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PhotoReview.App;

/// <summary>Details for one path (source or destination) of a Recovery entry, with Copy path / Show in Explorer.</summary>
public partial class RecoveryPathPanel : UserControl
{
    public RecoveryPathPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the OS message when Explorer could not be started; the owning window shows it (dialog boundary).</summary>
    internal event Action<string>? ExplorerFailed;

    internal string CurrentPath => PathBox.Text;

    internal void Show(RecoveryPathView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        TitleText.Text = view.Title;
        PathBox.Text = view.Path;
        StatusText.Text = view.StatusText;
        StatusText.Foreground = view.StatusBrush;
        NowText.Text = view.NowText;
        JournalText.Text = view.JournalText;
        NoteText.Text = view.Note ?? string.Empty;
        NoteText.Visibility = view.Note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(PathBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: the user can select the text and Ctrl+C */ }
    }

    // Read-only UI helper: opens Explorer at the file, or at the nearest folder that still exists.
    private void Show_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            var folder = RecoveryPresenter.NearestExistingFolder(path, Directory.Exists);
            if (folder is null) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ExplorerFailed?.Invoke(ex.Message);
        }
    }
}
