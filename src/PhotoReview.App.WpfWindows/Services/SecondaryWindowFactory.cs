using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Services;

/// <summary>
/// WP-19b: dung cac cua so phu WPF (Settings, Recovery, Diagnostics, Benchmark, Skipped files, Batch review, zoom tuy chon) tu
/// <see cref="IServiceProvider"/> - logic truoc day nam trong <c>WpfDialogService</c> cua App. Dung chung boi app WPF
/// (<c>WpfDialogService</c>, owner = MainWindow) va cau noi shell Win32 (<c>Shell.WpfBridge</c>, owner = HWND qua
/// <c>WindowInteropHelper</c>). Chi tao cua so; hien thi (modal/modeless, owner) do nguoi goi quyet dinh. Phai goi tren thread co
/// Dispatcher WPF cua cua so.
/// </summary>
public static class SecondaryWindowFactory
{
    public static Window CreateBatchReview(IReadOnlyList<BatchReviewItem> items)
        => new BatchReviewWindow(items);

    /// <summary>
    /// Cua so Recovery, hoac null khi khong co journal / journal doc khong duoc (da bao loi qua <paramref name="showError"/>).
    /// </summary>
    public static Window? CreateRecovery(IServiceProvider serviceProvider, Action<string, string> showError)
    {
        var journal = serviceProvider.GetService<OperationJournal>();
        var retryService = serviceProvider.GetService<RecoveryRetryService>();
        if (journal is null) return null;

        // R2-F-17: one journal pass. RV-A14: an unreadable journal is reported instead of silently doing nothing.
        var entries = TryReadRecoveryEntries(journal.ReadPendingAndFailedOperations, showError);
        if (entries is null) return null;
        Func<JournalEntry, Task<RecoveryRetryResult>>? retry = retryService is not null ? entry => retryService.RetryMoveOrCopyAsync(entry, confirmedFinishCancelled: true) : null; // the window confirms a cancelled entry explicitly first
        return new RecoveryWindow(entries, retry, dismissed => journal.Dismiss(dismissed), serviceProvider.GetService<IFileSystem>(),
            () => serviceProvider.GetService<SettingsStore>()?.Current.AllowPermanentDeleteWithoutRecycleBin == true);
    }

    /// <summary>RV-A14: the journal entries for the Recovery window, or null after telling the user why the journal could not be read.</summary>
    public static List<JournalEntry>? TryReadRecoveryEntries(Func<IReadOnlyList<JournalEntry>> read, Action<string, string> showError)
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

    public static Window CreateDiagnostics(IServiceProvider serviceProvider)
    {
        var metrics = serviceProvider.GetService<ReviewMetrics>();
        var snapshot = metrics?.Snapshot() ?? new ReviewMetrics().Snapshot();
        return new DiagnosticsWindow(snapshot, serviceProvider.GetService<LatestExplorerSnapshot>()?.Current);
    }

    /// <summary>Cua so Settings, hoac null khi khong co <c>SettingsStore</c>.</summary>
    public static Window? CreateSettings(IServiceProvider serviceProvider, SettingsTarget target)
    {
        var store = serviceProvider.GetService<SettingsStore>();
        if (store is null) return null;

        var decoderFactory = serviceProvider.GetService<IImageDecoderFactory>();
        var window = new SettingsWindow(store, decoderFactory, serviceProvider.GetService<PhotoReview.App.Localization.LocalizationService>());
        if (target == SettingsTarget.ExternalEditor) window.FocusExternalEditorOnLoad = true;
        return window;
    }

    /// <summary>RAW files are not benchmarked (the executor has no RAW-routed decoder), whatever RawSupportEnabled says.</summary>
    public static Window CreateBenchmark(string? folder) => new BenchmarkWindow(folder);

    public static Window CreateSkippedFiles(IReadOnlyList<SkippedEntry> entries) => new SkippedFilesWindow(entries);

    /// <summary>Hop thoai zoom tuy chon (Q-R43), modal; tra gia tri da kiem hop le, hoac null khi huy.</summary>
    public static int? PromptCustomZoom(Window? owner, int currentPercent)
    {
        var dialog = new ClickZoomCustomDialog(currentPercent);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    /// <summary>Như trên nhưng owner là HWND ngoài WPF (shell Win32); 0 = không owner.</summary>
    public static int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
    {
        var dialog = new ClickZoomCustomDialog(currentPercent);
        if (ownerHwnd != 0) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHwnd;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
