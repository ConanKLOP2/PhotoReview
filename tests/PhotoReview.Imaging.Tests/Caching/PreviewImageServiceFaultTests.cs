using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Disk-cache and lifecycle fault paths of PreviewImageService, in the default (non-Slow) gate: every shape of unusable
/// .pv4 entry must be a miss that is re-decoded from the source, and concurrent readers racing cache clears must all
/// still get a valid image.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceFaultTests : IDisposable
{
    private readonly TempRoot _root = new("preview-faults");
    private readonly string _source;

    public PreviewImageServiceFaultTests()
    {
        _source = Path.Combine(_root.Dir("source"), "a.png");
        File.WriteAllBytes(_source, TestImages.OpaquePng); // opaque: a translucent preview is never disk-cached
    }

    public void Dispose() => _root.Dispose();

    private static PreviewImageService NewService(ReviewMetrics metrics, string diskDir, int width = 32) =>
        new(metrics, () => false, () => width, capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: diskDir);

    /// <summary>Decodes once with a fresh service so its persist worker writes the entry, and returns that entry's path and bytes.</summary>
    private async Task<(string Path, byte[] Bytes)> WriteEntryAsync(string diskDir)
    {
        var writer = NewService(new ReviewMetrics(), diskDir);
        await writer.GetPreviewAsync(_source);
        await writer.ShutdownPersistWorkersAsync(); // returns only after the queued write completed
        Assert.True(await writer.WaitForPruneAsync(TimeSpan.FromSeconds(10)));
        var path = Assert.Single(Directory.GetFiles(diskDir, "*.pv4"));
        return (path, await File.ReadAllBytesAsync(path));
    }

    public static TheoryData<string> BrokenEntries() =>
    [
        "zero-bytes",
        "one-byte",
        "header-only",
        "cut-mid-payload",
        "bad-magic",
        "future-version",
        "orientation-0",
        "backend-255",
        "width-negative",
        "exif-length-past-eof",
        "garbage-payload",
    ];

    private static byte[] Break(string kind, byte[] valid)
    {
        var bytes = (byte[])valid.Clone();
        switch (kind)
        {
            case "zero-bytes": return [];
            case "one-byte": return [(byte)'P'];
            case "header-only": return bytes[..24];
            case "cut-mid-payload": return bytes[..(bytes.Length - Math.Max(4, bytes.Length / 3))];
            case "bad-magic": bytes[0] = (byte)'X'; break;
            case "future-version": bytes[4] = (byte)(PreviewCacheFile.CurrentVersion + 1); break;
            case "orientation-0": bytes[6] = 0; break;
            case "backend-255": bytes[5] = 255; break;
            case "width-negative": bytes[11] = 0x80; break;
            case "exif-length-past-eof": bytes[24] = 0xFF; bytes[25] = 0x03; break; // 1023: within the codec maximum, beyond the file
            case "garbage-payload": for (var i = 26; i < bytes.Length; i++) bytes[i] = (byte)(i * 31); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return bytes;
    }

    [Theory(DisplayName = "An unusable .pv4 entry of any shape is a miss: the preview is decoded from the source again and the bad entry is replaced or removed")]
    [MemberData(nameof(BrokenEntries))]
    public async Task BrokenEntry_IsAMiss(string kind)
    {
        var diskDir = _root.Dir("disk-" + kind);
        var (entry, valid) = await WriteEntryAsync(diskDir);
        var broken = Break(kind, valid);
        await File.WriteAllBytesAsync(entry, broken);

        var metrics = new ReviewMetrics();
        var reader = NewService(metrics, diskDir);
        var image = await reader.GetPreviewAsync(_source);
        await reader.ShutdownPersistWorkersAsync();
        await reader.WaitForPruneAsync(TimeSpan.FromSeconds(10));
        var snapshot = metrics.Snapshot();

        Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
        Assert.Equal(1, snapshot.SourceReads);
        Assert.Equal(0, snapshot.DiskCacheHits);
        // Whatever is at the entry path now must not be the broken bytes any more (deleted, or rewritten by the fresh decode).
        if (File.Exists(entry)) Assert.False((await File.ReadAllBytesAsync(entry)).AsSpan().SequenceEqual(broken), "the broken entry was left in place");
    }

    [Fact(DisplayName = "An entry file that another process holds open exclusively is a miss (the source is decoded), not an error")]
    public async Task LockedEntry_FallsBackToSource()
    {
        var diskDir = _root.Dir("disk-locked");
        var (entry, _) = await WriteEntryAsync(diskDir);
        await using var held = new FileStream(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var metrics = new ReviewMetrics();
        var reader = NewService(metrics, diskDir);
        var image = await reader.GetPreviewAsync(_source);
        await reader.ShutdownPersistWorkersAsync();

        Assert.True(image.PixelWidth > 0);
        Assert.Equal(1, metrics.Snapshot().SourceReads);
    }

    [Fact(DisplayName = "Readers racing ClearCache, ClearDisk and EvictCachedPath all get a valid image and the service ends quiescent")]
    public async Task ReadersRacingClears_AllGetImages()
    {
        var diskDir = _root.Dir("disk-race");
        var service = NewService(new ReviewMetrics(), diskDir);
        using var barrier = new Barrier(7);
        var stop = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var readers = Enumerable.Range(0, 6).Select(worker => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    var image = await service.GetPreviewAsync(_source);
                    if (image.PixelWidth <= 0) failures.Add("empty image");
                }
                catch (Exception ex) { failures.Add($"worker {worker}: {ex.GetType().Name}: {ex.Message}"); }
            }
        })).ToArray();
        var clearer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            var round = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                switch (round++ % 3)
                {
                    case 0: service.ClearCache(); break;
                    case 1: service.ClearDisk(); break;
                    default: service.EvictCachedPath(_source); break;
                }
            }
        });

        await Task.WhenAll(readers);
        Volatile.Write(ref stop, 1);
        await clearer;
        await service.ShutdownPersistWorkersAsync();
        await service.WaitForPruneAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(failures);
        Assert.False(service.HasInflightPreview(_source));
    }

    [Fact(DisplayName = "Threads hammering more images than the RAM cache holds always get valid images, and the cache never exceeds its byte budget")]
    public async Task LruThrash_StaysWithinBudget_AndCorrect()
    {
        var folder = _root.Dir("thrash-source");
        var files = Enumerable.Range(0, 12).Select(i =>
        {
            var path = Path.Combine(folder, $"img{i}.png");
            File.WriteAllBytes(path, TestImages.OpaquePng);
            return path;
        }).ToArray();
        var diskDir = _root.Dir("thrash-disk");
        // 32x32 previews cost 4096 bytes each; the budget holds about three.
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, capacityBytes: 3 * 4096 + 100,
            diskCacheDirectory: diskDir, disableDiskCacheOverride: true);
        using var barrier = new Barrier(8);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var rng = new Random(worker);
            barrier.SignalAndWait();
            for (var i = 0; i < 150; i++)
            {
                var image = await service.GetPreviewAsync(files[rng.Next(files.Length)]);
                if (image.PixelWidth != 32) failures.Add($"width {image.PixelWidth}");
                if (service.CacheBytes > service.CapacityBytes) failures.Add($"cache {service.CacheBytes} > budget {service.CapacityBytes}");
            }
        })));
        await service.ShutdownPersistWorkersAsync();

        Assert.Empty(failures);
        Assert.InRange(service.CacheCount, 1, 4);
    }
}
