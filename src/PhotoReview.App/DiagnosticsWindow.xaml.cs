using System.Windows;
using System.Globalization;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(ReviewMetricsSnapshot snapshot, ExplorerViewSnapshot? explorerSnapshot = null)
    {
        InitializeComponent();
        var culture = CultureInfo.CurrentCulture;
        var total = snapshot.CacheHits + snapshot.CacheMisses;
        CacheHitsText.Text = snapshot.CacheHits.ToString("N0", culture);
        CacheMissesText.Text = snapshot.CacheMisses.ToString("N0", culture);
        HitRateText.Text = total == 0 ? Tr.DiagnosticsNotAvailable : $"{(double)snapshot.CacheHits / total:P1}";
        SourceBytesText.Text = Tr.DiagnosticsValueBytes(snapshot.SourceBytesRead.ToString("N0", culture));
        SourceReadsText.Text = snapshot.SourceReads.ToString("N0", culture);
        DecodeTimeText.Text = Tr.UnitMilliseconds(snapshot.DecodeMilliseconds.ToString("N0", culture));
        PresentLatencyText.Text = snapshot.PresentedImages == 0
            ? Tr.DiagnosticsNotAvailable
            : Tr.DiagnosticsValuePresentLatency(
                (snapshot.PresentMilliseconds / (double)snapshot.PresentedImages).ToString("N0", culture),
                snapshot.PresentedImages.ToString("N0", culture));
        ExplorerOrderText.Text = explorerSnapshot is null
            ? Tr.DiagnosticsExplorerOrderNotQueried
            : Tr.DiagnosticsExplorerOrderStatus(
                StatusText(explorerSnapshot.Status),
                explorerSnapshot.Reason is { } reason ? ReasonText(reason) : Tr.DiagnosticsExplorerOrderItemCount(explorerSnapshot.OrderedPaths.Count));
        ExplorerMetadataText.Text = explorerSnapshot is null
            ? Tr.DiagnosticsNotAvailable
            : Tr.DiagnosticsExplorerMetadata(explorerSnapshot.SortColumns.Count, explorerSnapshot.GroupState);
        SourceOpensText.Text = snapshot.SourceOpenCount == 0
            ? "0"
            : Tr.DiagSourceOpensSummary(
                snapshot.SourceOpenCount.ToString("N0", culture),
                string.Join(Tr.DiagListSeparator, snapshot.TopSourceOpens.Select(e => Tr.DiagSourceOpensEntry(System.IO.Path.GetFileName(e.Path), e.Count))));
        StatCountText.Text = snapshot.StatCount.ToString("N0", culture);
        SessionWritesText.Text = snapshot.SessionWriteCount.ToString("N0", culture);
        DecoderFallbacksText.Text = snapshot.DecoderFallbackCount.ToString("N0", culture);
        CrossThreadPresentsText.Text = snapshot.CrossThreadPresentCount.ToString("N0", culture);
        PresentHistogramText.Text = snapshot.PresentHistogram.Count == 0 ? Tr.DiagnosticsNotAvailable : string.Join(Tr.DiagListSeparator, snapshot.PresentHistogram.Select(b => Tr.DiagHistogramBucket(b.Label, b.Count.ToString("N0", culture))));
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
