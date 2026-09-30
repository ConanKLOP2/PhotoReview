using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>The pure pass-counting rules of <see cref="PreloadBusyBackoff"/> (no scheduler, no sleeps).</summary>
public sealed class PreloadBusyBackoffTests
{
    private const string Path = @"C:\busy\a.cr2";
    private static readonly DateTime Written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImageCacheKey Key(long length) =>
        ImageCacheKey.Create(new CatalogEntry(Path).WithMetadata(length, Written), false, new DecodeBox(100, 100));

    [Fact]
    public void ShouldSkip_NeverBusyPath_IsFalse() => Assert.False(new PreloadBusyBackoff().ShouldSkip(Path, Key(1)));

    [Fact]
    public void ShouldSkip_AfterBusy_SkipsExactlyTheCooldownPassesThenAllowsARetry()
    {
        var backoff = new PreloadBusyBackoff();
        backoff.BeginPass();
        backoff.NoteBusy(Path, Key(1));

        var skipped = new List<bool>();
        for (var pass = 0; pass < PreloadBusyBackoff.CooldownPasses + 1; pass++)
        {
            backoff.BeginPass();
            skipped.Add(backoff.ShouldSkip(Path, Key(1)));
        }

        Assert.Equal([true, true, true, false], skipped);
    }

    [Fact]
    public void ShouldSkip_AfterTheRetryCap_StaysSkippedWhateverThePass()
    {
        var backoff = new PreloadBusyBackoff();
        for (var busy = 0; busy < PreloadBusyBackoff.MaxRetries + 1; busy++)
        {
            backoff.BeginPass();
            backoff.NoteBusy(Path, Key(1));
        }

        for (var pass = 0; pass < 50; pass++) backoff.BeginPass();

        Assert.True(backoff.ShouldSkip(Path, Key(1)));
    }

    [Fact]
    public void ShouldSkip_FileChangedAfterTheCap_StartsFresh()
    {
        var backoff = new PreloadBusyBackoff();
        for (var busy = 0; busy < PreloadBusyBackoff.MaxRetries + 1; busy++) backoff.NoteBusy(Path, Key(1));

        Assert.False(backoff.ShouldSkip(Path, Key(2))); // a different length/mtime: a new file identity
        Assert.Equal(1, backoff.NoteBusy(Path, Key(2))); // and its busy count restarts, so it logs its first busy again
    }

    [Fact]
    public void NoteBusy_ReturnsTheRunningCountPerPath()
    {
        var backoff = new PreloadBusyBackoff();

        Assert.Equal([1, 2, 3], new[] { backoff.NoteBusy(Path, Key(1)), backoff.NoteBusy(Path, Key(1)), backoff.NoteBusy(Path, Key(1)) });
        Assert.Equal(1, backoff.NoteBusy(@"C:\busy\b.cr2", Key(1)));
    }
}
