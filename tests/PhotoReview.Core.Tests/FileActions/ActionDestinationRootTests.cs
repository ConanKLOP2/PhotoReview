using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>RV-T08 gap: a relative destination for a photo that lives at a drive root or a UNC share root resolves inside it.</summary>
public sealed class ActionDestinationRootTests
{
    private static readonly AppPaths JournalPaths = new(@"C:\Users\test\AppData\Local");

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);
    }

    private sealed class NoBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    [Theory]
    [InlineData(@"C:\", @"C:\sel\a.jpg")]
    [InlineData(@"C:\", @"C:\sel")]
    [InlineData(@"\\server\share", @"\\server\share\sel\a.jpg")]
    [InlineData(@"\\server\share\", @"\\server\share\sel")]
    public void ValidateNoEscapeViaReparsePoint_SourceAtRoot_DestinationInsideIsOk(string sourceFolder, string destination)
    {
        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(sourceFolder, destination, new InMemoryFileSystem());

        Assert.Equal(ActionDestinationCheck.Ok, result);
    }

    [Theory]
    [InlineData(@"C:\", @"D:\sel\a.jpg")]
    [InlineData(@"\\server\share", @"\\server\share2\sel")]
    [InlineData(@"\\server\share", @"\\other\share\sel")]
    public void ValidateNoEscapeViaReparsePoint_SourceAtRoot_DestinationOnAnotherRootEscapes(string sourceFolder, string destination)
    {
        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(sourceFolder, destination, new InMemoryFileSystem());

        Assert.Equal(ActionDestinationCheck.EscapesSourceFolder, result);
    }

    [Theory]
    [InlineData(FileOperationType.Move, @"C:\a.jpg", @"C:\sel\a.jpg")]
    [InlineData(FileOperationType.Copy, @"C:\a.jpg", @"C:\sel\a.jpg")]
    [InlineData(FileOperationType.Move, @"\\server\share\a.jpg", @"\\server\share\sel\a.jpg")]
    [InlineData(FileOperationType.Copy, @"\\server\share\a.jpg", @"\\server\share\sel\a.jpg")]
    public async Task ExecuteAsync_PhotoAtRootWithRelativeDestination_LandsInsideTheRoot(FileOperationType type, string source, string expected)
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(source, "photo");
        var service = new FileActionService(new OperationJournal(JournalPaths, disk, new Clock()), disk, new Clock(), new NoBin());

        var result = await service.ExecuteAsync(new FileActionRequest(source, type, "sel"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, result.DestinationPath, ignoreCase: true);
        Assert.True(disk.FileExists(expected));
        Assert.Equal(type == FileOperationType.Copy, disk.FileExists(source));
    }

    [Theory]
    [InlineData(@"C:\a.jpg")]
    [InlineData(@"\\server\share\a.jpg")]
    public async Task ExecuteAsync_PhotoAtRootWithParentTraversal_IsRejectedAndNothingMoves(string source)
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(source, "photo");
        var service = new FileActionService(new OperationJournal(JournalPaths, disk, new Clock()), disk, new Clock(), new NoBin());

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"..\x"));

        Assert.False(result.Succeeded);
        Assert.True(disk.FileExists(source));
    }
}
