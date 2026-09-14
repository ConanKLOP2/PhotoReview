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
        DecodeTimeText.Text = $"{snapshot.DecodeMilliseconds:N0} ms";
    }
}
