using System.IO;

namespace PhotoReview.Core.Tests;

/// <summary>
/// The scenario is one ordered interleaving, so it runs once per test class instance
/// (xUnit constructs a fresh instance
/// per test) and each Fact asserts one of the original checkpoints.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class BenchmarkScenarioTests : IDisposable
{
    private readonly TempRoot _root = new("benchmark-scenario");
    private readonly string _folder;

    private readonly bool _moveCheckpoint;
    private readonly bool _deleteCheckpoint;
    private readonly bool _copyCheckpoint;
    private readonly int _navigationCount;

    public BenchmarkScenarioTests()
    {
        _folder = _root.Dir("benchmark-action-scenario");
        var files = Enumerable.Range(0, 8)
            .Select(i => Path.Combine(_folder, $"image-{i:00}.jpg")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, Enumerable.Repeat((byte)7, 128 * 1024).ToArray());

        var index = 0;
        var navigationCount = 0;
        void NextOnce()
        {
            index = Math.Min(index + 1, files.Length - 1);
            navigationCount++;
        }

        // The read handle explicitly shares Delete: this is the contract required by the
        // production decoder while Move/Delete is started after the catalog advances.
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        {
            var nextBeforeAction = navigationCount;
            NextOnce();
            var destination = Path.Combine(_folder, "moved", Path.GetFileName(files[0]));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(files[0], destination);
            _moveCheckpoint = index == 1 && navigationCount == nextBeforeAction + 1 && File.Exists(destination);
        }

        var nextBeforeDelete = navigationCount;
        NextOnce();
        File.Delete(files[1]);
        _deleteCheckpoint = index == 2 && navigationCount == nextBeforeDelete + 1 && !File.Exists(files[1]);

        var copy = Path.Combine(_folder, "copy", Path.GetFileName(files[2]));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        using (new FileStream(files[2], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        {
            var nextBeforeCopy = navigationCount;
            NextOnce();
            File.Copy(files[2], copy);
            _copyCheckpoint = index == 3 && navigationCount == nextBeforeCopy + 1 && File.Exists(copy);
        }

        _navigationCount = navigationCount;
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Action scenario advances exactly once before Move while read is open")]
    public void AdvancesExactlyOnceBeforeMove() => Assert.True(_moveCheckpoint);

    [Fact(DisplayName = "Action scenario advances exactly once before Delete")]
    public void AdvancesExactlyOnceBeforeDelete() => Assert.True(_deleteCheckpoint);

    [Fact(DisplayName = "Action scenario advances exactly once before Copy")]
    public void AdvancesExactlyOnceBeforeCopy() => Assert.True(_copyCheckpoint);

    [Fact(DisplayName = "Interleaved action sequence has no double navigation")]
    public void InterleavedSequenceHasNoDoubleNavigation() => Assert.Equal(3, _navigationCount);

    [Fact(DisplayName = "Action scenario performs no retry side effect")]
    public void PerformsNoRetrySideEffect() =>
        Assert.False(Directory.EnumerateFiles(_folder, "*.retry", SearchOption.AllDirectories).Any());
}
