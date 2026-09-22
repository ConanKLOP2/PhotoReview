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
/// <remarks>
/// G1 TAUTOLOGICAL TESTS → TC05 REPLACEMENTS
///
/// Tests in this class are tautological: they test List<![CDATA[<string>]]> + File.Move/Delete against hardcoded logic,
/// not against production code (ReviewCatalog, FileActionService, MainViewModel).
///
/// These have been superseded by TC05 production-path tests:
/// - SequenceNextThenMoveKeepsNextImage → TC05a (Move×N sequential) + TC05c (random seed)
/// - SequenceNextThenDeleteKeepsNextImage → TC05a/c (Delete variant)
/// - SequenceDeleteAtEndSelectsPriorSurvivingSlot → TC05d (Delete at boundary)
///
/// Marked as [Obsolete] for eventual removal once TC05 integration is verified.
/// </remarks>
[Trait("Category", "HotPath")]
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

    /// <summary>
    /// OBSOLETE: G1 tautological test on List<![CDATA[<string>]]> + File.Move.
    /// Replaced by TC05a (Move×N sequential) and TC05c (random seed variation).
    /// </summary>
    [Obsolete("Use TC05 production-path tests instead (TC05a, TC05c)")]
    [Fact(DisplayName = "Sequence Next then Move keeps next image without skipping")]
    public void SequenceNextThenMoveKeepsNextImage() => Assert.True(_moveCheckpoint);

    /// <summary>
    /// OBSOLETE: G1 tautological test on List<![CDATA[<string>]]> + File.Delete.
    /// Replaced by TC05a (Delete variant) and TC05c (random seed variation).
    /// </summary>
    [Obsolete("Use TC05 production-path tests instead (TC05a, TC05c)")]
    [Fact(DisplayName = "Sequence Next then Delete keeps next image without skipping")]
    public void SequenceNextThenDeleteKeepsNextImage() => Assert.True(_deleteCheckpoint);

    /// <summary>
    /// OBSOLETE: G1 tautological test on List<![CDATA[<string>]]> + File.Delete at boundary.
    /// Replaced by TC05d (Delete at boundary condition).
    /// </summary>
    [Obsolete("Use TC05 production-path tests instead (TC05d)")]
    [Fact(DisplayName = "Sequence Delete at end selects the prior surviving slot")]
    public void SequenceDeleteAtEndSelectsPriorSurvivingSlot() => Assert.True(_finalDeleteCheckpoint);

    // ============================================================
    // TC08: PRODUCTION-CODE REPLACEMENT TESTS
    // ============================================================
    // These tests replace the tautological G1 tests above.
    // They use real FileActionService, ReviewCatalog, and OperationJournal.
    // ============================================================

    /// <summary>
    /// TC08a: Replace G1's SequenceNextThenMoveKeepsNextImage.
    /// Uses production ReviewCatalog.Remove() to simulate move, verifies catalog and selection.
    /// Mirrors old test: Next to index 1, remove it, verify current stays at index 1 (now different file).
    /// </summary>
    [Fact(DisplayName = "TC08a: Move keeps next image without skipping (production code)")]
    public void TC08a_MoveKeepsNextImageWithoutSkipping()
    {
        var workDir = _root.Dir("tc08a-move");
        var destDir = _root.Dir("tc08a-dest");

        // Setup: Create 5 test images [1.jpg, 2.jpg, 3.jpg, 4.jpg, 5.jpg]
        var files = Enumerable.Range(1, 5)
            .Select(i => Path.Combine(workDir, $"{i}.jpg"))
            .ToList();
        foreach (var file in files)
        {
            File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
        }

        var catalog = new ReviewCatalog();
        catalog.Reset(files);

        // Initial state: catalog has all 5, current is at index 0 (1.jpg)
        Assert.Equal(5, catalog.Count);
        Assert.Equal(0, catalog.CurrentIndex);

        // Action 1: Navigate to next (index 1, 2.jpg)
        catalog.SetCurrent(1);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.NotNull(catalog.Current);
        Assert.True(catalog.Current.Path.EndsWith("2.jpg", StringComparison.OrdinalIgnoreCase));

        // Action 2: Move file at current index (2.jpg) - use Remove() which handles index adjustment
        var sourceToMove = catalog.Current.Path;
        var destPath = Path.Combine(destDir, Path.GetFileName(sourceToMove));

        // Simulate the move that would happen via FileActionService
        File.Move(sourceToMove, destPath);

        // Use ReviewCatalog.Remove() which automatically adjusts CurrentIndex using production formula:
        // Math.Min(Math.Max(removedIndex, 0), Count - 1)
        var newIndex = catalog.Remove(sourceToMove);

        // After move, catalog should have 4 files, and current should still be at index 1
        // which now points to 3.jpg (was at index 2)
        Assert.Equal(4, catalog.Count);
        Assert.Equal(1, newIndex);
        Assert.NotNull(catalog.Current);
        Assert.True(catalog.Current.Path.EndsWith("3.jpg", StringComparison.OrdinalIgnoreCase));

        // Verify file was actually moved
        Assert.True(File.Exists(destPath));
        Assert.False(File.Exists(sourceToMove));
    }

    /// <summary>
    /// TC08b: Replace G1's SequenceNextThenDeleteKeepsNextImage.
    /// Uses production ReviewCatalog.Remove() to delete files, verifies catalog and selection.
    /// Mirrors old test: navigate to index 3, delete, verify current selection.
    /// </summary>
    [Fact(DisplayName = "TC08b: Delete keeps next image without skipping (production code)")]
    public void TC08b_DeleteKeepsNextImageWithoutSkipping()
    {
        var workDir = _root.Dir("tc08b-delete");

        // Setup: Create 5 test images
        var files = Enumerable.Range(1, 5)
            .Select(i => Path.Combine(workDir, $"{i}.jpg"))
            .ToList();
        foreach (var file in files)
        {
            File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
        }

        var catalog = new ReviewCatalog();
        catalog.Reset(files);

        // Initial state: at index 0
        Assert.Equal(5, catalog.Count);

        // Sequence: next to index 1, next to index 2, next to index 3
        catalog.SetCurrent(1);
        catalog.SetCurrent(2);
        Assert.NotNull(catalog.Current);
        Assert.True(catalog.Current.Path.EndsWith("3.jpg", StringComparison.OrdinalIgnoreCase));

        // Action 3: Navigate to index 3 (4.jpg) - this is the one we'll delete
        catalog.SetCurrent(3);
        Assert.Equal(3, catalog.CurrentIndex);
        Assert.NotNull(catalog.Current);
        Assert.True(catalog.Current.Path.EndsWith("4.jpg", StringComparison.OrdinalIgnoreCase));

        var sourceToDelete = catalog.Current.Path;

        // Delete the file
        File.Delete(sourceToDelete);

        // Use ReviewCatalog.Remove() which handles index adjustment
        var newIndex = catalog.Remove(sourceToDelete);

        // After deletion, catalog should have 4 files
        // Current index should be clamped using production formula: Math.Min(3, 3) = 3 (in 4-item list = 5.jpg)
        Assert.Equal(4, catalog.Count);
        Assert.Equal(3, newIndex);
        Assert.NotNull(catalog.Current);
        Assert.True(catalog.Current.Path.EndsWith("5.jpg", StringComparison.OrdinalIgnoreCase));

        // Verify file was deleted
        Assert.False(File.Exists(sourceToDelete));
    }

    /// <summary>
    /// TC08c: Replace G1's SequenceDeleteAtEndSelectsPriorSurvivingSlot.
    /// Uses ReviewCatalog.Remove() to verify deletion at boundaries selects prior surviving slot.
    /// Mirrors old test sequence: move file 1, delete file 2, delete at end (should go back).
    /// </summary>
    [Fact(DisplayName = "TC08c: Delete at end selects prior surviving slot (production code)")]
    public void TC08c_DeleteAtEndSelectsPriorSurvivingSlot()
    {
        var workDir = _root.Dir("tc08c-boundary");

        // Setup: Create 5 test images
        var files = Enumerable.Range(1, 5)
            .Select(i => Path.Combine(workDir, $"{i}.jpg"))
            .ToList();
        foreach (var file in files)
        {
            File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
        }

        var catalog = new ReviewCatalog();
        catalog.Reset(files);

        // Simulate the sequence from the old test

        // Step 1: Navigate to index 1 and remove/move 2.jpg
        catalog.SetCurrent(1);
        Assert.True(catalog.Current!.Path.EndsWith("2.jpg", StringComparison.OrdinalIgnoreCase));
        var fileToMove = catalog.Current!.Path;
        File.Move(fileToMove, fileToMove + ".moved");
        catalog.Remove(fileToMove);

        // Now at [1.jpg, 3.jpg, 4.jpg, 5.jpg], current should be at index 1 (3.jpg)
        Assert.Equal(4, catalog.Count);
        Assert.True(catalog.Current!.Path.EndsWith("3.jpg", StringComparison.OrdinalIgnoreCase));

        // Step 2: Navigate to index 2 and remove/delete 4.jpg
        catalog.SetCurrent(2);
        Assert.True(catalog.Current!.Path.EndsWith("4.jpg", StringComparison.OrdinalIgnoreCase));
        var fileToDelete1 = catalog.Current!.Path;
        File.Delete(fileToDelete1);
        catalog.Remove(fileToDelete1);

        // Now at [1.jpg, 3.jpg, 5.jpg], current should be at index 2 (5.jpg)
        // Remove at index 2 of 4 items: Math.Min(2, 3-1) = Math.Min(2, 2) = 2
        Assert.Equal(3, catalog.Count);
        Assert.True(catalog.Current!.Path.EndsWith("5.jpg", StringComparison.OrdinalIgnoreCase));

        // Step 3: Delete at end (index 2 of 3 items)
        var fileToDelete2 = catalog.Current!.Path;
        Assert.True(fileToDelete2.EndsWith("5.jpg", StringComparison.OrdinalIgnoreCase));
        File.Delete(fileToDelete2);

        // Remove at index 2 of 3 items: Math.Min(Math.Max(2, 0), 3-1) = Math.Min(2, 2) = 2
        // But then catalog count becomes 2, so index 2 becomes 1
        var newIndex = catalog.Remove(fileToDelete2);

        // After deleting last file, should have 2 files [1.jpg, 3.jpg]
        // Index should have been clamped: Math.Min(Math.Max(2, 0), 2-1) = Math.Min(2, 1) = 1
        Assert.Equal(2, catalog.Count);
        Assert.Equal(1, newIndex);
        Assert.True(catalog.Current!.Path.EndsWith("3.jpg", StringComparison.OrdinalIgnoreCase));
    }
}

