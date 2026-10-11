using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Services;

/// <summary>
/// Hiện thực IDialogService bằng hộp thoại WPF và Win32/WPF Window.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly IServiceProvider serviceProvider;
    private readonly IDialogHost _host;

    public WpfDialogService(IServiceProvider serviceProvider) : this(serviceProvider, new WpfDialogHost())
    {
    }

    internal WpfDialogService(IServiceProvider serviceProvider, IDialogHost host)
    {
        this.serviceProvider = serviceProvider;
        _host = host;
    }

    public bool ShowConfirmation(string title, string message)
        => _host.ShowMessageBox(_host.Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowMessage(string title, string message)
        => _host.ShowMessageBox(_host.Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
        => _host.ShowMessageBox(_host.Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? PickFolder(string? initialFolder = null)
        => _host.PickFolder(_host.Owner, Tr.DialogPickFolderTitle, WpfFolderPicker.UsableInitialFolder(initialFolder));

    public bool ShowBatchReview(IReadOnlyList<string> paths)
        => ShowBatchReview(paths.Select(path => new BatchReviewItem(path, null)).ToList());

    public bool ShowBatchReview(IReadOnlyList<BatchReviewItem> items)
        => ShowOwnedDialog(SecondaryWindowFactory.CreateBatchReview(items)) == true;

    public void ShowRecovery()
    {
        var window = SecondaryWindowFactory.CreateRecovery(serviceProvider, ShowError);
        if (window is not null) ShowOwnedDialog(window);
    }

    /// <summary>RV-A14: the journal entries for the Recovery window, or null after telling the user why the journal could not be read.</summary>
    internal static List<JournalEntry>? TryReadRecoveryEntries(Func<IReadOnlyList<JournalEntry>> read, Action<string, string> showError)
        => SecondaryWindowFactory.TryReadRecoveryEntries(read, showError);

    public void ShowDiagnostics() => ShowOwnedDialog(SecondaryWindowFactory.CreateDiagnostics(serviceProvider));

    public bool ShowSettings() => ShowSettings(SettingsTarget.Default);

    public bool ShowSettings(SettingsTarget target)
    {
        var window = SecondaryWindowFactory.CreateSettings(serviceProvider, target);
        return window is not null && ShowOwnedDialog(window) == true;
    }

    public void ShowBenchmark(string? folder = null)
    {
        var window = SecondaryWindowFactory.CreateBenchmark(folder);
        window.Owner = _host.Owner;
        _host.Show(window);
    }

    public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
        => ShowOwnedDialog(SecondaryWindowFactory.CreateSkippedFiles(entries));

    private bool? ShowOwnedDialog(Window window)
    {
        window.Owner = _host.Owner;
        return _host.ShowDialog(window);
    }
}

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
