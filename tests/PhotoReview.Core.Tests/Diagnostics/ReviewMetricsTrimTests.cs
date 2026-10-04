using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

public sealed class ReviewMetricsTrimTests
{
    [Fact(DisplayName = "The per-path open table is untouched at the cap and trimmed to exactly 3/4 of the cap one path later")]
    public void SourceOpenTable_TrimsToThreeQuartersOfCap()
    {
        var metrics = new ReviewMetrics();
        var cap = ReviewMetrics.MaxTrackedSourceOpenPaths;

        for (var i = 0; i < cap; i++) metrics.RecordSourceOpen($@"C:\photos\p{i:D5}.jpg");
        Assert.Equal(cap, metrics.TrackedSourceOpenPaths);

        metrics.RecordSourceOpen(@"C:\photos\one-more.jpg");

        Assert.Equal(cap * 3 / 4, metrics.TrackedSourceOpenPaths);
        Assert.Equal(cap + 1, metrics.Snapshot().SourceOpenCount);
    }
}
