using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Q-R27 (decision D): an executing operation holds a live marker from just before its Prepared entry until its outcome
/// is appended, and reconcile skips a pending entry whose marker is live. Two journal instances sharing one file and one
/// registry stand for two PhotoReview processes (the Windows registry makes the marker visible across processes).
/// </summary>
public sealed class LiveOperationMarkerTests
{
    private static readonly DateTime Start = new(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class ScriptedRecycleBin : IRecycleBin
    {
        public Action<string>? OnRecycle { get; set; }

        public void SendToRecycleBin(string path) => OnRecycle?.Invoke(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    /// <summary>The real in-process registry, plus the list of Ids ever begun so a test can snapshot which are live.</summary>
    private sealed class SnapshotRegistry : ILiveOperationRegistry
    {
        private readonly InProcessLiveOperationRegistry _inner = new();
        private readonly ConcurrentDictionary<string, byte> _begun = new(StringComparer.Ordinal);

        public IDisposable Begin(string operationId)
        {
            _begun[operationId] = 0;
            return _inner.Begin(operationId);
        }

        public bool IsLive(string operationId) => _inner.IsLive(operationId);

        public HashSet<string> LiveNow() => _begun.Keys.Where(_inner.IsLive).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// One process's file actions on an in-memory disk. Every journal append records which Ids were live at that moment,
    /// so a test can check the marker covered the Prepared append and the outcome append of its operation.
    /// </summary>
    private sealed class Rig
    {
        public InMemoryFileSystem Fs { get; } = new();
        public SnapshotRegistry Registry { get; } = new();
        public OperationJournal Journal { get; }
        public ScriptedRecycleBin Bin { get; } = new();
        public List<HashSet<string>> LiveAtAppend { get; } = [];

        public Rig()
        {
            Journal = new OperationJournal(Paths, Fs, new FixedClock(Start), liveOperations: Registry);
            Fs.OpenAppendHook = _ =>
            {
                lock (LiveAtAppend) LiveAtAppend.Add(Registry.LiveNow());
                return null;
            };
        }

        public FileActionService Actions() => new(Journal, Fs, new FixedClock(Start), Bin);

        public List<JournalEntry> Lines() =>
            Fs.ReadAllText(Paths.JournalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonSerializer.Deserialize<JournalEntry>(line)!).ToList();

        /// <summary>Asserts every appended line (Prepared and outcome) was written while its own Id was live.</summary>
        public void AssertEveryAppendUnderItsMarker(int expectedLines)
        {
            var lines = Lines();
            Assert.Equal(expectedLines, lines.Count);
            Assert.Equal(lines.Count, LiveAtAppend.Count);
            for (var i = 0; i < lines.Count; i++)
                Assert.True(LiveAtAppend[i].Contains(lines[i].Id),
                    $"journal line {i} ({lines[i].State}) was appended while operation {lines[i].Id} had no live marker");
        }

        /// <summary>The single pending entry right now (inside the mutation) and whether its marker is live.</summary>
        public (string Id, bool Live) PendingNow()
        {
            var pending = Assert.Single(Journal.ReadPendingOperations());
            return (pending.Id, Registry.IsLive(pending.Id));
        }
    }

    private static JournalEntry Prepared(string id, FileOperationType type, string source, string? destination) =>
        new(id, type, JournalState.Prepared, source, destination, 5, Start.AddDays(-1), Start.AddMinutes(-10));

    // --- 1. Reconcile skips live operations -------------------------------------------------------------------------------

    public static TheoryData<FileOperationType> Types => new() { FileOperationType.Move, FileOperationType.Copy, FileOperationType.Recycle };

    [Theory(DisplayName = "Q-R27: reconcile leaves a Prepared operation whose marker is live alone; once the marker is gone it is judged as before")]
    [MemberData(nameof(Types))]
    public void Reconcile_LiveMarker_SkippedUntilReleased(FileOperationType type)
    {
        var fs = new InMemoryFileSystem();
        var registry = new InProcessLiveOperationRegistry();
        var owner = new OperationJournal(Paths, fs, new FixedClock(Start), liveOperations: registry);      // process A
        var reconciler = new OperationJournal(Paths, fs, new FixedClock(Start), liveOperations: registry); // process B
        // A is mid-flight: a Move/Recycle whose source is still there, a Copy whose destination is not written yet.
        var entry = Prepared("op1", type, @"C:\photos\a.jpg", type == FileOperationType.Recycle ? null : @"C:\photos\sel\a.jpg");
        fs.AddFile(entry.Source, "12345");
        var marker = registry.Begin(entry.Id);
        owner.Append(entry);

        // B started after A prepared (the startup cutoff does not protect A's operation).
        var reconciled = reconciler.ReconcilePendingOperations(preparedBeforeUtc: Start);

        Assert.Empty(reconciled);
        Assert.Empty(reconciler.ReadFailedOperations());
        Assert.Equal(entry.Id, Assert.Single(reconciler.ReadPendingOperations()).Id);

        marker.Dispose(); // A died without an outcome: its marker is gone, the leftover is reconciled as before Q-R27

        var failed = Assert.Single(reconciler.ReconcilePendingOperations(preparedBeforeUtc: Start));
        Assert.Equal(JournalState.Failed, failed.State);
        Assert.Equal(type == FileOperationType.Recycle ? JournalErrors.SourceStillExistsAfterRecovery : JournalErrors.PendingUnconfirmed,
            failed.ErrorCode);
        Assert.Empty(reconciler.ReadPendingOperations());
    }

    /// <summary>Forwards to the in-memory FS and runs a one-shot action when the source is first inspected.</summary>
    public class InterceptingFs : DispatchProxy
    {
        public IFileSystem Inner { get; set; } = null!;
        public string? Source { get; set; }
        public Action? OnFirstSourceCheck { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFileSystem.FileExists) && args![0] as string == Source && OnFirstSourceCheck is { } action)
            {
                OnFirstSourceCheck = null;
                action();
            }
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException!; }
        }
    }

    [Fact(DisplayName = "Q-R27: a retry that re-prepares the Id while reconcile judges the old snapshot is not marked Failed")]
    public void Reconcile_RetryBecomesLiveDuringJudgement_NotAppended()
    {
        var fs = new InMemoryFileSystem();
        var registry = new InProcessLiveOperationRegistry();
        var owner = new OperationJournal(Paths, fs, new FixedClock(Start), liveOperations: registry);
        var entry = Prepared("op1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg");
        fs.AddFile(entry.Source, "12345");
        owner.Append(entry); // a dead leftover: no marker

        var proxy = DispatchProxy.Create<IFileSystem, InterceptingFs>();
        var intercept = (InterceptingFs)(object)proxy;
        intercept.Inner = fs;
        intercept.Source = entry.Source;
        IDisposable? retryMarker = null;
        // Reconcile has read the leftover and is judging it; now Recovery (another process) retries the same Id: marker
        // first, then a fresh Prepared under that Id (JournalTransaction order).
        intercept.OnFirstSourceCheck = () =>
        {
            retryMarker = registry.Begin(entry.Id);
            owner.Append(entry with { TimestampUtc = Start.AddMinutes(-1) });
        };
        var reconciler = new OperationJournal(Paths, proxy, new FixedClock(Start), liveOperations: registry);

        var reconciled = reconciler.ReconcilePendingOperations(preparedBeforeUtc: Start);

        Assert.NotNull(retryMarker);
        Assert.Empty(reconciled);
        Assert.Empty(reconciler.ReadFailedOperations());
        Assert.Single(reconciler.ReadPendingOperations());
        retryMarker!.Dispose();
    }

    // --- 2. Every Prepared writer holds the marker exactly for the Prepared -> outcome window ------------------------------

    [Theory(DisplayName = "Q-R27: Move/Copy hold the live marker from Prepared through the outcome (success and failure)")]
    [InlineData(FileOperationType.Move, false)]
    [InlineData(FileOperationType.Move, true)]
    [InlineData(FileOperationType.Copy, false)]
    [InlineData(FileOperationType.Copy, true)]
    public async Task FileAction_MoveCopy_MarkerCoversPreparedToOutcome(FileOperationType type, bool mutationFails)
    {
        var rig = new Rig();
        rig.Fs.AddFile(@"C:\photos\a.jpg", "12345", Start.AddDays(-1));
        (string Id, bool Live)? duringMutation = null;
        Func<string, string, Exception?> hook = (_, _) =>
        {
            duringMutation = rig.PendingNow();
            return mutationFails ? new IOException("network drive went away") : null;
        };
        if (type == FileOperationType.Move) rig.Fs.MoveHook = hook; else rig.Fs.CopyHook = hook;

        var result = await rig.Actions().ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", type, "sel"));

        Assert.Equal(!mutationFails, result.Succeeded);
        Assert.NotNull(duringMutation);
        Assert.True(duringMutation.Value.Live, "the operation must be live while its file mutation runs");
        Assert.False(rig.Registry.IsLive(duringMutation.Value.Id), "the marker must end with the operation");
        rig.AssertEveryAppendUnderItsMarker(expectedLines: 2);
        Assert.Equal(mutationFails ? JournalState.Failed : JournalState.Committed, rig.Lines()[1].State);
    }

    [Theory(DisplayName = "Q-R27: Recycle holds the live marker from Prepared through the outcome (success and failure)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAction_Recycle_MarkerCoversPreparedToOutcome(bool recycleFails)
    {
        var rig = new Rig();
        rig.Fs.AddFile(@"C:\photos\a.jpg", "12345", Start.AddDays(-1));
        (string Id, bool Live)? duringMutation = null;
        rig.Bin.OnRecycle = _ =>
        {
            duringMutation = rig.PendingNow();
            if (recycleFails) throw new IOException("in use");
        };

        var result = await rig.Actions().ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Recycle, null));

        Assert.Equal(!recycleFails, result.Succeeded);
        Assert.NotNull(duringMutation);
        Assert.True(duringMutation.Value.Live);
        Assert.False(rig.Registry.IsLive(duringMutation.Value.Id));
        rig.AssertEveryAppendUnderItsMarker(expectedLines: 2);
    }

    [Fact(DisplayName = "Q-R27: a failed Prepared append releases the marker (nothing pending, nothing live)")]
    public async Task FileAction_PreparedAppendFails_MarkerReleased()
    {
        var rig = new Rig();
        rig.Fs.AddFile(@"C:\photos\a.jpg", "12345", Start.AddDays(-1));
        rig.Fs.OpenAppendHook = _ => new IOException("journal disk full");

        var result = await rig.Actions().ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "sel"));

        Assert.False(result.Succeeded);
        Assert.Empty(rig.Registry.LiveNow());
        Assert.True(rig.Fs.FileExists(@"C:\photos\a.jpg"));
    }

    [Theory(DisplayName = "Q-R27: Undo of a Move holds the live marker from Prepared through the outcome")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_Move_MarkerCoversPreparedToOutcome(bool moveFails)
    {
        var rig = new Rig();
        const string source = @"C:\photos\a.jpg";
        const string destination = @"C:\photos\sel\a.jpg";
        var writeTime = Start.AddDays(-1);
        rig.Fs.AddFile(destination, "12345", writeTime);
        var undo = new UndoService(rig.Journal, rig.Fs, rig.Bin, clock: new FixedClock(Start));
        undo.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 5, writeTime, null));
        (string Id, bool Live)? duringMutation = null;
        rig.Fs.MoveHook = (_, _) =>
        {
            duringMutation = rig.PendingNow();
            return moveFails ? new IOException("locked") : null;
        };

        var result = await undo.UndoMoveAsync();

        Assert.Equal(!moveFails, result.Succeeded);
        Assert.NotNull(duringMutation);
        Assert.True(duringMutation.Value.Live);
        Assert.False(rig.Registry.IsLive(duringMutation.Value.Id));
        rig.AssertEveryAppendUnderItsMarker(expectedLines: 2);
    }

    [Theory(DisplayName = "Q-R27: a Recovery retry holds the live marker from Prepared through the outcome")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRetry_MarkerCoversPreparedToOutcome(bool copyFails)
    {
        var rig = new Rig();
        var writeTime = Start.AddDays(-1);
        rig.Fs.AddFile(@"C:\photos\a.jpg", "12345", writeTime);
        var failed = new JournalEntry("retry1", FileOperationType.Copy, JournalState.Failed, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg",
            5, writeTime, Start.AddMinutes(-5), "x", JournalErrors.PendingUnconfirmed);
        (string Id, bool Live)? duringMutation = null;
        rig.Fs.CopyHook = (_, _) =>
        {
            duringMutation = rig.PendingNow();
            return copyFails ? new IOException("network drive went away") : null;
        };

        var result = await new RecoveryRetryService(rig.Journal, rig.Fs, new FixedClock(Start)).RetryMoveOrCopyAsync(failed);

        Assert.Equal(!copyFails, result.Succeeded);
        Assert.NotNull(duringMutation);
        Assert.Equal("retry1", duringMutation.Value.Id);
        Assert.True(duringMutation.Value.Live);
        Assert.False(rig.Registry.IsLive("retry1"));
        rig.AssertEveryAppendUnderItsMarker(expectedLines: 2);
    }

    // --- In-process registry --------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Q-R27: in-process registry is live per Id while any handle is undisposed; Dispose is idempotent")]
    public void InProcessRegistry_RefCountsPerId()
    {
        var registry = new InProcessLiveOperationRegistry();
        var first = registry.Begin("a");
        var second = registry.Begin("a");

        Assert.True(registry.IsLive("a"));
        Assert.False(registry.IsLive("b"));
        first.Dispose();
        first.Dispose();
        Assert.True(registry.IsLive("a"));
        second.Dispose();
        Assert.False(registry.IsLive("a"));
    }
}
