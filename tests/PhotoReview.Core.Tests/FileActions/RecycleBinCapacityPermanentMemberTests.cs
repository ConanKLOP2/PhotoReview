using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Audit B (mutation gap X3): a member that is deleted permanently (its path has no Recycle Bin) never enters the bin, so its
/// size must not count against the bin's capacity of the volume. Fake bin and in-memory disk: the real Recycle Bin is never touched.
/// </summary>
public sealed class RecycleBinCapacityPermanentMemberTests
{
    private static readonly DateTime Stamp = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";   // 4 bytes, has a bin
    private const string Raw = @"C:\photos\a.cr2";    // 8 bytes, no bin: permanent

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private sealed class MixedBin(InMemoryFileSystem fs, long capacity) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string Path, long Size)> CapacityQueries { get; } = [];

        public bool CanRecycle(string path) => path != Raw;

        public bool FitsInRecycleBin(string path, long fileSize)
        {
            CapacityQueries.Add((path, fileSize));
            return fileSize <= capacity;
        }

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            fs.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            Deleted.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    [Fact]
    public async Task ExecuteGroupAsync_PermanentMemberLargerThanTheBin_DoesNotCountAgainstTheBinCapacity()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Jpeg, "jpeg", Stamp);
        fs.AddFile(Raw, "raw data", Stamp);
        var bin = new MixedBin(fs, capacity: 5); // fits the 4-byte JPEG; 4 + 8 would not fit
        var service = new FileActionService(
            new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new Clock()), fs, new Clock(), bin);

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(Jpeg, Raw), FileOperationType.Recycle, AllowPermanentDelete: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([Jpeg], bin.Recycled);
        Assert.Equal([Raw], bin.Deleted);
        var query = Assert.Single(bin.CapacityQueries); // only the bin-bound members are measured
        Assert.Equal(4, query.Size);
    }
}
