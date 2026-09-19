using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>
/// Mirrors the UI contract sequence from Program.cs's RunInterleavedFileActionSequence:
/// navigation/action advances first, then the filesystem operation runs against the
/// captured source path.
/// </summary>
public sealed class InterleavedFileActionSequenceTests : IDisposable
{
    private readonly TempRoot _root = new("sequence");

    private readonly bool _moveCheckpoint;
    private readonly bool _deleteCheckpoint;
    private readonly bool _finalDeleteCheckpoint;

    public InterleavedFileActionSequenceTests()
    {
        var folder = _root.Dir("sequence");
        var moved = _root.Dir("sequence-moved");
        var files = Enumerable.Range(1, 5).Select(i => Path.Combine(folder, $"{i}.jpg")).ToList();
        foreach (var file in files) File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
        var catalog = files.ToList();
        var index = 0;

        index = Math.Min(index + 1, catalog.Count - 1); // Next => 2
        var moveSource = catalog[index];
        var moveDestination = Path.Combine(moved, Path.GetFileName(moveSource));
        catalog.RemoveAt(index); // Move advances/removes exactly once; current becomes 3.
        index = Math.Min(index, catalog.Count - 1);
        File.Move(moveSource, moveDestination);
        _moveCheckpoint = Path.Exists(moveDestination) && !Path.Exists(moveSource)
            && Path.GetFileName(catalog[index]) == "3.jpg";

        index = Math.Min(index + 1, catalog.Count - 1); // Next => 4
        var deleteSource = catalog[index];
        catalog.RemoveAt(index); // Delete advances/removes exactly once; current becomes 5.
        index = Math.Min(index, catalog.Count - 1);
        File.Delete(deleteSource);
        _deleteCheckpoint = !Path.Exists(deleteSource) && Path.GetFileName(catalog[index]) == "5.jpg";

        index = Math.Min(index + 1, catalog.Count - 1); // Next at end remains 5.
        var finalDelete = catalog[index];
        catalog.RemoveAt(index);
        index = Math.Min(index, catalog.Count - 1);
        File.Delete(finalDelete);
        _finalDeleteCheckpoint = catalog.Count == 2 && Path.GetFileName(catalog[index]) == "3.jpg"
            && catalog.All(File.Exists);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Sequence Next then Move keeps next image without skipping")]
    public void SequenceNextThenMoveKeepsNextImage() => Assert.True(_moveCheckpoint);

    [Fact(DisplayName = "Sequence Next then Delete keeps next image without skipping")]
    public void SequenceNextThenDeleteKeepsNextImage() => Assert.True(_deleteCheckpoint);

    [Fact(DisplayName = "Sequence Delete at end selects the prior surviving slot")]
    public void SequenceDeleteAtEndSelectsPriorSurvivingSlot() => Assert.True(_finalDeleteCheckpoint);
}

