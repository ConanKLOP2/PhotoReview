using System.IO;
using System.Security.Cryptography;

namespace PhotoReview.App.Tests.Services;

/// <summary>Q-R25: cancelling one caller of FileHashService must stop only that caller, and the last one must stop the read.</summary>
public sealed class FileHashServiceCancelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_HashCancel_" + Guid.NewGuid().ToString("N"));

    public FileHashServiceCancelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string BigFile()
    {
        var path = Path.Combine(_root, "big.bin");
        var bytes = new byte[48 * 1024 * 1024];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact(DisplayName = "An already-canceled token throws before any read is started")]
    public async Task GetAsync_PreCanceled_ThrowsWithoutReading()
    {
        var path = BigFile();
        var service = new FileHashService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(path, cts.Token));

        File.Delete(path); // no handle may be held by a read that was started anyway
    }

    [Fact(DisplayName = "Cancelling the only caller throws OperationCanceledException and does not poison a later request for the same file")]
    public async Task GetAsync_CancelSoleCaller_ThenRetryGivesRealHash()
    {
        var path = BigFile();
        var service = new FileHashService();
        using var cts = new CancellationTokenSource();

        var canceled = service.GetAsync(path, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        var hash = await service.GetAsync(path);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), hash);
    }

    [Fact(DisplayName = "Cancelling one of two callers of the same file does not cancel the other (shared computation)")]
    public async Task GetAsync_CancelOneOfTwoCallers_OtherStillCompletes()
    {
        var path = BigFile();
        var service = new FileHashService();
        using var cts = new CancellationTokenSource();

        var first = service.GetAsync(path, cts.Token);
        var second = service.GetAsync(path);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), await second);
    }
}
