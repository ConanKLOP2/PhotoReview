using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// RV-T29: the hash service's in-flight hand-over and its post-hash validation. The windows are reached through the byte
/// cache's internal test seams (<c>AfterReadForTests</c> / <c>BeforePublishForTests</c>, set by reflection because the
/// Imaging assembly does not expose its internals to this project), so nothing depends on timing.
/// </summary>
public sealed class FileHashServiceGapTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_HashGap_" + Guid.NewGuid().ToString("N"));
    private readonly SourceBytesCache _bytes = new(4 * 1024 * 1024);
    private readonly FileHashService _service;

    public FileHashServiceGapTests()
    {
        Directory.CreateDirectory(_root);
        _service = new FileHashService(_bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Make(string name = "photo.bin")
    {
        var path = Path.Combine(_root, name);
        var data = new byte[64 * 1024];
        new Random(11).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private void SetSeam(string property, Action? action) =>
        typeof(SourceBytesCache).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_bytes, action);

    private int HashCacheCount()
    {
        var cache = typeof(FileHashService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
    }

    [Fact]
    public async Task GetAsync_WithoutAnyInterference_CachesTheHash()
    {
        var path = Make();

        var hash = await _service.GetAsync(path);

        Assert.Equal(Sha(path), hash);
        Assert.Equal(1, HashCacheCount());
    }

    [Theory]
    [InlineData("mtime")]
    [InlineData("length")]
    public async Task GetAsync_FileChangesAfterTheReadButBeforeCompletion_ThrowsLocalizedIOExceptionAndCachesNothing(string change)
    {
        var path = Make();
        SetSeam("BeforePublishForTests", () =>
        {
            if (change == "mtime") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            else File.AppendAllText(path, "grew");
            SetSeam("BeforePublishForTests", null);
        });

        var ex = await Assert.ThrowsAsync<IOException>(() => _service.GetAsync(path));

        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.Equal(Tr.ErrIoFileChangedWhileHashing(Path.GetFullPath(path)), UserFacingError.Describe(ex));
        Assert.Equal(0, HashCacheCount());
    }

    [Fact]
    public async Task GetAsync_ClearRunsWhileTheHashIsInFlight_ReturnsTheHashButDoesNotCacheIt()
    {
        var path = Make();
        SetSeam("BeforePublishForTests", () =>
        {
            _service.Clear();
            SetSeam("BeforePublishForTests", null);
        });

        var hash = await _service.GetAsync(path);

        Assert.Equal(Sha(path), hash);
        Assert.Equal(0, HashCacheCount());

        Assert.Equal(hash, await _service.GetAsync(path)); // a later request caches normally again
        Assert.Equal(1, HashCacheCount());
    }

    [Fact]
    public async Task GetAsync_SecondCallerJoinsAnEntryWhoseLastWaiterJustCancelled_RetriesAndReturnsTheRealHash()
    {
        var path = Make();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        SetSeam("AfterReadForTests", () =>
        {
            SetSeam("AfterReadForTests", null); // one-shot: the retry reads normally
            entered.TrySetResult();
            release.Wait(Bound);
            throw new OperationCanceledException(); // the cancelled computation winds up
        });
        using var firstCaller = new CancellationTokenSource();

        var first = _service.GetAsync(path, firstCaller.Token);
        await entered.Task.WaitAsync(Bound);
        firstCaller.Cancel(); // the only waiter leaves: the shared computation is cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = _service.GetAsync(path); // joins the still-registered, now cancelled entry
        release.Set();

        Assert.Equal(Sha(path), await second.WaitAsync(Bound));
    }
}
