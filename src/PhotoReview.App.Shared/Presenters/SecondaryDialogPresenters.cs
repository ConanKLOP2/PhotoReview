using System.Globalization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Presenters;

/// <summary>
/// WP-35 G0: the "Click zoom level > Custom..." prompt. One number in
/// [<see cref="AppSettings.MinClickZoomPercent"/>, <see cref="AppSettings.MaxClickZoomPercent"/>] (same rule and message as the
/// Settings dialog's click zoom level box). No WPF types.
/// </summary>
internal sealed class ClickZoomCustomPresenter
{
    public ClickZoomCustomPresenter(int currentPercent)
    {
        InitialText = currentPercent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The text the box starts with.</summary>
    public string InitialText { get; }

    /// <summary>The validated value once <see cref="TryAccept"/> returned null.</summary>
    public int Value { get; private set; }

    /// <summary>The localized refusal for the typed text (the dialog stays open), or null when accepted and <see cref="Value"/> is set.</summary>
    public string? TryAccept(string? text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
            || percent < AppSettings.MinClickZoomPercent || percent > AppSettings.MaxClickZoomPercent)
            return Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);
        Value = percent;
        return null;
    }
}

/// <summary>The 15 value texts of the diagnostics dialog, in the row order of its label column.</summary>
internal sealed record DiagnosticsDisplay(
    string CacheHits, string CacheMisses, string HitRate, string SourceBytes, string SourceReads, string DecodeTime,
    string PresentLatency, string ExplorerOrder, string ExplorerMetadata, string SourceOpens, string StatCount,
    string SessionWrites, string DecoderFallbacks, string PresentHistogram, string CrossThreadPresents);

/// <summary>WP-35 G0: turns a metrics snapshot into the dialog's texts (read-only dialog; no WPF types).</summary>
internal static class DiagnosticsPresenter
{
    /// <summary>Formats with the current culture, as the dialog always did.</summary>
    public static DiagnosticsDisplay Build(ReviewMetricsSnapshot snapshot, ExplorerViewSnapshot? explorerSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var culture = CultureInfo.CurrentCulture;
        var total = snapshot.CacheHits + snapshot.CacheMisses;
        return new DiagnosticsDisplay(
            CacheHits: snapshot.CacheHits.ToString("N0", culture),
            CacheMisses: snapshot.CacheMisses.ToString("N0", culture),
            HitRate: total == 0 ? Tr.DiagnosticsNotAvailable : $"{(double)snapshot.CacheHits / total:P1}",
            SourceBytes: Tr.DiagnosticsValueBytes(snapshot.SourceBytesRead.ToString("N0", culture)),
            SourceReads: snapshot.SourceReads.ToString("N0", culture),
            DecodeTime: Tr.UnitMilliseconds(snapshot.DecodeMilliseconds.ToString("N0", culture)),
            PresentLatency: snapshot.PresentedImages == 0
                ? Tr.DiagnosticsNotAvailable
                : Tr.DiagnosticsValuePresentLatency(
                    (snapshot.PresentMilliseconds / (double)snapshot.PresentedImages).ToString("N0", culture),
                    snapshot.PresentedImages.ToString("N0", culture)),
            ExplorerOrder: explorerSnapshot is null
                ? Tr.DiagnosticsExplorerOrderNotQueried
                : Tr.DiagnosticsExplorerOrderStatus(
                    StatusText(explorerSnapshot.Status),
                    explorerSnapshot.Reason is { } reason ? ReasonText(reason) : Tr.DiagnosticsExplorerOrderItemCount(explorerSnapshot.OrderedPaths.Count)),
            ExplorerMetadata: explorerSnapshot is null
                ? Tr.DiagnosticsNotAvailable
                : Tr.DiagnosticsExplorerMetadata(explorerSnapshot.SortColumns.Count, explorerSnapshot.GroupState),
            SourceOpens: snapshot.SourceOpenCount == 0
                ? "0"
                : Tr.DiagSourceOpensSummary(
                    snapshot.SourceOpenCount.ToString("N0", culture),
                    string.Join(Tr.DiagListSeparator, snapshot.TopSourceOpens.Select(e => Tr.DiagSourceOpensEntry(Path.GetFileName(e.Path), e.Count)))),
            StatCount: snapshot.StatCount.ToString("N0", culture),
            SessionWrites: snapshot.SessionWriteCount.ToString("N0", culture),
            DecoderFallbacks: snapshot.DecoderFallbackCount.ToString("N0", culture),
            PresentHistogram: snapshot.PresentHistogram.Count == 0
                ? Tr.DiagnosticsNotAvailable
                : string.Join(Tr.DiagListSeparator, snapshot.PresentHistogram.Select(b => Tr.DiagHistogramBucket(b.Label, b.Count.ToString("N0", culture)))),
            CrossThreadPresents: snapshot.CrossThreadPresentCount.ToString("N0", culture));
    }

    /// <summary>
    /// Translated text for a snapshot reason (<see cref="ExplorerReason"/> code with optional technical details);
    /// a reason without a known code is shown as-is inside a generic sentence.
    /// </summary>
    internal static string ReasonText(string reason)
    {
        var (code, d) = ExplorerReason.Parse(reason);
        string Detail(int i) => i < d.Length ? d[i] : string.Empty;
        return code switch
        {
            ExplorerReason.Timeout => Tr.DiagExplorerReasonTimeout,
            ExplorerReason.Canceled => Tr.DiagExplorerReasonCanceled,
            ExplorerReason.QueryFailed => Tr.DiagExplorerReasonQueryFailed(Detail(0)),
            ExplorerReason.ShellUnavailable => Tr.DiagExplorerReasonShellUnavailable,
            ExplorerReason.NativeViewFailed => Tr.DiagExplorerReasonNativeViewFailed(Detail(0), Detail(1)),
            ExplorerReason.NoMatchingWindow => Tr.DiagExplorerReasonNoMatchingWindow(Detail(0)),
            ExplorerReason.ComCallFailed => Tr.DiagExplorerReasonComCallFailed(Detail(0), Detail(1)),
            ExplorerReason.EmptyView => Tr.DiagExplorerReasonEmptyView(Detail(0), Detail(1)),
            ExplorerReason.EmptyPath => Tr.DiagExplorerReasonEmptyPath,
            ExplorerReason.OutsideFolder => Tr.DiagExplorerReasonOutsideFolder,
            ExplorerReason.DuplicateItem => Tr.DiagExplorerReasonDuplicateItem,
            ExplorerReason.IncompleteSnapshot => Tr.DiagExplorerReasonIncompleteSnapshot,
            _ => Tr.DiagExplorerReasonUnknown(reason),
        };
    }

    internal static string StatusText(ExplorerOrderStatus status) => status switch
    {
        ExplorerOrderStatus.Available => Tr.EnumExplorerOrderStatusAvailable,
        ExplorerOrderStatus.NoMatchingWindow => Tr.EnumExplorerOrderStatusNoMatchingWindow,
        ExplorerOrderStatus.NativeViewUnavailable => Tr.EnumExplorerOrderStatusNativeViewUnavailable,
        ExplorerOrderStatus.InvalidSnapshot => Tr.EnumExplorerOrderStatusInvalidSnapshot,
        ExplorerOrderStatus.TimedOut => Tr.EnumExplorerOrderStatusTimedOut,
        ExplorerOrderStatus.Canceled => Tr.EnumExplorerOrderStatusCanceled,
        ExplorerOrderStatus.Failed => Tr.EnumExplorerOrderStatusFailed,
        _ => status.ToString(),
    };
}

/// <summary>WP-35 G0: the lines of the batch-review confirmation list (no WPF types).</summary>
internal static class BatchReviewPresenter
{
    /// <summary>The list lines for the files, in order.</summary>
    public static List<string> BuildLines(IEnumerable<BatchReviewItem> items) => items.Select(BuildItem).ToList();

    /// <summary>The heading under the title, e.g. "N files will be processed".</summary>
    public static string Summary(int count) => Tr.BatchReviewSummary(count);

    /// <summary>The path-only form (sizes unknown): every line shows the "no longer exists" text.</summary>
    public static List<BatchReviewItem> FromPaths(IEnumerable<string> paths) => paths.Select(path => new BatchReviewItem(path, null)).ToList();

    /// <summary>One list line; an item without a known size (missing, or never stat'ed) gives the "missing" line instead of breaking the list.</summary>
    internal static string BuildItem(BatchReviewItem item) => item.Length is { } length
        ? Tr.BatchReviewItem(Path.GetFileName(item.Path), length.ToString("N0", CultureInfo.CurrentCulture), item.Path)
        : Tr.BatchReviewItemMissing(Path.GetFileName(item.Path), item.Path);
}

/// <summary>WP-35 G0: the texts of the "skipped files" list (IO05, ADR 0007 section 3; no WPF types).</summary>
internal static class SkippedFilesPresenter
{
    public static string Title => Tr.MainSkippedWindowTitle;
    public static string Intro => Tr.MainSkippedWindowIntro;
    public static string CloseText => Tr.MainSkippedWindowClose;

    /// <summary>One line per skipped entry, in order.</summary>
    public static List<string> BuildLines(IEnumerable<SkippedEntry> entries) => entries.Select(FormatEntry).ToList();

    /// <summary>"path  (reason)": the OS message stays verbatim inside a translated category sentence.</summary>
    internal static string FormatEntry(SkippedEntry entry) => Tr.SkippedEntry(entry.Path, entry.Kind switch
    {
        SkippedKind.ListingInterrupted => Tr.SkippedReasonListingInterrupted(entry.Reason),
        _ => Tr.SkippedReasonFileUnreadable(entry.Reason),
    });
}
