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
    {
        var window = new BatchReviewWindow(items)
        {
            Owner = _host.Owner
        };
        return _host.ShowDialog(window) == true;
    }

    public void ShowRecovery()
    {
        var journal = serviceProvider.GetService<OperationJournal>();
        var retryService = serviceProvider.GetService<RecoveryRetryService>();
        if (journal is null) return;

        // R2-F-17: one journal pass. RV-A14: an unreadable journal is reported instead of silently doing nothing.
        var entries = TryReadRecoveryEntries(journal.ReadPendingAndFailedOperations, ShowError);
        if (entries is null) return;
        Func<JournalEntry, Task<RecoveryRetryResult>>? retry = retryService is not null ? entry => retryService.RetryMoveOrCopyAsync(entry, confirmedFinishCancelled: true) : null; // the window confirms a cancelled entry explicitly first
        var window = new RecoveryWindow(entries, retry, dismissed => journal.Dismiss(dismissed), serviceProvider.GetService<IFileSystem>(),
            () => serviceProvider.GetService<SettingsStore>()?.Current.AllowPermanentDeleteWithoutRecycleBin == true)
        {
            Owner = _host.Owner
        };
        _host.ShowDialog(window);
    }

    /// <summary>RV-A14: the journal entries for the Recovery window, or null after telling the user why the journal could not be read.</summary>
    internal static List<JournalEntry>? TryReadRecoveryEntries(Func<IReadOnlyList<JournalEntry>> read, Action<string, string> showError)
    {
        try
        {
            return read().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not read the operation journal for the Recovery window", ex);
            showError(Tr.RecoveryTitle, Tr.RecoveryReadFailed(ex.Message));
            return null;
        }
    }

    public void ShowDiagnostics()
    {
        var metrics = serviceProvider.GetService<ReviewMetrics>();
        var snapshot = metrics?.Snapshot() ?? new ReviewMetrics().Snapshot();
        var window = new DiagnosticsWindow(snapshot, serviceProvider.GetService<LatestExplorerSnapshot>()?.Current)
        {
            Owner = _host.Owner
        };
        _host.ShowDialog(window);
    }

    public bool ShowSettings() => ShowSettings(SettingsTarget.Default);

    public bool ShowSettings(SettingsTarget target)
    {
        var store = serviceProvider.GetService<SettingsStore>();
        if (store is null) return false;

        var decoderFactory = serviceProvider.GetService<IImageDecoderFactory>();
        var window = new SettingsWindow(store, decoderFactory, serviceProvider.GetService<PhotoReview.App.Localization.LocalizationService>())
        {
            Owner = _host.Owner
        };
        if (target == SettingsTarget.ExternalEditor) window.FocusExternalEditorOnLoad = true;
        return _host.ShowDialog(window) == true;
    }

    public void ShowBenchmark(string? folder = null)
    {
        // RAW files are not benchmarked (the executor has no RAW-routed decoder), whatever RawSupportEnabled says.
        var window = new BenchmarkWindow(folder)
        {
            Owner = _host.Owner
        };
        _host.Show(window);
    }

    public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
    {
        var window = new SkippedFilesWindow(entries)
        {
            Owner = _host.Owner
        };
        _host.ShowDialog(window);
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
