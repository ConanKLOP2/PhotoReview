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
        DecodeTimeText.Text = $"{snapshot.DecodeMilliseconds:N0} ms";
        PresentLatencyText.Text = snapshot.PresentedImages == 0
            ? Tr.DiagnosticsNotAvailable
            : Tr.DiagnosticsValuePresentLatency(
                (snapshot.PresentMilliseconds / (double)snapshot.PresentedImages).ToString("N0", culture),
                snapshot.PresentedImages.ToString("N0", culture));
        ExplorerOrderText.Text = explorerSnapshot is null
            ? Tr.DiagnosticsExplorerOrderNotQueried
            : Tr.DiagnosticsExplorerOrderStatus(
                StatusText(explorerSnapshot.Status),
                explorerSnapshot.Reason ?? Tr.DiagnosticsExplorerOrderItemCount(explorerSnapshot.OrderedPaths.Count));
        ExplorerMetadataText.Text = explorerSnapshot is null
            ? Tr.DiagnosticsNotAvailable
            : Tr.DiagnosticsExplorerMetadata(explorerSnapshot.SortColumns.Count, explorerSnapshot.GroupState);
        SourceOpensText.Text = snapshot.SourceOpenCount == 0 ? "0" : $"{snapshot.SourceOpenCount:N0} · " + string.Join("; ", snapshot.TopSourceOpens.Select(e => $"{System.IO.Path.GetFileName(e.Path)}×{e.Count}"));
        StatCountText.Text = snapshot.StatCount.ToString("N0", culture);
        SessionWritesText.Text = snapshot.SessionWriteCount.ToString("N0", culture);
        DecoderFallbacksText.Text = snapshot.DecoderFallbackCount.ToString("N0", culture);
        CrossThreadPresentsText.Text = snapshot.CrossThreadPresentCount.ToString("N0", culture);
        PresentHistogramText.Text = snapshot.PresentHistogram.Count == 0 ? Tr.DiagnosticsNotAvailable : string.Join("  ", snapshot.PresentHistogram.Select(b => $"{b.Label}ms:{b.Count:N0}"));
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
