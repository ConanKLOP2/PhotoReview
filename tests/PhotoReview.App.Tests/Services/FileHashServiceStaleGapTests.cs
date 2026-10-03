using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>Cache-validity pin for <see cref="FileHashService"/> found by Stryker (mutation gap).</summary>
[Trait("Category", "HotPath")]
public sealed class FileHashServiceStaleGapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview_HashStale_" + Guid.NewGuid().ToString("N"));

    public FileHashServiceStaleGapTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task GetAsync_FileReplacedByAnotherLengthWithTheSameTimestamp_IsRehashedNotServedFromTheCache()
    {
        var path = Path.Combine(_dir, "a.bin");
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.WriteAllBytes(path, new byte[100]);
        File.SetLastWriteTimeUtc(path, stamp);
        var service = new FileHashService();
        var first = await service.GetAsync(path);

        File.WriteAllBytes(path, new byte[200]); // a copy tool preserving the modified time
        File.SetLastWriteTimeUtc(path, stamp);
        var second = await service.GetAsync(path);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task GetAsync_UnchangedFile_ReturnsTheSameHashTwice()
    {
        var path = Path.Combine(_dir, "b.bin");
        File.WriteAllBytes(path, new byte[100]);
        var service = new FileHashService();

        Assert.Equal(await service.GetAsync(path), await service.GetAsync(path));
    }
}
