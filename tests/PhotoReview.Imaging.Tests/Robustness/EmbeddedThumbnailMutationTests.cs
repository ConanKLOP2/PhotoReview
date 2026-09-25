using System.IO;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// EmbeddedThumbnailReader is the open-folder fast path (ThumbnailCache miss): its contract is "null when there is nothing
/// usable", never an exception, because ThumbnailCache does not catch around it and a throw fails the whole thumbnail load.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class EmbeddedThumbnailMutationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbMutation-" + Guid.NewGuid().ToString("N"));

    public EmbeddedThumbnailMutationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "Mutated JPEGs with an embedded thumbnail (damage inside the APP1 segment) never make EmbeddedThumbnailReader throw")]
    public void MutatedApp1_NeverThrows()
    {
        var seed = EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 48, thumbnailSize: 16);
        var (app1Start, app1End) = (0, 0);
        for (var at = 2; at + 4 <= seed.Length && seed[at] == 0xFF; at += 2 + ((seed[at + 2] << 8) | seed[at + 3]))
        {
            if (seed[at + 1] != 0xE1) continue;
            (app1Start, app1End) = (at, at + 2 + ((seed[at + 2] << 8) | seed[at + 3]));
            break;
        }
        Assert.True(app1End > app1Start, "the fixture must carry an APP1 with the thumbnail");
        var rng = new Random(31);
        var path = Path.Combine(_dir, "t.jpg");
        var found = 0;
        var failures = new List<string>();
        for (var i = 0; i < 250; i++)
        {
            var mutant = (byte[])seed.Clone();
            var flips = rng.Next(1, 4);
            for (var f = 0; f < flips; f++) mutant[rng.Next(app1Start + 4, app1End)] = (byte)rng.Next(256);
            if (i % 5 == 0) mutant = JpegBytes.Mutate(rng, mutant);
            File.WriteAllBytes(path, mutant);
            try
            {
                if (EmbeddedThumbnailReader.TryRead(path) is { } image)
                {
                    found++;
                    Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
                }
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                if (failures.Count < 8) failures.Add($"mutant {i}: {ex.GetType().Name}: {ex.Message.Split((char)10)[0]} at {ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()}");
            }
        }

        Assert.True(failures.Count == 0, "EmbeddedThumbnailReader threw:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        Assert.True(found > 25, $"only {found} mutants still yielded a thumbnail");
    }

    [Fact(DisplayName = "A mutated cached thumbnail PNG is a miss: the entry is dropped and the thumbnail regenerated from the source, never a throw")]
    public async Task MutatedCachedPng_IsRegeneratedNotThrown()
    {
        var source = Path.Combine(_dir, "with-thumb.jpg");
        await File.WriteAllBytesAsync(source, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 48, thumbnailSize: 16));
        var diskDir = Path.Combine(_dir, "disk");
        using var cache = new PhotoReview.Imaging.Caching.ThumbnailCache(diskDir, maxRamBytes: 16 * 1024 * 1024);
        Assert.NotNull(await cache.GetAsync(source));
        var cached = Assert.Single(Directory.GetFiles(diskDir, "*.png"));
        var seed = await File.ReadAllBytesAsync(cached);
        var rng = new Random(8);
        var failures = new List<string>();
        for (var i = 0; i < 150; i++)
        {
            cache.ClearMemory();
            await File.WriteAllBytesAsync(cached, JpegBytes.Mutate(rng, seed));
            try { Assert.NotNull(await cache.GetAsync(source)); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                if (failures.Count < 8) failures.Add($"mutant {i}: {ex.GetType().Name}: {ex.Message.Split((char)10)[0]}");
            }
        }

        Assert.True(failures.Count == 0, "ThumbnailCache threw on a corrupt cached PNG:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
