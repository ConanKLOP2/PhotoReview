using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gaps of <see cref="OperationJournal"/> and <see cref="JournalEntry"/>: value equality and hashing, Dismiss,
/// the reconcile of Delete/Move groups (including the older-build downgrade repair and its race/failure handling), the
/// compare-and-append guards, append flushing and the tail reader of committed Moves. Fakes only.
/// </summary>
public sealed class OperationJournalMutationTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string OutJpg = @"C:\photos\out\a.jpg";
    private const string OutRaw = @"C:\photos\out\a.cr2";

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>A live-operation registry whose IsLive runs a one-shot hook (a deterministic "another process wrote meanwhile").</summary>
    private sealed class HookedRegistry : ILiveOperationRegistry
    {
        private readonly InProcessLiveOperationRegistry _inner = new();
        public Action? OnFirstIsLive { get; set; }

        public IDisposable Begin(string operationId) => _inner.Begin(operationId);

        public bool IsLive(string operationId)
        {
            var hook = OnFirstIsLive;
            OnFirstIsLive = null;
            hook?.Invoke();
            return _inner.IsLive(operationId);
        }
    }

    private readonly InMemoryFileSystem _fs = new();

    private OperationJournal NewJournal(ILiveOperationRegistry? registry = null) =>
        new(Paths, _fs, new FixedClock(Stamp.AddMinutes(5)), liveOperations: registry);

    private int JournalLineCount() => _fs.FileExists(Paths.JournalFile)
        ? _fs.ReadLines(Paths.JournalFile).Count(line => line.Length > 0)
        : 0;

    private static JournalGroupMember MoveMember(string source, string destination, long size) => new(source, destination, size, Stamp);

    private static JournalEntry MoveGroup(JournalState state, string id = "grp") => new(
        id, FileOperationType.Move, state, Jpg, OutJpg, 4, Stamp, Stamp,
        GroupId: "capture", GroupMembers: [MoveMember(Jpg, OutJpg, 4), MoveMember(Raw, OutRaw, 8)]);

    private static JournalEntry WithoutMembers(JournalEntry entry) => entry with { GroupId = null, GroupMembers = null };

    // ---------------------------------------------------------------------------------------------------------
    // JournalEntry value equality / hash code.
    // ---------------------------------------------------------------------------------------------------------

    private static JournalEntry FullEntry() => new(
        "id", FileOperationType.Move, JournalState.Failed, @"C:\a.jpg", @"C:\b\a.jpg", 10, Stamp, Stamp.AddSeconds(1),
        Error: "e", ErrorCode: "c", Permanent: false, Undo: false, GroupId: "g",
        GroupMembers: [MoveMember(@"C:\a.jpg", @"C:\b\a.jpg", 10)]);

    public static TheoryData<int> FieldIndexes() => new(Enumerable.Range(0, 13));

    private static JournalEntry Variant(JournalEntry e, int field) => field switch
    {
        0 => e with { Id = "other" },
        1 => e with { Type = FileOperationType.Copy },
        2 => e with { State = JournalState.Committed },
        3 => e with { Source = @"C:\other.jpg" },
        4 => e with { Destination = @"C:\b\other.jpg" },
        5 => e with { Size = 11 },
        6 => e with { LastWriteUtc = Stamp.AddSeconds(1) },
        7 => e with { TimestampUtc = Stamp.AddSeconds(2) },
        8 => e with { Error = "other" },
        9 => e with { ErrorCode = "other" },
        10 => e with { Permanent = true },
        11 => e with { Undo = true },
        12 => e with { GroupId = "other" },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void Equals_EntriesDifferingInOnlyOneField_AreNotEqual(int field)
    {
        var left = FullEntry();
        var right = Variant(left, field);

        Assert.False(left.Equals(right));
        Assert.False(right.Equals(left));
    }

    [Fact]
    public void Equals_EntriesDifferingOnlyInTheMembers_AreNotEqual()
    {
        var left = FullEntry();
        var right = left with { GroupMembers = [MoveMember(@"C:\a.jpg", @"C:\b\a.jpg", 99)] };

        Assert.False(left.Equals(right));
    }

    [Fact]
    public void Equals_Null_IsFalse()
    {
        Assert.False(FullEntry().Equals((JournalEntry?)null));
    }

    [Fact]
    public void Equals_SameValuesWithFreshMemberLists_AreEqualWithTheSameHash()
    {
        var left = FullEntry();
        var right = FullEntry() with { GroupMembers = [MoveMember(@"C:\a.jpg", @"C:\b\a.jpg", 10)] };

        Assert.True(left.Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void GetHashCode_EntryWithoutMembers_DoesNotThrow()
    {
        var entry = FullEntry() with { GroupId = null, GroupMembers = null };

        Assert.Equal(entry.GetHashCode(), (entry with { }).GetHashCode()); // equal records hash equally, with null group fields too
    }

    [Fact]
    public void GetHashCode_EntriesDifferingOnlyInTheMembers_DifferInTheirHash()
    {
        var left = FullEntry();
        var right = left with { GroupMembers = [MoveMember(@"C:\a.jpg", @"C:\b\a.jpg", 99), MoveMember(Raw, OutRaw, 8)] };

        Assert.NotEqual(left.GetHashCode(), right.GetHashCode());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Dismiss.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dismiss_OnlyStaleSnapshots_AppendsNothingAndTouchesNoFile()
    {
        var journal = NewJournal();

        var outcome = journal.Dismiss([MoveGroup(JournalState.Failed)]);

        Assert.Empty(outcome.Dismissed);
        Assert.Single(outcome.Skipped);
        Assert.DoesNotContain(_fs.Events, e => e.Kind == "append");
        Assert.False(_fs.FileExists(Paths.JournalFile));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reconcile of Delete (Recycle) groups: a Delete is complete when every member is gone, an undo-Delete when every member is back.
    // ---------------------------------------------------------------------------------------------------------

    private static JournalEntry RecycleGroup(bool? undo) => new(
        "del", FileOperationType.Recycle, JournalState.Prepared, Jpg, null, 4, Stamp, Stamp, Undo: undo,
        GroupId: "capture", GroupMembers: [new(Jpg, null, 4, Stamp), new(Raw, null, 8, Stamp)]);

    [Fact]
    public void Reconcile_DeleteGroupWithEveryMemberGone_IsCommitted()
    {
        var journal = NewJournal();
        journal.Append(RecycleGroup(null));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public void Reconcile_DeleteGroupWithAMemberStillOnDisk_FailsWithTheStillExistsCode()
    {
        _fs.AddFile(Raw, "raw data", Stamp);
        var journal = NewJournal();
        journal.Append(RecycleGroup(null));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, outcome.ErrorCode);
    }

    [Fact]
    public void Reconcile_UndoDeleteGroupWithEveryMemberBack_IsCommitted()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        var journal = NewJournal();
        journal.Append(RecycleGroup(true));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
    }

    [Fact]
    public void Reconcile_UndoDeleteGroupWithAMemberStillMissing_Fails()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        var journal = NewJournal();
        journal.Append(RecycleGroup(true));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
    }

    [Fact]
    public void Reconcile_UndoDeleteGroupWithNothingBack_Fails()
    {
        var journal = NewJournal();
        journal.Append(RecycleGroup(true));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reconcile: empty member lists are not groups.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_PendingMoveWithAnEmptyMemberList_IsJudgedAsASingleMove()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp); // source still there, destination missing: the Move did not happen
        var journal = NewJournal();
        journal.Append(new JournalEntry("single", FileOperationType.Move, JournalState.Prepared, Jpg, OutJpg, 4, Stamp, Stamp, GroupMembers: []));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(JournalErrors.PendingUnconfirmed, outcome.ErrorCode);
    }

    [Fact]
    public void Reconcile_LatestLineWithAnEmptyMemberListAfterAGroupLine_IsRepairedFromTheEarlierMembers()
    {
        // Sources still present, destinations missing: the capture was NOT fully moved although the latest line says Committed.
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        var journal = NewJournal();
        journal.Append(MoveGroup(JournalState.Failed));
        journal.Append(MoveGroup(JournalState.Committed) with { GroupId = null, GroupMembers = [] });

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(OperationJournal.SettledByOlderBuildText, outcome.Error);
        Assert.Equal(2, outcome.GroupMembers!.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Older-build downgrade repair.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_OlderBuildRetriedAGroupAndLeftItPreparedWithoutMembers_IsReconciledWithTheMembersAndWritesOneLine()
    {
        _fs.AddFile(OutJpg, "jpeg", Stamp);
        _fs.AddFile(OutRaw, "raw data", Stamp);
        var journal = NewJournal();
        journal.Append(MoveGroup(JournalState.Failed));
        journal.Append(WithoutMembers(MoveGroup(JournalState.Prepared)));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
        Assert.Equal(2, outcome.GroupMembers!.Count);
        Assert.Equal(3, JournalLineCount()); // Failed, Prepared (older build) and exactly one outcome: no interim Prepared copy
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Reconcile_RepairAppendFailsWithAnIoError_DoesNotThrowAndLeavesTheJournalAlone(Type failure)
    {
        _fs.AddFile(OutJpg, "jpeg", Stamp);
        _fs.AddFile(OutRaw, "raw data", Stamp);
        var journal = NewJournal();
        journal.Append(MoveGroup(JournalState.Failed));
        journal.Append(WithoutMembers(MoveGroup(JournalState.Committed))); // older build settled it; every member really moved
        _fs.OpenAppendHook = _ => (Exception)Activator.CreateInstance(failure, "locked")!;

        var reconciled = journal.ReconcilePendingOperations();

        Assert.Empty(reconciled);
        _fs.OpenAppendHook = null;
        Assert.Equal(2, JournalLineCount());
    }

    private OperationJournal SettledByOlderBuildJournal(HookedRegistry registry)
    {
        _fs.AddFile(OutJpg, "jpeg", Stamp);
        _fs.AddFile(OutRaw, "raw data", Stamp);
        var journal = NewJournal(registry);
        journal.Append(MoveGroup(JournalState.Failed));
        journal.Append(WithoutMembers(MoveGroup(JournalState.Committed)));
        return journal;
    }

    [Fact]
    public void Reconcile_RepairFindsANewerLineAppendedByAnotherWriterMeanwhile_AppendsNothing()
    {
        var registry = new HookedRegistry();
        var journal = SettledByOlderBuildJournal(registry);
        registry.OnFirstIsLive = () => journal.Append(WithoutMembers(MoveGroup(JournalState.Failed)) with { TimestampUtc = Stamp.AddMinutes(9), Error = "other process" });

        var reconciled = journal.ReconcilePendingOperations();

        Assert.Empty(reconciled);
        Assert.Equal(3, JournalLineCount()); // Failed, Committed (older build) and the other writer's line only
    }

    [Fact]
    public void Reconcile_RepairFindsTheIdGoneFromTheJournalMeanwhile_DoesNotThrowOrAppend()
    {
        var registry = new HookedRegistry();
        var journal = SettledByOlderBuildJournal(registry);
        registry.OnFirstIsLive = () => _fs.Delete(Paths.JournalFile);

        var reconciled = journal.ReconcilePendingOperations();

        Assert.Empty(reconciled);
        Assert.False(_fs.FileExists(Paths.JournalFile));
    }

    // ---------------------------------------------------------------------------------------------------------
    // AppendIfUnchangedSince with several own entries.
    // ---------------------------------------------------------------------------------------------------------

    private static JournalEntry Line(JournalState state, int tick) =>
        new("op", FileOperationType.Move, state, Jpg, OutJpg, 4, Stamp, Stamp.AddSeconds(tick));

    [Fact]
    public void AppendIfUnchangedSince_TailIsExactlyTheOwnEntriesInOrder_Appends()
    {
        var journal = NewJournal();
        var anchor = Line(JournalState.Failed, 0);
        var own1 = Line(JournalState.Prepared, 1);
        var own2 = Line(JournalState.Prepared, 2);
        journal.Append(anchor);
        journal.Append(own1);
        journal.Append(own2);

        var appended = journal.AppendIfUnchangedSince(anchor, [own1, own2], Line(JournalState.Committed, 3));

        Assert.True(appended);
        Assert.Equal(4, JournalLineCount());
    }

    [Fact]
    public void AppendIfUnchangedSince_SecondEntryAfterTheAnchorIsSomebodyElses_DoesNotAppend()
    {
        var journal = NewJournal();
        var anchor = Line(JournalState.Failed, 0);
        var own1 = Line(JournalState.Prepared, 1);
        var own2 = Line(JournalState.Prepared, 2);
        journal.Append(anchor);
        journal.Append(own1);
        journal.Append(Line(JournalState.Committed, 7)); // not own2

        var appended = journal.AppendIfUnchangedSince(anchor, [own1, own2], Line(JournalState.Failed, 8));

        Assert.False(appended);
        Assert.Equal(3, JournalLineCount());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Appends are flushed to the file system.
    // ---------------------------------------------------------------------------------------------------------

    private sealed class FlushSpyStream(Stream inner, Action onFlush) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { onFlush(); inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FlushSpyFileSystem(InMemoryFileSystem inner) : IFileSystem
    {
        public int Flushes { get; private set; }
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path) => inner.GetFileStat(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppendDurable(string path) => new FlushSpyStream(inner.OpenAppendDurable(path), () => Flushes++);
        public Stream OpenAppend(string path, bool durable) => new FlushSpyStream(inner.OpenAppend(path, durable), () => Flushes++);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    [Theory]
    [InlineData(JournalDurability.Fast)]
    [InlineData(JournalDurability.PowerLossSafe)]
    public void Append_FlushesTheAppendStream(JournalDurability mode)
    {
        var spy = new FlushSpyFileSystem(_fs);
        var journal = new OperationJournal(Paths, spy, new FixedClock(Stamp), () => mode);

        journal.Append(Line(JournalState.Committed, 0));

        Assert.True(spy.Flushes >= 1);
        Assert.Equal(1, JournalLineCount());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Tail reader of committed Moves (journals of at least 1 MiB).
    // ---------------------------------------------------------------------------------------------------------

    private const int OneMiB = 1024 * 1024;

    private static byte[] MoveLine(int i) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new JournalEntry($"m{i:D4}", FileOperationType.Move, JournalState.Committed, $@"C:\photos\{i}.jpg", $@"C:\sel\{i}.jpg", 4, Stamp, Stamp))
        + "\r\n");

    /// <summary>One malformed (skipped) line so that, with <paramref name="rest"/>, the file is exactly <paramref name="totalLength"/> bytes.</summary>
    private static byte[] PaddedJournal(int totalLength, byte[] rest)
    {
        var padLength = totalLength - rest.Length;
        Assert.True(padLength >= 3);
        var pad = new byte[padLength];
        Array.Fill(pad, (byte)'x');
        pad[^2] = (byte)'\r';
        pad[^1] = (byte)'\n';
        return [.. pad, .. rest];
    }

    private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(part => part).ToArray();

    [Fact]
    public void ReadCommittedMoves_JournalOfExactlyOneMiB_UsesTheTailWindowOf200Moves()
    {
        var rest = Concat(Enumerable.Range(0, 300).Select(MoveLine));
        _fs.AddFile(Paths.JournalFile, PaddedJournal(OneMiB, rest));

        var moves = NewJournal().ReadCommittedMoves();

        Assert.Equal(OperationJournal.StartupCommittedMoveLimit, moves.Count);
        Assert.Equal("m0100", moves[0].Id);
        Assert.Equal("m0299", moves[^1].Id);
    }

    [Fact]
    public void ReadCommittedMoves_JournalOneByteUnderOneMiB_ReadsEveryMove()
    {
        var rest = Concat(Enumerable.Range(0, 300).Select(MoveLine));
        _fs.AddFile(Paths.JournalFile, PaddedJournal(OneMiB - 1, rest));

        var moves = NewJournal().ReadCommittedMoves();

        Assert.Equal(300, moves.Count);
    }

    [Fact]
    public void ReadCommittedMoves_BareNewlinesBetweenLinesInABigJournal_NeitherLosesNorDuplicatesAMove()
    {
        // A bare "\n" is what the torn-tail repair leaves between records.
        var parts = new List<byte[]>();
        for (var i = 0; i < 50; i++) parts.Add(MoveLine(i));
        parts.Add("\n"u8.ToArray());
        for (var i = 50; i < 99; i++) parts.Add(MoveLine(i));
        parts.Add("\n"u8.ToArray());
        parts.Add(MoveLine(99));
        _fs.AddFile(Paths.JournalFile, PaddedJournal(OneMiB + 5000, Concat(parts)));

        var moves = NewJournal().ReadCommittedMoves();

        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"m{i:D4}"), moves.Select(m => m.Id));
    }
}