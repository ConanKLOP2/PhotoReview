using System.Reflection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class RecoveryFileCheckTests
{
    private const string Src = @"C:\photos\a.jpg";
    private const string Dst = @"D:\keep\a.jpg";
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly byte[] Bytes = new byte[10];

    private static JournalEntry Entry(FileOperationType type, JournalState state, string? destination = Dst) =>
        new("id1", type, state, Src, destination, Bytes.Length, Stamp, Stamp);

    private static InMemoryFileSystem Fs(bool source = false, bool sourceChanged = false, bool dest = false, bool destChanged = false, bool destDir = false)
    {
        var fs = new InMemoryFileSystem();
        if (source) fs.AddFile(Src, sourceChanged ? new byte[11] : Bytes, Stamp);
        if (dest) fs.AddFile(Dst, destChanged ? new byte[3] : Bytes, Stamp);
        if (destDir) fs.CreateDirectory(@"D:\keep");
        return fs;
    }

    [Theory]
    // Move/Copy Prepared + Failed: source, sourceChanged, dest, destChanged
    [InlineData(FileOperationType.Move, JournalState.Prepared, true, false, false, false, RecoveryVerdict.CanRetry)]
    [InlineData(FileOperationType.Move, JournalState.Failed, true, false, false, false, RecoveryVerdict.CanRetry)]
    [InlineData(FileOperationType.Copy, JournalState.Failed, true, false, false, false, RecoveryVerdict.CanRetry)]
    [InlineData(FileOperationType.Move, JournalState.Failed, true, true, false, false, RecoveryVerdict.SourceChanged)]
    [InlineData(FileOperationType.Move, JournalState.Prepared, false, false, true, false, RecoveryVerdict.AlreadyDone)]
    [InlineData(FileOperationType.Copy, JournalState.Prepared, false, false, true, false, RecoveryVerdict.AlreadyDone)]
    [InlineData(FileOperationType.Move, JournalState.Failed, false, false, true, true, RecoveryVerdict.DestinationChanged)]
    [InlineData(FileOperationType.Move, JournalState.Failed, false, false, false, false, RecoveryVerdict.Lost)]
    [InlineData(FileOperationType.Copy, JournalState.Prepared, false, false, false, false, RecoveryVerdict.Lost)]
    [InlineData(FileOperationType.Move, JournalState.Failed, true, false, true, false, RecoveryVerdict.Conflict)]
    [InlineData(FileOperationType.Move, JournalState.Failed, true, true, true, false, RecoveryVerdict.Conflict)]
    [InlineData(FileOperationType.Move, JournalState.Failed, true, false, true, true, RecoveryVerdict.Conflict)]
    [InlineData(FileOperationType.Copy, JournalState.Failed, true, false, true, false, RecoveryVerdict.AlreadyDone)]
    [InlineData(FileOperationType.Copy, JournalState.Failed, true, false, true, true, RecoveryVerdict.Conflict)]
    [InlineData(FileOperationType.Copy, JournalState.Failed, true, true, true, false, RecoveryVerdict.Conflict)]
    // Committed
    [InlineData(FileOperationType.Move, JournalState.Committed, false, false, true, false, RecoveryVerdict.AlreadyDone)]
    [InlineData(FileOperationType.Copy, JournalState.Committed, true, false, true, false, RecoveryVerdict.AlreadyDone)]
    [InlineData(FileOperationType.Move, JournalState.Committed, false, false, false, false, RecoveryVerdict.Lost)]
    [InlineData(FileOperationType.Move, JournalState.Committed, true, false, false, false, RecoveryVerdict.Unknown)]
    [InlineData(FileOperationType.Move, JournalState.Committed, false, false, true, true, RecoveryVerdict.Unknown)]
    // Dismissed
    [InlineData(FileOperationType.Move, JournalState.Dismissed, true, false, false, false, RecoveryVerdict.Unknown)]
    public void Verdict_MoveCopy_FollowsRuleTable(FileOperationType type, JournalState state, bool source, bool sourceChanged, bool dest, bool destChanged, RecoveryVerdict expected)
    {
        var result = new RecoveryFileCheck(Fs(source, sourceChanged, dest, destChanged)).Check(Entry(type, state));
        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public void Verdict_SourceLastWriteDiffers_IsSourceChanged()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Src, Bytes, Stamp.AddSeconds(5));
        var result = new RecoveryFileCheck(fs).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.Equal(RecoveryPathStatus.Changed, result.Source.Status);
        Assert.Equal(RecoveryVerdict.SourceChanged, result.Verdict);
    }

    [Fact]
    public void Destination_LastWriteDiffers_StillExists()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Dst, Bytes, Stamp.AddDays(1));
        var result = new RecoveryFileCheck(fs).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.Equal(RecoveryPathStatus.Exists, result.Destination!.Status);
        Assert.Equal(Stamp.AddDays(1), result.Destination.CurrentLastWriteUtc);
        Assert.Equal(RecoveryVerdict.AlreadyDone, result.Verdict);
    }

    [Fact]
    public void Verdict_MoveWithoutDestination_IsUnknown()
    {
        var result = new RecoveryFileCheck(Fs(source: true)).Check(Entry(FileOperationType.Move, JournalState.Failed, destination: null));
        Assert.Null(result.Destination);
        Assert.Equal(RecoveryVerdict.Unknown, result.Verdict);
    }

    [Theory]
    [InlineData(false, false, RecoveryVerdict.RecycleUnverifiable)]
    [InlineData(true, false, RecoveryVerdict.NotRecycled)]
    [InlineData(true, true, RecoveryVerdict.NotRecycled)]
    public void Verdict_Recycle(bool source, bool changed, RecoveryVerdict expected)
    {
        var result = new RecoveryFileCheck(Fs(source, changed)).Check(Entry(FileOperationType.Recycle, JournalState.Prepared, destination: null));
        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public void Verdict_Recycle_IgnoresDestination()
    {
        var result = new RecoveryFileCheck(Fs(dest: true)).Check(Entry(FileOperationType.Recycle, JournalState.Failed));
        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, result.Verdict);
    }

    [Fact]
    public void DestinationFolderMissing_TrueOnlyWhenDestinationAndFolderAreGone()
    {
        var gone = new RecoveryFileCheck(Fs(source: true)).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.True(gone.DestinationFolderMissing);

        var folderOnly = new RecoveryFileCheck(Fs(source: true, destDir: true)).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.False(folderOnly.DestinationFolderMissing);
        Assert.Equal(RecoveryVerdict.CanRetry, folderOnly.Verdict);

        var present = new RecoveryFileCheck(Fs(dest: true)).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.False(present.DestinationFolderMissing);
    }

    [Theory]
    [InlineData(Src, RecoveryVerdict.Unknown)]
    [InlineData(Dst, RecoveryVerdict.Unknown)]
    public void Unreadable_AccessDenied_IsUnknown(string deniedPath, RecoveryVerdict expected)
    {
        var fs = DenyStat(Fs(source: true, dest: true), deniedPath);
        var result = new RecoveryFileCheck(fs).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.Equal(expected, result.Verdict);
        var denied = deniedPath == Src ? result.Source : result.Destination!;
        Assert.Equal(RecoveryPathStatus.Unreadable, denied.Status);
        Assert.Null(denied.CurrentSize);
    }

    [Fact]
    public void Unreadable_RecycleSource_IsUnknown()
    {
        var fs = DenyStat(Fs(source: true), Src);
        var result = new RecoveryFileCheck(fs).Check(Entry(FileOperationType.Recycle, JournalState.Prepared, destination: null));
        Assert.Equal(RecoveryVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void Check_NeverMutates_OnlyStatCalls()
    {
        var inner = Fs(source: true, dest: true);
        var metrics = new ReviewMetrics();
        var counting = new CountingFileSystem(inner, metrics);
        new RecoveryFileCheck(counting).Check(Entry(FileOperationType.Move, JournalState.Failed));
        Assert.True(inner.FileExists(Src));
        Assert.True(inner.FileExists(Dst));
        Assert.Equal(Bytes.Length, inner.GetFileStat(Src)!.Length);
    }

    [Fact]
    public void Codes_AreStableAndUnique()
    {
        var codes = Enum.GetValues<RecoveryVerdict>().Select(RecoveryFileCheck.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("CanRetry", RecoveryFileCheck.Code(RecoveryVerdict.CanRetry));
        Assert.Equal("AlreadyDone", RecoveryFileCheck.Code(RecoveryVerdict.AlreadyDone));
        Assert.Equal("RecycleUnverifiable", RecoveryFileCheck.Code(RecoveryVerdict.RecycleUnverifiable));
    }

    private static IFileSystem DenyStat(IFileSystem inner, string deniedPath)
    {
        var proxy = DispatchProxy.Create<IFileSystem, DenyProxy>();
        var deny = (DenyProxy)(object)proxy;
        deny.Inner = inner;
        deny.Denied = deniedPath;
        return proxy;
    }

    public class DenyProxy : DispatchProxy
    {
        public IFileSystem Inner { get; set; } = null!;
        public string Denied { get; set; } = "";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFileSystem.GetFileStat) && string.Equals((string?)args![0], Denied, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("denied");
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
    }
}
