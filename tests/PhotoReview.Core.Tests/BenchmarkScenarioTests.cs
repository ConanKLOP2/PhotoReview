using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests;

/// <summary>
/// The benchmark action scenario as one ordered interleaving driven through production code:
/// <see cref="ReviewCatalog"/> navigation advances first, then <see cref="FileActionService"/> runs the
/// Move/Delete/Copy against the captured source path while a delete-sharing read handle (the decoder's) is
/// open. The scenario runs once per test instance (xUnit constructs a fresh instance per test) and each Fact
/// asserts one checkpoint. The recycle bin is a temp-owned fake, never the real one.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class BenchmarkScenarioTests : IAsyncLifetime, IDisposable
{
    private readonly TempRoot _root = new("benchmark-scenario");
    private readonly string _folder;
    private readonly string[] _files;
    private readonly ReviewCatalog _catalog = new();
    private readonly PhysicalActionHarness _actions;

    private readonly List<FileActionResult> _results = [];
    private readonly List<int> _indexAfterEachAction = [];
    private int _navigationCount;

    public BenchmarkScenarioTests()
    {
        _folder = _root.Dir("benchmark-action-scenario");
        _files = Enumerable.Range(0, 8).Select(i => Path.Combine(_folder, $"image-{i:00}.jpg")).ToArray();
        foreach (var file in _files) File.WriteAllBytes(file, Enumerable.Repeat((byte)7, 128 * 1024).ToArray());
        _catalog.Reset(_files);
        _actions = new PhysicalActionHarness(_root.Dir("data"));
    }

    public async Task InitializeAsync()
    {
        // Navigation advances first, then the action runs on the path captured before the advance.
        await ActAsync(FileOperationType.Move, "moved", holdReadHandle: true);
        await ActAsync(FileOperationType.Recycle, null, holdReadHandle: false);
        await ActAsync(FileOperationType.Copy, "copy", holdReadHandle: true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _root.Dispose();

    private async Task ActAsync(FileOperationType operation, string? destination, bool holdReadHandle)
    {
        var captured = _catalog.Current!.Path;
        // A held handle explicitly shares Delete: the contract of the production decoder while an action starts
        // right after the catalog advances.
        using var read = holdReadHandle
            ? new FileStream(captured, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan)
            : null;

        _catalog.SetCurrent(_catalog.CurrentIndex + 1);
        _navigationCount++;
        _results.Add(await _actions.Service.ExecuteAsync(new FileActionRequest(captured, operation, destination)));
        _indexAfterEachAction.Add(_catalog.CurrentIndex);
    }

    [Fact(DisplayName = "Action scenario advances exactly once before Move while read is open")]
    public void AdvancesExactlyOnceBeforeMove()
    {
        Assert.True(_results[0].Succeeded, _results[0].Error);
        Assert.Equal(1, _indexAfterEachAction[0]);
        Assert.False(File.Exists(_files[0]));
        Assert.True(File.Exists(Path.Combine(_folder, "moved", "image-00.jpg")));
    }

    [Fact(DisplayName = "Action scenario advances exactly once before Delete")]
    public void AdvancesExactlyOnceBeforeDelete()
    {
        Assert.True(_results[1].Succeeded, _results[1].Error);
        Assert.Equal(2, _indexAfterEachAction[1]);
        Assert.Equal([_files[1]], _actions.Recycled);
        Assert.False(File.Exists(_files[1]));
    }

    [Fact(DisplayName = "Action scenario advances exactly once before Copy")]
    public void AdvancesExactlyOnceBeforeCopy()
    {
        Assert.True(_results[2].Succeeded, _results[2].Error);
        Assert.Equal(3, _indexAfterEachAction[2]);
        Assert.True(File.Exists(_files[2]));
        Assert.True(File.Exists(Path.Combine(_folder, "copy", "image-02.jpg")));
    }

    [Fact(DisplayName = "Interleaved action sequence has no double navigation")]
    public void InterleavedSequenceHasNoDoubleNavigation()
    {
        Assert.Equal(3, _navigationCount);
        Assert.Equal(3, _catalog.CurrentIndex);
        Assert.Equal(_files[3], _catalog.Current!.Path);
    }

    [Fact(DisplayName = "Action scenario performs no retry side effect")]
    public void PerformsNoRetrySideEffect()
    {
        Assert.All(_results, r => Assert.True(r.Succeeded, r.Error));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.retry", SearchOption.AllDirectories));
        Assert.Empty(_actions.Journal.ReadPendingOperations());
    }
}
