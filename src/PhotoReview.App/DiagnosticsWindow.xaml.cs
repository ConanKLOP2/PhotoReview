using System.Windows;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(ReviewMetricsSnapshot snapshot, ExplorerViewSnapshot? explorerSnapshot = null)
    {
        InitializeComponent();
        var total = snapshot.CacheHits + snapshot.CacheMisses;
        CacheHitsText.Text = snapshot.CacheHits.ToString("N0");
        CacheMissesText.Text = snapshot.CacheMisses.ToString("N0");
        HitRateText.Text = total == 0 ? "N/A" : $"{(double)snapshot.CacheHits / total:P1}";
        SourceBytesText.Text = $"{snapshot.SourceBytesRead:N0} bytes";
        SourceReadsText.Text = snapshot.SourceReads.ToString("N0");
        DecodeTimeText.Text = $"{snapshot.DecodeMilliseconds:N0} ms";
        PresentLatencyText.Text = snapshot.PresentedImages == 0 ? "N/A" : $"{snapshot.PresentMilliseconds / (double)snapshot.PresentedImages:N0} ms/ảnh ({snapshot.PresentedImages:N0})";
        ExplorerOrderText.Text = explorerSnapshot is null ? "Chưa truy vấn" : $"{explorerSnapshot.Status}: {explorerSnapshot.Reason ?? $"{explorerSnapshot.OrderedPaths.Count} items"}";
        ExplorerMetadataText.Text = explorerSnapshot is null ? "N/A" : $"{explorerSnapshot.SortColumns.Count} sort column(s), group={explorerSnapshot.GroupState}";
    }
}
