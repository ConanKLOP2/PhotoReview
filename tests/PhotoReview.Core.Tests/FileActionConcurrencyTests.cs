using System.IO;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests;

/// <summary>
/// Production-path probes for file actions racing the decoder's open read stream: the real
/// <see cref="FileActionService"/> and <see cref="OperationJournal"/> run on the physical file system while a
/// delete-sharing read handle (what the production decoder opens) is held on the source. The recycle bin is a
/// temp-owned fake, so nothing here can reach the user's Recycle Bin.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FileActionConcurrencyTests : IDisposable
{
    private readonly TempRoot _root = new("file-action");
    private readonly PhysicalActionHarness _actions;

    public FileActionConcurrencyTests() => _actions = new PhysicalActionHarness(_root.Dir("data"));

    public void Dispose() => _root.Dispose();

    private string NewCase(string name) => _root.Dir(Path.Combine("file-action-tests", name));

    private static FileStream OpenLikeDecoder(string path, int bufferSize = 64 * 1024) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);

    [Fact(DisplayName = "Move succeeds immediately while decoder keeps a delete-sharing read handle")]
    public async Task MoveSucceedsWhileReadIsOpen()
    {
        var dir = NewCase("move-read");
        var source = Path.Combine(dir, "01.jpg");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = OpenLikeDecoder(source);

        var result = await _actions.Service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, "moved"));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(source));
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(dir, "moved", "01.jpg")).Length);
        Assert.Contains(_actions.Journal.ReadCommittedMoves(), m => m.Source == source);
    }

    [Fact(DisplayName = "Delete (recycle) succeeds immediately while decoder keeps a delete-sharing read handle")]
    public async Task DeleteSucceedsWhileReadIsOpen()
    {
        var dir = NewCase("delete-read");
        var source = Path.Combine(dir, "01.jpg");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = OpenLikeDecoder(source);

        var result = await _actions.Service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Recycle));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([source], _actions.Recycled);
        Assert.False(File.Exists(source));
    }

    [Fact(DisplayName = "Copy preserves source and bytes while decoder is reading")]
    public async Task CopyPreservesSourceAndBytesWhileReadIsOpen()
    {
        var dir = NewCase("copy-read");
        var source = Path.Combine(dir, "01.jpg");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        using var read = OpenLikeDecoder(source, 4096);

        var result = await _actions.Service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "copy"));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(source));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(dir, "copy", "01.jpg")));
    }

    [Fact(DisplayName = "Move/Delete/Copy sequence through the service leaves deterministic filesystem state")]
    public async Task InterleavedSequenceLeavesDeterministicState()
    {
        var dir = NewCase("sequence-read");
        var files = Enumerable.Range(1, 5).Select(i => Path.Combine(dir, $"{i:00}.jpg")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [1, 2]);

        using (OpenLikeDecoder(files[0], 4096))
            Assert.True((await _actions.Service.ExecuteAsync(new FileActionRequest(files[0], FileOperationType.Move, "moved"))).Succeeded);
        Assert.True((await _actions.Service.ExecuteAsync(new FileActionRequest(files[1], FileOperationType.Recycle))).Succeeded);
        Assert.True((await _actions.Service.ExecuteAsync(new FileActionRequest(files[2], FileOperationType.Copy, "copy"))).Succeeded);

        var survivors = Directory.EnumerateFiles(dir, "*.jpg", SearchOption.AllDirectories)
            .Select(p => Path.GetFileName(p)!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(["01.jpg", "03.jpg", "03.jpg", "04.jpg", "05.jpg"], survivors);
        Assert.Empty(Directory.EnumerateFiles(dir, "*.retry", SearchOption.AllDirectories));
        Assert.Empty(_actions.Journal.ReadPendingOperations());
    }
}
