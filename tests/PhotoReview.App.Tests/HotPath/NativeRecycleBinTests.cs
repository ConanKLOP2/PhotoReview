using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Platform.Windows;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TC06: Native Windows Recycle Bin integration tests.
/// Blocked on Q-T4 decision (Recycle Bin testing approval); scaffold now, enable when approved.
/// Uses real WindowsRecycleBin, not mocks.
/// </summary>
[Trait("Category", "Native")]
public sealed class NativeRecycleBinTests : IAsyncLifetime
{
    private string? _testFolder;
    private readonly int _photoCount = 3; // every restore scans the real bin through Shell COM (seconds each)

    public Task InitializeAsync()
    {
        // Create temporary folder for test files
        _testFolder = Path.Combine(Path.GetTempPath(), "TC06_RecycleBin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testFolder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_testFolder is null) return Task.CompletedTask;

        // Take this test's own items out of the user's real Recycle Bin (exact folder match only), and fail
        // loudly if any survive: these tests must leave the bin exactly as they found it.
        var leftovers = TestRecycleBinCleanup.RemoveItemsDeletedFrom(_testFolder);
        if (Directory.Exists(_testFolder))
        {
            try
            {
                Directory.Delete(_testFolder, true);
            }
            catch { }
        }
        Assert.True(leftovers.Count == 0, "Recycle Bin items created by this test could not be removed: " + string.Join(", ", leftovers));
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "TC06 hygiene: cleanup removes only the items deleted from the given folder")]
    public async Task Cleanup_RemovesOnlyItemsFromTheGivenFolder()
    {
        Assert.NotNull(_testFolder);
        var otherFolder = Path.Combine(Path.GetTempPath(), "TC06_RecycleBin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherFolder);
        try
        {
            var mine = CreateTestFile(_testFolder, "mine.png", GetValidPngBytes());
            var other = CreateTestFile(otherFolder, "other.png", GetValidPngBytes());
            var bin = new WindowsRecycleBin();
            bin.SendToRecycleBin(mine);
            bin.SendToRecycleBin(other);
            Assert.Equal(1, TestRecycleBinCleanup.CountItemsDeletedFrom(_testFolder));

            Assert.Empty(TestRecycleBinCleanup.RemoveItemsDeletedFrom(_testFolder));

            Assert.Equal(0, TestRecycleBinCleanup.CountItemsDeletedFrom(_testFolder));
            Assert.Equal(1, TestRecycleBinCleanup.CountItemsDeletedFrom(otherFolder));
        }
        finally
        {
            Assert.Empty(TestRecycleBinCleanup.RemoveItemsDeletedFrom(otherFolder));
            try { Directory.Delete(otherFolder, true); } catch { }
        }
    }

    private static string CreateTestFile(string folder, string name, byte[] content)
    {
        var filePath = Path.Combine(folder, name);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    private static byte[] GetValidPngBytes()
    {
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
            0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        ];
    }

    [Fact(DisplayName = "TC06: Delete multiple files into Recycle Bin, verify restore")]
    public async Task DeleteMultipleFiles_IntoRecycleBin_CanRestore_RecycleBinFull()
    {
        Assert.NotNull(_testFolder);

        // Step 1: Create M test files in folder
        var fileList = new List<string>();
        var pngBytes = GetValidPngBytes();
        for (var i = 0; i < _photoCount; i++)
        {
            var filePath = CreateTestFile(_testFolder, $"photo_{i:D3}.png", pngBytes);
            fileList.Add(filePath);
        }

        Assert.Equal(_photoCount, fileList.Count);
        Assert.All(fileList, path => Assert.True(File.Exists(path)));

        // Step 2: Create FileActionService with real WindowsRecycleBin
        var fileSystem = new PhysicalFileSystem();
        var tempDir = Path.Combine(Path.GetTempPath(), "TC06_Journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appPaths = new AppPaths(tempDir);
            var journal = new OperationJournal(appPaths, fileSystem, new SystemClock());
            var recycleBin = new WindowsRecycleBin();
            var fileActions = new FileActionService(journal, fileSystem, new SystemClock(), recycleBin);
            var undoService = new UndoService(journal, fileSystem, recycleBin, fileActions);

            var lastWriteBeforeDelete = File.GetLastWriteTimeUtc(fileList[^1]);

            // Step 3: Delete all M files via FileActionService
            var deleteResults = new List<FileActionResult>();
            foreach (var filePath in fileList)
            {
                var request = new FileActionRequest(filePath, FileOperationType.Recycle);
                var result = await fileActions.ExecuteAsync(request);
                deleteResults.Add(result);

                // Register for undo
                undoService.Register(result);
            }

            // Verify all deletes succeeded
            Assert.All(deleteResults, result => Assert.True(result.Succeeded,
                result.Error ?? "Delete operation failed"));

            // Step 4: Verify all files are gone from original location (in Recycle Bin)
            Assert.All(fileList, path => Assert.False(File.Exists(path),
                $"File should be in Recycle Bin, not on disk: {path}"));

            // Step 5: Undo is single-level by design (UndoService keeps only the last action), so one undo
            // restores the most recent delete. A failed restore is a failure, never a silent pass.
            var last = fileList[^1];
            var undoResult = await undoService.UndoLastAsync();
            Assert.True(undoResult.Succeeded, $"Undo of the last delete failed: {undoResult.ErrorMessage}");

            // Step 6: The last file is back at its original path with its original content...
            Assert.True(File.Exists(last), $"File should be restored to original location: {last}");
            Assert.Equal(pngBytes, File.ReadAllBytes(last));
            Assert.Equal(lastWriteBeforeDelete, File.GetLastWriteTimeUtc(last));

            // ...the earlier deletes stay in the bin, and a second undo has nothing to undo.
            Assert.All(fileList.Take(_photoCount - 1), path => Assert.False(File.Exists(path), $"Earlier delete must stay deleted: {path}"));
            Assert.False((await undoService.UndoLastAsync()).Succeeded);
            Assert.Equal(_photoCount - 1, TestRecycleBinCleanup.CountItemsDeletedFrom(_testFolder));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact(DisplayName = "TC06: Rapid Delete while delete in progress - all move to Recycle Bin")]
    public async Task DeleteRapidly_WhileDeleteInProgress_AllMovedToRecycleBin()
    {
        Assert.NotNull(_testFolder);

        // Step 1: Create K=10 test files
        const int K = 10;
        var fileList = new List<string>();
        var pngBytes = GetValidPngBytes();
        for (var i = 0; i < K; i++)
        {
            var filePath = CreateTestFile(_testFolder, $"rapid_{i:D3}.png", pngBytes);
            fileList.Add(filePath);
        }

        Assert.Equal(K, fileList.Count);

        // Step 2: Create FileActionService with real WindowsRecycleBin
        var fileSystem = new PhysicalFileSystem();
        var tempDir = Path.Combine(Path.GetTempPath(), "TC06_Rapid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appPaths = new AppPaths(tempDir);
            var journal = new OperationJournal(appPaths, fileSystem, new SystemClock());
            var recycleBin = new WindowsRecycleBin();
            var fileActions = new FileActionService(journal, fileSystem, new SystemClock(), recycleBin);

            // Step 3: Queue K Delete actions rapidly
            // Create all delete tasks without awaiting them individually
            var deleteTasks = new List<Task<FileActionResult>>(K);
            foreach (var filePath in fileList)
            {
                var request = new FileActionRequest(filePath, FileOperationType.Recycle);
                var task = fileActions.ExecuteAsync(request);
                deleteTasks.Add(task);
            }

            // Step 4: Await all to complete
            var deleteResults = await Task.WhenAll(deleteTasks);

            // Step 5: Verify all K files were successfully deleted
            // Note: Due to FileActionService gate, most will be rejected immediately.
            // Retry rejected ones until all succeed.
            var unfinishedPaths = new Queue<string>();
            for (var idx = 0; idx < fileList.Count; idx++)
            {
                if (!deleteResults[idx].Succeeded)
                {
                    unfinishedPaths.Enqueue(fileList[idx]);
                }
            }

            // Retry rejected operations (those that couldn't acquire the gate)
            var maxRetries = 100;
            while (unfinishedPaths.Count > 0 && maxRetries-- > 0)
            {
                var filePath = unfinishedPaths.Dequeue();
                if (File.Exists(filePath))
                {
                    var request = new FileActionRequest(filePath, FileOperationType.Recycle);
                    var result = await fileActions.ExecuteAsync(request);
                    if (!result.Succeeded)
                    {
                        if (result.Rejected)
                        {
                            // Still busy, re-queue and try again later
                            unfinishedPaths.Enqueue(filePath);
                            await Task.Delay(5); // Small delay before retry
                        }
                        else
                        {
                            // Real error, fail the test
                            Assert.True(result.Succeeded, $"Delete failed for {filePath}: {result.Error}");
                        }
                    }
                }
            }

            // The loop can end with the queue drained on the very last allowed retry (maxRetries then reads 0 or -1),
            // so success is "nothing left unfinished", not a retry-counter comparison.
            Assert.True(unfinishedPaths.Count == 0, $"Retried too many times - {unfinishedPaths.Count} delete(s) never went through");

            // Step 6: Verify all K files are gone from original location...
            Assert.All(fileList, path => Assert.False(File.Exists(path),
                $"File should be in Recycle Bin, not on disk: {path}"));
            // ...and really landed in the Recycle Bin: a regression to a permanent delete also removes them from disk.
            Assert.Equal(K, TestRecycleBinCleanup.CountItemsDeletedFrom(_testFolder));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
