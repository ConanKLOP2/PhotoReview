using System.Windows;

namespace PhotoReview.App;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(ReviewMetricsSnapshot snapshot)
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
    }
}
