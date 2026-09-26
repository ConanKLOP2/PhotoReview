using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    [Fact(DisplayName = "Esc through the view model while the duplicate check hashes cancels it: nothing is recycled and the status says so (Q-R25)")]
    public async Task CancelDuplicateCheck_WhileHashing_RecyclesNothing()
    {
        var folder = Path.Combine(_tempDir, "dup_cancel_vm");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "photo.png", ValidPngBytes);
        CreateImageFile(folder, "photo (1).png", ValidPngBytes);
        MainViewModel? vm = null;
        var consumed = new List<bool>();
        var hookFs = new StatHookFileSystem(_fileSystem, () => consumed.Add(vm!.CancelDuplicateCheck()));
        (vm, _) = CreateViewModel(hookFs);
        await vm.OpenFolderAsync(folder);
        hookFs.Armed = true;
        _dialogService.BatchReviewResponse = true;

        Assert.False(vm.CancelDuplicateCheck()); // idle: Esc keeps its normal meaning (exit fullscreen / close)
        await vm.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal([true], consumed.Take(1));   // hashing was running when Esc arrived
        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.False(_dialogService.BatchReviewCalled);
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusDuplicateCheckCanceled, vm.StatusText);
        Assert.False(vm.IsFileActionInProgress);
        Assert.False(vm.CancelDuplicateCheck());
    }

    private sealed class StatHookFileSystem(PhotoReview.Core.Abstractions.IFileSystem inner, Action onFirstStat) : PhotoReview.Core.Abstractions.IFileSystem
    {
        private int _statted;
        public bool Armed { get; set; }
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public PhotoReview.Core.Abstractions.FileStat? GetFileStat(string path)
        {
            if (Armed && Interlocked.Increment(ref _statted) == 1) onFirstStat();
            return inner.GetFileStat(path);
        }
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<(string Path, PhotoReview.Core.Abstractions.FileStat? Stat)> EnumerateFilesWithStat(string directory, string pattern = "*") => inner.EnumerateFilesWithStat(directory, pattern);
        public IEnumerable<(string Path, PhotoReview.Core.Abstractions.FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<PhotoReview.Core.Abstractions.SkippedEntry> onSkipped) =>
            inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }
}
