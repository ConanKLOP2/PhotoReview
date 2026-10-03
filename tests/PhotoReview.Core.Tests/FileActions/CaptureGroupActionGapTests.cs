using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>RV-T09 gap: duplicate paths in a capture group and the per-volume Recycle Bin capacity rule across two volumes.</summary>
public sealed class CaptureGroupActionGapTests
{
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly AppPaths JournalPaths = new(@"C:\Users\test\AppData\Local");

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Each volume (drive root) has its own capacity; sending deletes the file from the fake disk.</summary>
    private sealed class PerVolumeBin(InMemoryFileSystem fs, Dictionary<string, long> capacityByRoot) : IRecycleBin
    {
        public int RecycleCalls { get; private set; }
        public void SendToRecycleBin(string path) { RecycleCalls++; fs.Delete(path); }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
        public bool FitsInRecycleBin(string path, long fileSize) => fileSize <= capacityByRoot[Path.GetPathRoot(path)!];
    }

    private static FileActionService Service(InMemoryFileSystem fs, IRecycleBin bin) =>
        new(new OperationJournal(JournalPaths, fs, new Clock()), fs, new Clock(), bin);

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinFitsEachVolumeButNotTheSumAcrossVolumes_Succeeds()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);     // 4
        fs.AddFile(@"C:\photos\a.xmp", "xmp", Stamp);      // 3  -> C: needs 7
        fs.AddFile(@"D:\photos\a.cr2", "raw data", Stamp); // 8  -> D: needs 8
        var bin = new PerVolumeBin(fs, new() { [@"C:\"] = 10, [@"D:\"] = 10 }); // 15 in total would not fit one 10-byte bin

        var result = await Service(fs, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"D:\photos\a.cr2", @"C:\photos\a.xmp"), FileOperationType.Recycle));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, bin.RecycleCalls);
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinOverflowsOnTheSecondVolume_IsRefusedBeforeAnyMutation()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fs.AddFile(@"D:\photos\a.cr2", "raw data", Stamp); // 8 > 5
        var bin = new PerVolumeBin(fs, new() { [@"C:\"] = 100, [@"D:\"] = 5 });

        var result = await Service(fs, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"D:\photos\a.cr2"), FileOperationType.Recycle));

        Assert.False(result.Succeeded);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fs.FileExists(@"C:\photos\a.jpg"));
        Assert.True(fs.FileExists(@"D:\photos\a.cr2"));
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinOverflowsOnTheFirstVolumeOnly_IsRefusedBeforeAnyMutation()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fs.AddFile(@"C:\photos\a.xmp", "xmp", Stamp);
        fs.AddFile(@"D:\photos\a.cr2", "raw data", Stamp);
        var bin = new PerVolumeBin(fs, new() { [@"C:\"] = 6, [@"D:\"] = 100 }); // C: needs 7

        var result = await Service(fs, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"D:\photos\a.cr2", @"C:\photos\a.xmp"), FileOperationType.Recycle));

        Assert.False(result.Succeeded);
        Assert.Equal(0, bin.RecycleCalls);
    }

    // Pins the CURRENT behaviour for a group listing the same path twice (a CaptureGroup does not forbid it).
    [Fact]
    public async Task ExecuteGroupAsync_MoveWithTheSamePathListedTwice_FailsAndLeavesTheFileInPlace()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        var bin = new PerVolumeBin(fs, new() { [@"C:\"] = 100 });

        var result = await Service(fs, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\A.JPG"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(@"C:\photos\a.jpg"));
        Assert.False(fs.FileExists(@"C:\photos\selected\a.jpg"));
    }
}
