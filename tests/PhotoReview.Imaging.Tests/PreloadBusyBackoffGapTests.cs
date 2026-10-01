using System.Reflection;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>RV-T50 (backoff part): Clear mid-cooldown and concurrent pass counting.</summary>
public sealed class PreloadBusyBackoffGapTests
{
    private const string Path = @"C:\busy\gap.cr2";
    private static readonly DateTime Written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImageCacheKey Key(long length) =>
        ImageCacheKey.Create(new CatalogEntry(Path).WithMetadata(length, Written), false, new DecodeBox(100, 100));

    [Fact]
    public void Clear_MidCooldown_ForgetsThePathAndRestartsItsBusyCount()
    {
        var backoff = new PreloadBusyBackoff();
        backoff.BeginPass();
        backoff.NoteBusy(Path, Key(1));
        backoff.BeginPass();
        Assert.True(backoff.ShouldSkip(Path, Key(1))); // cooling down

        backoff.Clear();

        Assert.False(backoff.ShouldSkip(Path, Key(1)));
        Assert.Equal(1, backoff.NoteBusy(Path, Key(1)));
    }

    [Fact]
    public async Task BeginPassAndNoteBusy_Concurrent_LoseNoPassAndKeepPerPathCounts()
    {
        const int threads = 8;
        const int perThread = 20_000;
        var backoff = new PreloadBusyBackoff();
        var counts = new int[threads];

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            var path = @"C:\busy\t" + t + ".cr2";
            var key = ImageCacheKey.Create(new CatalogEntry(path).WithMetadata(1, Written), false, new DecodeBox(100, 100));
            for (var i = 0; i < perThread; i++)
            {
                backoff.BeginPass();
                counts[t] = backoff.NoteBusy(path, key);
            }
        }, TaskCreationOptions.LongRunning)));

        Assert.All(counts, c => Assert.Equal(perThread, c)); // each path's running count is exact
        var pass = (int)typeof(PreloadBusyBackoff).GetField("_pass", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backoff)!;
        Assert.Equal(threads * perThread, pass); // no lost increment
    }
}