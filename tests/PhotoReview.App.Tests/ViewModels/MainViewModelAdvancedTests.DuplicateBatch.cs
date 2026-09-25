using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Duplicate cleanup batch edge cases: nothing, one, many, partial failure, files that changed underneath.</summary>
public sealed partial class MainViewModelAdvancedTests
{
    [Fact(DisplayName = "Empty catalog: cleanup is a silent no-op that never hashes, asks or recycles")]
    public async Task RemoveDuplicatesAsync_EmptyCatalog_DoesNothing()
    {
        var (vm, _) = CreateViewModel();

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.False(_dialogService.BatchReviewCalled);
        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.Equal(0, vm.TotalFiles);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "A single image can have no duplicate: reports it and leaves everything alone")]
    public async Task RemoveDuplicatesAsync_SingleImage_ReportsNoDuplicates()
    {
        var folder = Path.Combine(_tempDir, "dup_single");
        Directory.CreateDirectory(folder);
        var only = CreateImageFile(folder, "only.png");
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.False(_dialogService.BatchReviewCalled);
        Assert.Equal(StatusFormatter.NoDuplicatesFound(), vm.StatusText);
        Assert.True(File.Exists(only));
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact(DisplayName = "Many duplicates across several groups are all recycled in one batch and the folder is reloaded")]
    public async Task RemoveDuplicatesAsync_ManyDuplicates_RecyclesEveryNumberedCopy()
    {
        var folder = Path.Combine(_tempDir, "dup_many");
        Directory.CreateDirectory(folder);
        var copies = new List<string>();
        for (var group = 0; group < 4; group++)
        {
            // Two different contents alternate so the groups are really separate.
            var bytes = group % 2 == 0 ? ValidPngBytes : DifferentPngBytes;
            CreateImageFile(folder, $"g{group}.png", bytes);
            for (var n = 1; n <= 5; n++) copies.Add(CreateImageFile(folder, $"g{group} ({n}).png", bytes));
        }
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        Assert.Equal(24, vm.TotalFiles);

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(copies.Count, _recycleBin.RecycledPaths.Count);
        Assert.All(copies, c => Assert.False(File.Exists(c)));
        Assert.Equal(4, vm.TotalFiles);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "Partial failure: a locked duplicate is reported by name, the others are still recycled, the folder is reloaded")]
    public async Task RemoveDuplicatesAsync_OneRecycleFails_ReportsItAndContinues()
    {
        var folder = Path.Combine(_tempDir, "dup_partial");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        var first = CreateImageFile(folder, "photo (1).png");
        var locked = CreateImageFile(folder, "photo (2).png");
        var third = CreateImageFile(folder, "photo (3).png");
        _recycleBin.FailingPaths.Add(locked);
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal([first, third], _recycleBin.RecycledPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.True(File.Exists(locked));
        var error = Assert.Single(_dialogService.Errors);
        Assert.Contains("photo (2).png", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("photo (1).png", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, vm.TotalFiles); // the original and the locked copy
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "Every recycle failing reports every file and recycles nothing")]
    public async Task RemoveDuplicatesAsync_AllRecyclesFail_ReportsAllAndKeepsCatalog()
    {
        var folder = Path.Combine(_tempDir, "dup_allfail");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        var a = CreateImageFile(folder, "photo (1).png");
        var b = CreateImageFile(folder, "photo (2).png");
        _recycleBin.FailingPaths.Add(a);
        _recycleBin.FailingPaths.Add(b);
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(_recycleBin.RecycledPaths);
        var error = Assert.Single(_dialogService.Errors);
        Assert.Contains("photo (1).png", error.Message, StringComparison.Ordinal);
        Assert.Contains("photo (2).png", error.Message, StringComparison.Ordinal);
        Assert.Equal(StatusFormatter.BatchDone(0, 2), vm.StatusText);
        Assert.Equal(3, vm.TotalFiles);
    }

    [Fact(DisplayName = "A file that vanished from disk before hashing never crashes the cleanup or leaves the gate held")]
    public async Task RemoveDuplicatesAsync_FileGoneBeforeHashing_DoesNotCrashOrStrandTheGate()
    {
        var folder = Path.Combine(_tempDir, "dup_gone");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png");
        CreateImageFile(folder, "photo (1).png");
        var vanishing = CreateImageFile(folder, "photo (2).png");
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        File.Delete(vanishing); // removed by another program after the scan

        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.False(vm.IsFileActionInProgress);
        Assert.DoesNotContain(vanishing, _recycleBin.RecycledPaths);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }
}
