using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Failure injection and boundary cases for the small catalog services.</summary>
public sealed class CatalogServicesRobustnessTests
{
    [Fact(DisplayName = "CatalogEntry rejects blank paths and Matches compares length and timestamp")]
    public void CatalogEntry_Validation()
    {
        Assert.Throws<ArgumentException>(() => new CatalogEntry(""));
        Assert.Throws<ArgumentException>(() => new CatalogEntry("   "));
        Assert.Throws<ArgumentException>(() => new CatalogEntry(null!));
        var now = DateTime.UtcNow;
        var entry = new CatalogEntry(@"C:\a.jpg").WithMetadata(5, now, 10, 20);

        Assert.True(entry.Matches(new FileStat(5, now)));
        Assert.False(entry.Matches(new FileStat(6, now)));
        Assert.False(entry.Matches(new FileStat(5, now.AddTicks(1))));
    }

    [Theory(DisplayName = "ReviewMetrics: concurrent recording while Snapshot runs never throws and totals are exact")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReviewMetrics_ConcurrentRecordAndSnapshot(int seed)
    {
        var metrics = new ReviewMetrics();
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();
        var snapshotter = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested) _ = metrics.Snapshot();
            }
            catch (Exception e)
            {
                lock (errors) errors.Add(e);
            }
        });
        const int threads = 6, perThread = 6000;

        Parallel.For(0, threads, t =>
        {
            var r = new Random(seed * 100 + t);
            for (var i = 0; i < perThread; i++)
            {
                // Distinct paths beyond the cap force TrimSourceOpens while snapshots are taken.
                metrics.RecordSourceOpen(@"C:\p" + ((t * perThread) + i) + ".jpg");
                metrics.RecordSourceRead(10, r.Next(1, 50));
                metrics.RecordPresented(r.Next(0, 700));
            }
        });
        await stop.CancelAsync();
        await snapshotter;

        Assert.Empty(errors);
        var snap = metrics.Snapshot();
        Assert.Equal(threads * perThread, snap.SourceOpenCount);
        Assert.Equal(threads * perThread, snap.SourceReads);
        Assert.Equal(threads * perThread * 10L, snap.SourceBytesRead);
        Assert.Equal(threads * perThread, snap.PresentedImages);
        Assert.Equal(threads * perThread, snap.PresentHistogram.Sum(b => b.Count));
        Assert.InRange(metrics.DecodeMillisecondsEwma, 1, 50);
        Assert.True(snap.TopSourceOpens.Count <= 10);
    }

    [Fact(DisplayName = "DragDropInputService: paths with invalid characters or a NUL are ignored, not thrown on")]
    public void DragDrop_InvalidPaths_AreInvalidNotExceptions()
    {
        var result = DragDropInputService.Parse(["<>|?*", "C:\\a\0b.jpg", new string('x', 5000), "   "]);

        Assert.False(result.IsValid);
        Assert.Equal(DragDropInputKind.Invalid, result.Kind);
        Assert.Null(result.FolderPath);
    }

    [Fact(DisplayName = "DragDropInputService: null and empty input are invalid with a message")]
    public void DragDrop_NullAndEmpty()
    {
        Assert.False(DragDropInputService.Parse(null).IsValid);
        Assert.False(DragDropInputService.Parse([]).IsValid);
        Assert.NotNull(DragDropInputService.Parse([]).Warning);
    }

    [Fact(DisplayName = "ImageFileTypes: blank/extensionless/odd names, case and trailing-dot names")]
    public void ImageFileTypes_EdgeCases()
    {
        Assert.False(ImageFileTypes.IsSupported(null!));
        Assert.False(ImageFileTypes.IsSupported(""));
        Assert.False(ImageFileTypes.IsSupported("jpg"));
        Assert.False(ImageFileTypes.IsSupported(@"C:\dir.jpg\file"));
        Assert.False(ImageFileTypes.IsSupported(@"C:\a.jpg."));
        Assert.False(ImageFileTypes.IsSupported(@"C:\a.jpg "));
        Assert.True(ImageFileTypes.IsSupported(@"C:\A.JPG"));
        Assert.True(ImageFileTypes.IsSupported(@"C:\\a.TiFf"));
        Assert.True(ImageFileTypes.IsSupported(@"C:\Đ\日本.jpeg"));
    }

    [Theory(DisplayName = "BenchmarkStatistics.Percentile: boundaries, NaN-free results and monotonic in the percentile")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Percentile_Properties(int seed)
    {
        var r = new Random(seed);
        for (var round = 0; round < 200; round++)
        {
            var values = Enumerable.Range(0, r.Next(1, 40)).Select(_ => Math.Round(r.NextDouble() * 1000, 3)).ToList();
            var previous = double.MinValue;
            foreach (var p in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 1.0 })
            {
                var v = BenchmarkStatistics.Percentile(values, p);
                Assert.False(double.IsNaN(v));
                Assert.InRange(v, values.Min(), values.Max());
                Assert.True(v >= previous);
                previous = v;
            }
            Assert.Equal(values.Min(), BenchmarkStatistics.Percentile(values, 0));
            Assert.Equal(values.Max(), BenchmarkStatistics.Percentile(values, 1));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.Percentile([1.0], 1.0001));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.Percentile([1.0], double.NaN));
    }
}
