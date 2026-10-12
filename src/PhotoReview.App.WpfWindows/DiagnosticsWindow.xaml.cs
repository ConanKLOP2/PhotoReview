using System.Windows;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

/// <summary>Read-only diagnostics list, WPF view only (WP-35 G0): the texts come from <see cref="DiagnosticsPresenter"/>.</summary>
public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(ReviewMetricsSnapshot snapshot, ExplorerViewSnapshot? explorerSnapshot = null)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        var display = DiagnosticsPresenter.Build(snapshot, explorerSnapshot);
        CacheHitsText.Text = display.CacheHits;
        CacheMissesText.Text = display.CacheMisses;
        HitRateText.Text = display.HitRate;
        SourceBytesText.Text = display.SourceBytes;
        SourceReadsText.Text = display.SourceReads;
        DecodeTimeText.Text = display.DecodeTime;
        PresentLatencyText.Text = display.PresentLatency;
        ExplorerOrderText.Text = display.ExplorerOrder;
        ExplorerMetadataText.Text = display.ExplorerMetadata;
        SourceOpensText.Text = display.SourceOpens;
        StatCountText.Text = display.StatCount;
        SessionWritesText.Text = display.SessionWrites;
        DecoderFallbacksText.Text = display.DecoderFallbacks;
        CrossThreadPresentsText.Text = display.CrossThreadPresents;
        PresentHistogramText.Text = display.PresentHistogram;
    }
}