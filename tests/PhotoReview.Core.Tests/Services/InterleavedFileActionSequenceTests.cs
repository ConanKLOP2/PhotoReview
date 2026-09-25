using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Services;

/// <summary>
/// Interleaved navigation/action sequences (advance first, then the file operation on the captured source path)
/// checked against the production <see cref="ReviewCatalog"/>. The former in-test List + File.Move simulations were
/// tautological and are gone; TC08a-c below replace them.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class InterleavedFileActionSequenceTests : IDisposable
{
    private readonly TempRoot _root = new("sequence");

    public void Dispose() => _root.Dispose();

    // TC08: production-path tests - the real ReviewCatalog for navigation/removal and the real FileActionService
    // (physical file system, temp-owned fake recycle bin) for the file operation.
    private PhysicalActionHarness NewActions(string name) => new(_root.Dir(name + "-data"));

    /// <summary>
    /// TC08a: Replace G1's SequenceNextThenMoveKeepsNextImage.
    /// Uses production ReviewCatalog.Remove() to simulate move, verifies catalog and selection.
    /// Mirrors old test: Next to index 1, remove it, verify current stays at index 1 (now different file).
    /// </summary>
    [Fact(DisplayName = "TC08a: Move keeps next image without skipping (production code)")]
    public async Task TC08a_MoveKeepsNextImageWithoutSkipping()
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
        Assert.EndsWith("2.jpg", catalog.Current.Path, StringComparison.OrdinalIgnoreCase);

        // Action 2: Move file at current index (2.jpg) - use Remove() which handles index adjustment
        var sourceToMove = catalog.Current.Path;
        var destPath = Path.Combine(destDir, Path.GetFileName(sourceToMove));

        var actions = NewActions("tc08a");
        var moved = await actions.Service.ExecuteAsync(new FileActionRequest(sourceToMove, FileOperationType.Move, destDir));
        Assert.True(moved.Succeeded, moved.Error);

        // Use ReviewCatalog.Remove() which automatically adjusts CurrentIndex using production formula:
        // Math.Min(Math.Max(removedIndex, 0), Count - 1)
        var newIndex = catalog.Remove(sourceToMove);

        // After move, catalog should have 4 files, and current should still be at index 1
        // which now points to 3.jpg (was at index 2)
        Assert.Equal(4, catalog.Count);
        Assert.Equal(1, newIndex);
        Assert.NotNull(catalog.Current);
        Assert.EndsWith("3.jpg", catalog.Current.Path, StringComparison.OrdinalIgnoreCase);

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
    public async Task TC08b_DeleteKeepsNextImageWithoutSkipping()
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
        Assert.EndsWith("3.jpg", catalog.Current.Path, StringComparison.OrdinalIgnoreCase);

        // Action 3: Navigate to index 3 (4.jpg) - this is the one we'll delete
        catalog.SetCurrent(3);
        Assert.Equal(3, catalog.CurrentIndex);
        Assert.NotNull(catalog.Current);
        Assert.EndsWith("4.jpg", catalog.Current.Path, StringComparison.OrdinalIgnoreCase);

        var sourceToDelete = catalog.Current.Path;

        var actions = NewActions("tc08b");
        var deleted = await actions.Service.ExecuteAsync(new FileActionRequest(sourceToDelete, FileOperationType.Recycle));
        Assert.True(deleted.Succeeded, deleted.Error);

        // Use ReviewCatalog.Remove() which handles index adjustment
        var newIndex = catalog.Remove(sourceToDelete);

        // After deletion, catalog should have 4 files
        // Current index should be clamped using production formula: Math.Min(3, 3) = 3 (in 4-item list = 5.jpg)
        Assert.Equal(4, catalog.Count);
        Assert.Equal(3, newIndex);
        Assert.NotNull(catalog.Current);
        Assert.EndsWith("5.jpg", catalog.Current.Path, StringComparison.OrdinalIgnoreCase);

        // Verify file was deleted
        Assert.False(File.Exists(sourceToDelete));
    }

    /// <summary>
    /// TC08c: Replace G1's SequenceDeleteAtEndSelectsPriorSurvivingSlot.
    /// Uses ReviewCatalog.Remove() to verify deletion at boundaries selects prior surviving slot.
    /// Mirrors old test sequence: move file 1, delete file 2, delete at end (should go back).
    /// </summary>
    [Fact(DisplayName = "TC08c: Delete at end selects prior surviving slot (production code)")]
    public async Task TC08c_DeleteAtEndSelectsPriorSurvivingSlot()
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
        Assert.EndsWith("2.jpg", catalog.Current!.Path, StringComparison.OrdinalIgnoreCase);
        var fileToMove = catalog.Current!.Path;
        var actions = NewActions("tc08c");
        Assert.True((await actions.Service.ExecuteAsync(new FileActionRequest(fileToMove, FileOperationType.Move, "moved"))).Succeeded);
        catalog.Remove(fileToMove);

        // Now at [1.jpg, 3.jpg, 4.jpg, 5.jpg], current should be at index 1 (3.jpg)
        Assert.Equal(4, catalog.Count);
        Assert.EndsWith("3.jpg", catalog.Current!.Path, StringComparison.OrdinalIgnoreCase);

        // Step 2: Navigate to index 2 and remove/delete 4.jpg
        catalog.SetCurrent(2);
        Assert.EndsWith("4.jpg", catalog.Current!.Path, StringComparison.OrdinalIgnoreCase);
        var fileToDelete1 = catalog.Current!.Path;
        Assert.True((await actions.Service.ExecuteAsync(new FileActionRequest(fileToDelete1, FileOperationType.Recycle))).Succeeded);
        catalog.Remove(fileToDelete1);

        // Now at [1.jpg, 3.jpg, 5.jpg], current should be at index 2 (5.jpg)
        // Remove at index 2 of 4 items: Math.Min(2, 3-1) = Math.Min(2, 2) = 2
        Assert.Equal(3, catalog.Count);
        Assert.EndsWith("5.jpg", catalog.Current!.Path, StringComparison.OrdinalIgnoreCase);

        // Step 3: Delete at end (index 2 of 3 items)
        var fileToDelete2 = catalog.Current!.Path;
        Assert.EndsWith("5.jpg", fileToDelete2, StringComparison.OrdinalIgnoreCase);
        Assert.True((await actions.Service.ExecuteAsync(new FileActionRequest(fileToDelete2, FileOperationType.Recycle))).Succeeded);

        // Remove at index 2 of 3 items: Math.Min(Math.Max(2, 0), 3-1) = Math.Min(2, 2) = 2
        // But then catalog count becomes 2, so index 2 becomes 1
        var newIndex = catalog.Remove(fileToDelete2);

        // After deleting last file, should have 2 files [1.jpg, 3.jpg]
        // Index should have been clamped: Math.Min(Math.Max(2, 0), 2-1) = Math.Min(2, 1) = 1
        Assert.Equal(2, catalog.Count);
        Assert.Equal(1, newIndex);
        Assert.EndsWith("3.jpg", catalog.Current!.Path, StringComparison.OrdinalIgnoreCase);
    }
}

