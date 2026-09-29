using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// One journal line (JSON). <see cref="Error"/> is invariant machine text: English for coded errors, the OS message
/// otherwise; files written before ADR 0006 may hold Vietnamese. <see cref="ErrorCode"/> (Q-L3) is a stable
/// <see cref="JournalErrors"/> code the UI localizes by (<see cref="JournalErrors.Describe(JournalEntry)"/>). It is
/// omitted from the JSON when null, so records without a code keep their previous shape, and older builds (which
/// skip unknown members) still read new files. <see cref="Permanent"/> (Q-R8) is true for a Recycle that deleted the file
/// permanently (drive without a Recycle Bin, user opt-in): such an entry can never be restored; null/omitted otherwise.
/// <see cref="Undo"/> (review r7) is true for the Move that undoes an earlier Move (Source = the earlier destination):
/// it is journaled so a crash mid-undo is reconciled/recoverable, but it is never itself loaded as undoable history.
/// </summary>
public sealed record JournalEntry(
    string Id,
    FileOperationType Type,
    JournalState State,
    string Source,
    string? Destination,
    long Size,
    DateTime LastWriteUtc,
    DateTime TimestampUtc,
    string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Permanent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Undo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JournalGroupMember>? GroupMembers = null);

/// <summary>
/// Result of <see cref="OperationJournal.Dismiss"/>: <see cref="Dismissed"/> are the entries actually appended as
/// Dismissed; <see cref="Skipped"/> are entries the caller asked to dismiss whose current latest journal state no
/// longer matches the snapshot it held (R09) -- a concurrent retry or another process already resolved them, so
/// nothing was appended for them and the caller should refresh instead of treating them as cleared.
/// </summary>
public sealed record DismissOutcome(IReadOnlyList<JournalEntry> Dismissed, IReadOnlyList<JournalEntry> Skipped);

/// <summary>What <see cref="OperationJournal.TryCompact"/> did. Every outcome but <see cref="Compacted"/> left the journal untouched.</summary>
public enum JournalCompactionOutcome
{
    /// <summary>The journal was replaced by its compacted copy (plus every line appended meanwhile).</summary>
    Compacted,
    /// <summary>No compaction primitives were given to this journal (tests, tools).</summary>
    NotSupported,
    /// <summary>Missing, or smaller than <c>OperationJournal.CompactionThresholdBytes</c>.</summary>
    BelowThreshold,
    /// <summary>Less than <c>OperationJournal.CompactionMinGain</c> of the file could be dropped.</summary>
    NotWorthIt,
    /// <summary>A writer held the journal when compaction tried to lock it; try again later.</summary>
    Busy,
    /// <summary>The journal's start no longer matches the snapshot (another process compacted it meanwhile).</summary>
    Changed,
    /// <summary>An I/O error; see <see cref="JournalCompactionResult.Error"/>.</summary>
    Failed,
}

/// <summary>Result of <see cref="OperationJournal.TryCompact"/>; sizes in bytes (0 when unknown).</summary>
public sealed record JournalCompactionResult(JournalCompactionOutcome Outcome, long BytesBefore, long BytesAfter, string? Error = null);

public sealed class OperationJournal
{
    private const long FullScanThresholdBytes = 1 * 1024 * 1024;
    internal const int StartupCommittedMoveLimit = 200;
    private readonly string _path;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Func<JournalDurability> _durability;
    private readonly Action<TimeSpan> _appendRetryDelay;
    private readonly ILiveOperationRegistry _liveOperations;
    private readonly IJournalCompactionFiles? _compactionFiles;

    /// <summary>
    /// Review r7: two PhotoReview processes (one per folder) share operations.jsonl, and an append holds the file with
    /// FileShare.Read only for the few milliseconds of its write. A concurrent append therefore gets a sharing violation;
    /// it is retried a few times with a short, bounded backoff (worst case ~100 ms in total) instead of failing the action.
    /// </summary>
    internal const int AppendAttempts = 5;
    private static readonly TimeSpan AppendRetryStep = TimeSpan.FromMilliseconds(10);

    public OperationJournal()
        : this(PhotoReview.Core.AppPaths.FromEnvironment(), new PhysicalFileSystem(), new SystemClock())
    {
    }

    /// <param name="appendRetryDelay">
    /// Wait between append attempts after a sharing/lock violation (default <see cref="Thread.Sleep(TimeSpan)"/>).
    /// A test seam: it lets a test release a competing handle deterministically instead of racing a timer.
    /// </param>
    /// <param name="liveOperations">
    /// Q-R27: markers of the operations executing right now (in any process sharing this journal). Every Prepared entry is
    /// written under a marker (<see cref="JournalTransaction"/>) and reconcile skips live ones. Default: a process-local
    /// registry (tests, tools); the app injects the Windows named-object registry.
    /// </param>
    /// <param name="compactionFiles">
    /// The file primitives <see cref="TryCompact"/> needs (<see cref="PhotoReview.Core.IO.PhysicalJournalCompactionFiles"/>
    /// in the app). Null (default): the journal never compacts.
    /// </param>
    public OperationJournal(IAppPaths paths, IFileSystem fileSystem, IClock clock, Func<JournalDurability>? durability = null,
        Action<TimeSpan>? appendRetryDelay = null, ILiveOperationRegistry? liveOperations = null,
        IJournalCompactionFiles? compactionFiles = null)
    {
        _compactionFiles = compactionFiles;
        _liveOperations = liveOperations ?? new InProcessLiveOperationRegistry();
        _durability = durability ?? (() => JournalDurability.Fast);
        _appendRetryDelay = appendRetryDelay ?? Thread.Sleep;
        ArgumentNullException.ThrowIfNull(paths);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _path = paths.JournalFile;
    }

    /// <summary>Mode applied to the next write (re-read from the provider every time, so a Settings change needs no restart).</summary>
    public JournalDurability Durability => _durability();

    /// <summary>Q-R27: the registry the operations of this journal hold their live marker in (see <see cref="JournalTransaction"/>).</summary>
    public ILiveOperationRegistry LiveOperations => _liveOperations;

    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AppendLines([entry]);
    }

    // Clearing a Recovery item appends a Dismissed entry under the same Id (the journal is
    // append-only); latest-entry resolution then drops it from pending/failed. No file is touched.
    //
    // R09: the Recovery window snapshots a JournalEntry when it opens/re-checks, but the user may click Dismiss
    // later -- meanwhile another process sharing this journal (InstanceMode.PerFolder), or a retry in this same
    // process, may have appended a newer entry for the same Id (e.g. a successful retry). Blindly appending a
    // Dismissed record built from the stale snapshot would then win under latest-entry resolution and hide that
    // newer entry. So each entry is re-checked against the CURRENT latest entry for its Id (same
    // read-latest/compare/act-only-if-still-current pattern as <see cref="AppendIfStillPending"/>) and only
    // dismissed when the latest entry is still byte-for-byte the one the caller snapshotted; anything else is
    // reported back as skipped instead of silently dismissed.
    public DismissOutcome Dismiss(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = _clock.UtcNow;
        var toDismiss = new List<JournalEntry>();
        var skipped = new List<JournalEntry>();
        lock (_gate)
        {
            var latest = ComputeLatestEntries();
            foreach (var entry in entries)
            {
                if (latest.TryGetValue(entry.Id, out var current) && current == entry)
                    toDismiss.Add(entry with { State = JournalState.Dismissed, TimestampUtc = now, Error = null, ErrorCode = null });
                else
                    skipped.Add(entry);
            }
            if (toDismiss.Count > 0) AppendLines(toDismiss);
        }
        return new DismissOutcome(toDismiss, skipped);
    }

    private bool _tailChecked; // every append ends with a newline, so the file tail only needs checking before this process's first append

    private bool TailLacksNewline()
    {
        if (!_fileSystem.FileExists(_path)) return false;
        using var stream = _fileSystem.OpenReadShared(_path, 16);
        if (!stream.CanSeek || stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() is not (-1 or (int)'\n');
    }

    private void AppendLines(IReadOnlyList<JournalEntry> entries)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                _fileSystem.CreateDirectory(dir);
            }
            var text = new StringBuilder();
            foreach (var entry in entries)
                text.Append(JsonSerializer.Serialize(entry)).Append(Environment.NewLine);
            var durable = _durability() == JournalDurability.PowerLossSafe;
            // A crash can leave a partial last line; appending straight after it would glue two records together and lose both.
            if (!_tailChecked && TailLacksNewline()) text.Insert(0, Environment.NewLine);
            // Disarm until this write is known to have completed: a write that fails half-way (disk full) leaves a partial
            // line, and the next append must repair it even though an earlier append of this process succeeded.
            _tailChecked = false;
            using var stream = OpenAppendWithRetry(durable);
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
            // Fast: plain Flush() hands the bytes to the OS before the file operation starts (survives a process crash).
            if (durable && stream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
            else
            {
                stream.Flush();
            }
            // Only now does the file end with a newline written by this process; a failed open/write/flush keeps the
            // check for the next append, which must still repair a partial tail left by a crash (review r7).
            _tailChecked = true;
        }
    }

    private Stream OpenAppendWithRetry(bool durable)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _fileSystem.OpenAppend(_path, durable);
            }
            catch (IOException ex) when (attempt < AppendAttempts && IsSharingOrLockViolation(ex))
            {
                _appendRetryDelay(AppendRetryStep * attempt);
            }
        }
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33), surfaced by FileStream as HRESULT 0x80070020 / 0x80070021.
    private static bool IsSharingOrLockViolation(IOException ex) =>
        ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    public IReadOnlyList<JournalEntry> ReadCommittedMoves()
    {
        lock (_gate)
        {
            if (!_fileSystem.FileExists(_path)) return [];
            using var stream = _fileSystem.OpenReadShared(_path, 64 * 1024);
            if (stream.Length >= FullScanThresholdBytes)
                return ReadCommittedMovesReverse(stream);
        }

        var entries = new List<JournalEntry>();
        ReadEntries(entry =>
        {
            if (entry.Type == FileOperationType.Move && entry.State == JournalState.Committed)
                entries.Add(entry);
        });
        return entries;
    }

    // CORE-08: every pass reads only the not-yet-parsed prefix [start, boundary) and parses it once, so a Move-sparse
    // journal costs O(file) instead of re-parsing the tail on each window doubling. `boundary` always sits on a line
    // start: the head line of a pass may be cut mid-line, so it is left for the next (earlier) pass to complete.
    private static List<JournalEntry> ReadCommittedMovesReverse(Stream stream)
    {
        if (!stream.CanSeek)
            return [];

        var length = stream.Length;
        var boundary = length;
        var window = 256L * 1024;
        var collected = new List<JournalEntry>(StartupCommittedMoveLimit); // file (ascending) order
        while (true)
        {
            var start = Math.Max(0, length - window);
            var buffer = new byte[(int)(boundary - start)];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);

            var lineStart = 0;
            var newBoundary = boundary;
            if (start > 0)
            {
                var firstNewline = Array.IndexOf(buffer, (byte)'\n');
                if (firstNewline < 0)
                {
                    // One line longer than the window: nothing parseable yet, widen and re-read the same prefix.
                    window *= 2;
                    continue;
                }
                lineStart = firstNewline + 1;
                newBoundary = start + lineStart;
            }

            var chunk = ParseCommittedMoves(buffer, lineStart);
            collected.InsertRange(0, chunk);

            if (collected.Count >= StartupCommittedMoveLimit || start == 0)
            {
                return collected.Count <= StartupCommittedMoveLimit
                    ? collected
                    : collected.GetRange(collected.Count - StartupCommittedMoveLimit, StartupCommittedMoveLimit);
            }
            boundary = newBoundary;
            window *= 2;
        }
    }

    private static List<JournalEntry> ParseCommittedMoves(byte[] buffer, int offset)
    {
        var entries = new List<JournalEntry>();
        var remaining = buffer.AsSpan(offset);
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf((byte)'\n'); // vectorized: the line split must not dominate a 25 MB scan
            var line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? default : remaining[(newline + 1)..];
            if (!line.IsEmpty && line[^1] == '\r') line = line[..^1];
            if (TryParseTailCommittedMove(line) is { } entry) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// The tail reader's line predicate (CORE-08): the line's committed Move, or null. Shared with
    /// <see cref="JournalCompactionPlan"/>, which must keep exactly the lines this reader returns.
    /// </summary>
    internal static JournalEntry? TryParseTailCommittedMove(ReadOnlySpan<byte> line)
    {
        // Perf (CORE-08): a compact-written Recycle/Copy line cannot be a Move, so skip its JSON parse. The exact
        // token never occurs inside a string value (there quotes are escaped as \"), so a Move line is never skipped.
        if (line.IsEmpty || IsRecycleOrCopyLine(line)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<JournalEntry>(line);
            return entry is { Type: FileOperationType.Move, State: JournalState.Committed } ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsRecycleOrCopyLine(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"Type\":\"Recycle\""u8) >= 0 || line.IndexOf("\"Type\":\"Copy\""u8) >= 0;

    public IReadOnlyList<JournalEntry> ReadPendingOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == JournalState.Prepared).ToList();

    public IReadOnlyList<JournalEntry> ReadFailedOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == JournalState.Failed).ToList();

    /// <summary>
    /// R2-F-17: the Recovery window's list (pending first, then failed) from ONE pass over the journal,
    /// instead of one full parse for <see cref="ReadPendingOperations"/> and another for <see cref="ReadFailedOperations"/>.
    /// </summary>
    public IReadOnlyList<JournalEntry> ReadPendingAndFailedOperations()
    {
        var latest = ComputeLatestEntries().Values;
        return latest.Where(entry => entry.State == JournalState.Prepared)
            .Concat(latest.Where(entry => entry.State == JournalState.Failed))
            .ToList();
    }

    // Retries append a new Prepared/Committed/Failed entry under the SAME Id as the
    // attempt they're retrying (RecoveryRetryService.RetryMoveOrCopy), so an Id's
    // state must be resolved from its most recent entry, not from "was any terminal
    // entry ever appended for this Id" — otherwise an old Failed entry permanently
    // shadows a later, still-in-flight retry under the same Id.
    private Dictionary<string, JournalEntry> ComputeLatestEntries()
    {
        var latest = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        ReadEntries(entry =>
        {
            // FA-01 (cross-process residue): another process's reconcile judged this operation from a stale snapshot and its
            // Failed landed after the owner's Committed (the recheck-then-append window cannot be closed without a file lock).
            // A reconcile verdict can only ever follow Prepared, so after Committed it is stale and must not win.
            if (entry.State == JournalState.Failed && IsReconcileCode(entry.ErrorCode)
                && latest.TryGetValue(entry.Id, out var previous) && previous.State == JournalState.Committed)
                return;
            latest[entry.Id] = entry;
        });
        return latest;
    }

    internal static bool IsReconcileCode(string? code) =>
        code is JournalErrors.PendingUnconfirmed or JournalErrors.SourceStillExistsAfterRecovery;

    private void ReadEntries(Action<JournalEntry> handle)
    {
        lock (_gate)
        {
            if (!_fileSystem.FileExists(_path)) return;
            using var stream = _fileSystem.OpenReadShared(_path, 64 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                // One JSON pass per line (JournalLineParser). Skipped, as before: malformed JSON, an unknown/missing Type
                // or State (the lenient enum converters would map it to Move/Prepared and invent a pending move), and
                // valid JSON lacking Id or Source (records do not enforce required members).
                if (JournalLineParser.TryParse(line) is { } entry) handle(entry);
            }
        }
    }

    /// <param name="preparedBeforeUtc">
    /// Only Prepared entries stamped before this instant are reconciled. Startup passes the moment it began, so an
    /// action this process starts while the (background) reconcile runs is never judged mid-flight.
    /// </param>
    /// <remarks>
    /// Q-R27: an entry whose live marker exists (<see cref="ILiveOperationRegistry.IsLive"/>) is still executing, possibly in
    /// another PhotoReview process that started it before this one launched (a long Move/Copy to a slow drive). It is
    /// skipped: left Prepared, nothing appended, not returned. The owner appends its outcome; a crashed owner's marker is
    /// gone with its process, so a real leftover is reconciled by the next start.
    /// </remarks>
    public IReadOnlyList<JournalEntry> ReconcilePendingOperations(DateTime? preparedBeforeUtc = null)
    {
        var reconciled = new List<JournalEntry>();
        foreach (var pending in ReadPendingOperations())
        {
            if (preparedBeforeUtc is { } cutoff && pending.TimestampUtc >= cutoff) continue;
            if (IsExecuting(pending.Id)) continue; // Q-R27: skip before any (possibly slow) file check
            if (pending.GroupMembers is { Count: > 0 })
            {
                var allCompleted = pending.GroupMembers.All(member => IsGroupMemberCompleted(pending, member));
                var entry = WithOutcome(pending, allCompleted ? JournalState.Committed : JournalState.Failed,
                    pending.Type == FileOperationType.Recycle ? JournalErrors.SourceStillExistsAfterRecovery : JournalErrors.PendingUnconfirmed);
                if (AppendIfStillPending(entry)) reconciled.Add(entry);
            }
            else if (pending.Type == FileOperationType.Recycle)
            {
                var state = _fileSystem.FileExists(pending.Source) ? JournalState.Failed : JournalState.Committed;
                var entry = WithOutcome(pending, state, JournalErrors.SourceStillExistsAfterRecovery);
                if (AppendIfStillPending(entry)) reconciled.Add(entry);
            }
            else if (pending.Type is FileOperationType.Move or FileOperationType.Copy)
            {
                var sourceExists = _fileSystem.FileExists(pending.Source);
                var destinationStat = pending.Destination is not null ? _fileSystem.GetFileStat(pending.Destination) : null;
                var destinationMatches = destinationStat is not null && destinationStat.Length == pending.Size;
                // Move must have removed the source to count as done; Copy is expected
                // to leave the source in place, so requiring its absence would reconcile
                // every genuinely-successful pending Copy as Failed.
                var state = pending.Type == FileOperationType.Move
                    ? (!sourceExists && destinationMatches ? JournalState.Committed : JournalState.Failed)
                    : (destinationMatches ? JournalState.Committed : JournalState.Failed);
                var entry = WithOutcome(pending, state, JournalErrors.PendingUnconfirmed);
                if (AppendIfStillPending(entry)) reconciled.Add(entry);
            }
        }
        return reconciled;
    }

    private bool IsGroupMemberCompleted(JournalEntry group, JournalGroupMember member)
    {
        if (group.Type == FileOperationType.Recycle)
        {
            var exists = _fileSystem.FileExists(member.Source);
            return group.Undo == true ? exists : !exists;
        }

        var destinationPath = member.Destination;
        var sourcePath = member.Source;
        if (group.Undo == true && group.Type == FileOperationType.Move)
            (sourcePath, destinationPath) = (destinationPath ?? string.Empty, sourcePath);
        var destination = !string.IsNullOrWhiteSpace(destinationPath) ? _fileSystem.GetFileStat(destinationPath) : null;
        var destinationMatches = destination is not null && destination.Length == member.Size;
        var sourceExists = _fileSystem.FileExists(sourcePath);
        return group.Type switch
        {
            FileOperationType.Move => !sourceExists && destinationMatches,
            FileOperationType.Copy => destinationMatches,
            _ => false
        };
    }

    // FA-01: the verdict was computed from a stale snapshot; another process sharing this journal may have appended a
    // terminal entry (Committed/Failed/Dismissed) for the same Id meanwhile. Re-read the latest state under the journal
    // lock and append only if the operation is still Prepared, so a later Committed is never overwritten by a false Failed.
    // (Best effort across processes: the check and append are atomic per instance, and the window is a single read.)
    private bool AppendIfStillPending(JournalEntry outcome)
    {
        lock (_gate)
        {
            JournalEntry? latest = null;
            ReadEntries(entry => { if (string.Equals(entry.Id, outcome.Id, StringComparison.Ordinal)) latest = entry; });
            if (latest is null || latest.State != JournalState.Prepared) return false;
            // Q-R27: re-checked after the re-read. A marker is taken before its Prepared entry is appended, so a retry that
            // re-prepared this Id since the snapshot (same Id, RecoveryRetryService) is already visible as live here.
            if (IsExecuting(outcome.Id)) return false;
            Append(outcome);
            return true;
        }
    }

    /// <summary>
    /// P02 (R09-style guard for Recovery retry): appends <paramref name="entry"/> only if, for <paramref name="anchor"/>'s
    /// Id, the journal holds <paramref name="anchor"/> followed by exactly <paramref name="ownSince"/> and nothing else --
    /// i.e. no other writer (another process sharing this journal on <c>InstanceMode.PerFolder</c>, or another retry)
    /// appended a record for the Id since the snapshot the caller acted on. Returns false (nothing appended) otherwise.
    /// When <paramref name="anchor"/> itself is not in the journal (a caller-built snapshot), the Id's whole history
    /// counts as "since": it must then be exactly <paramref name="ownSince"/>. A retry passes its Failed snapshot as the anchor:
    /// an empty <paramref name="ownSince"/> before Prepared (the snapshot is still the latest entry), and its own Prepared
    /// before Failed, so a loser's Failed can never land after the winner's Committed. Checking the whole tail (not only
    /// the latest entry) also covers a loser whose Prepared was appended after the winner's Committed.
    /// Same read-latest/compare/act-only-if-still-current discipline as <see cref="AppendIfStillPending"/> and
    /// <see cref="Dismiss"/>: atomic per instance; across processes the window is a single read.
    /// </summary>
    internal bool AppendIfUnchangedSince(JournalEntry anchor, IReadOnlyList<JournalEntry> ownSince, JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(ownSince);
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var history = new List<JournalEntry>();
            ReadEntries(e => { if (string.Equals(e.Id, anchor.Id, StringComparison.Ordinal)) history.Add(e); });
            var at = history.FindLastIndex(e => JournalEntriesEqual(e, anchor)); // -1: not found, the whole history is "since"
            if (history.Count - at - 1 != ownSince.Count) return false;
            for (var i = 0; i < ownSince.Count; i++)
            {
                if (!JournalEntriesEqual(history[at + 1 + i], ownSince[i])) return false;
            }
            Append(entry);
            return true;
        }
    }

    private static bool JournalEntriesEqual(JournalEntry left, JournalEntry right)
    {
        if (left with { GroupMembers = null } != right with { GroupMembers = null }) return false;
        return left.GroupMembers is null || right.GroupMembers is null
            ? left.GroupMembers is null && right.GroupMembers is null
            : left.GroupMembers.SequenceEqual(right.GroupMembers);
    }

    internal const long CompactionThresholdBytes = FullScanThresholdBytes;

    /// <summary>Compaction rewrites the journal only when it drops at least this share of its bytes (no rewrite per start for a few lines).</summary>
    internal const double CompactionMinGain = 0.25;

    /// <summary>A replacement file left by a compaction that crashed is removed once it is this old (a younger one may be another process's, in progress).</summary>
    internal static readonly TimeSpan StaleCompactionFileAge = TimeSpan.FromHours(1);

    internal const string CompactionFileSuffix = ".compact.tmp";

    /// <summary>
    /// Rewrites the append-only journal without the lines no reader can observe any more (<see cref="JournalCompactionPlan"/>:
    /// entries superseded by a later Committed/Dismissed entry of the same Id). Never throws for I/O; the result says what
    /// happened. Call it off the UI thread (startup runs it after reconcile, <see cref="JournalStartupRecovery"/>).
    /// <para><b>Concurrency and crash safety.</b> Other PhotoReview processes may append at any moment; there is no
    /// cross-process lock besides the file's share modes, and this uses exactly those:</para>
    /// <list type="number">
    /// <item>Snapshot the file (shared read, nobody blocked), cut after its last '\n', plan, write the kept lines to a new
    /// replacement file and fsync it. The journal is not touched.</item>
    /// <item>Under this instance's lock, open the journal DENYING writers. Appends in every process now get a sharing
    /// violation and retry (<see cref="AppendAttempts"/>, ~100 ms budget); an append in progress makes this open fail and
    /// compaction gives up (<see cref="JournalCompactionOutcome.Busy"/>).</item>
    /// <item>Re-read the file through that handle. Its first bytes must equal the snapshot (append-only: anything else means
    /// another compaction replaced it, so give up); the bytes after it are the lines appended since the snapshot and are
    /// copied verbatim to the replacement (a torn last line gets a newline, like the append tail repair).</item>
    /// <item>fsync, then ONE atomic rename of the replacement over the journal while the deny-writers handle is still open
    /// (<see cref="IStagedReplacement.ReplaceAtomically"/>). No write can land in the old file between the re-read and the
    /// rename, so no entry is lost; the next append in any process opens the new file.</item>
    /// </list>
    /// <para>A crash before the rename leaves the journal untouched (plus a stale replacement file, removed by a later
    /// compaction); after it, the new file is complete and fsynced. The critical section of step 2-4 is a read of a file
    /// the snapshot just cached plus one rename: milliseconds, well inside the appenders' retry budget.</para>
    /// </summary>
    public JournalCompactionResult TryCompact()
    {
        if (_compactionFiles is null) return new JournalCompactionResult(JournalCompactionOutcome.NotSupported, 0, 0);
        try
        {
            RemoveStaleCompactionFiles();
            if (!_fileSystem.FileExists(_path)) return new JournalCompactionResult(JournalCompactionOutcome.BelowThreshold, 0, 0);

            byte[] snapshot;
            using (var stream = _fileSystem.OpenReadShared(_path, 64 * 1024))
                snapshot = ReadToEnd(stream);
            var snapshotLength = snapshot.AsSpan().LastIndexOf((byte)'\n') + 1; // a partial last line belongs to the delta
            if (snapshotLength < CompactionThresholdBytes)
                return new JournalCompactionResult(JournalCompactionOutcome.BelowThreshold, snapshot.Length, snapshot.Length);

            var plan = JournalCompactionPlan.Build(snapshot.AsSpan(0, snapshotLength));
            if (snapshotLength - plan.Kept.Length < snapshotLength * CompactionMinGain)
                return new JournalCompactionResult(JournalCompactionOutcome.NotWorthIt, snapshot.Length, snapshot.Length);

            var tempPath = _path + "." + Guid.NewGuid().ToString("N") + CompactionFileSuffix;
            using var replacement = _compactionFiles.CreateStagedReplacement(tempPath);
            replacement.Write(plan.Kept);
            replacement.FlushToDisk();

            lock (_gate)
            {
                Stream locked;
                try
                {
                    locked = _compactionFiles.OpenReadDenyWriters(_path);
                }
                catch (IOException ex) when (IsSharingOrLockViolation(ex))
                {
                    return new JournalCompactionResult(JournalCompactionOutcome.Busy, snapshot.Length, snapshot.Length);
                }
                using (locked)
                {
                    var current = ReadToEnd(locked);
                    if (current.Length < snapshotLength || !current.AsSpan(0, snapshotLength).SequenceEqual(snapshot.AsSpan(0, snapshotLength)))
                        return new JournalCompactionResult(JournalCompactionOutcome.Changed, current.Length, current.Length);
                    var delta = current.AsSpan(snapshotLength);
                    var newLength = (long)plan.Kept.Length + delta.Length;
                    if (!delta.IsEmpty)
                    {
                        replacement.Write(delta);
                        if (delta[^1] != (byte)'\n')
                        {
                            var newline = Encoding.UTF8.GetBytes(Environment.NewLine);
                            replacement.Write(newline);
                            newLength += newline.Length;
                        }
                        replacement.FlushToDisk();
                    }
                    replacement.ReplaceAtomically(_path);
                    return new JournalCompactionResult(JournalCompactionOutcome.Compacted, current.Length, newLength);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Nothing was renamed (the rename is the last step and atomic): the journal is as it was.
            return new JournalCompactionResult(JournalCompactionOutcome.Failed, 0, 0, ex.Message);
        }
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void RemoveStaleCompactionFiles()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !_fileSystem.DirectoryExists(dir)) return;
        foreach (var file in _fileSystem.EnumerateFiles(dir, Path.GetFileName(_path) + ".*" + CompactionFileSuffix).ToList())
        {
            if (!file.EndsWith(CompactionFileSuffix, StringComparison.OrdinalIgnoreCase)) continue; // 8.3 pattern quirks
            if (_fileSystem.GetFileStat(file) is not { } stat || _clock.UtcNow - stat.LastWriteUtc < StaleCompactionFileAge) continue;
            try
            {
                _fileSystem.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open (a compaction in progress after all) or not ours: leave it.
            }
        }
    }

    private bool IsExecuting(string operationId) => _liveOperations.IsLive(operationId);

    // Journal text is invariant (AGENTS.md rule 4): a failure stores its code plus English, never the UI language.
    private JournalEntry WithOutcome(JournalEntry pending, JournalState state, string failureCode)
    {
        var failed = state == JournalState.Failed;
        return pending with
        {
            State = state,
            TimestampUtc = _clock.UtcNow,
            Error = failed ? JournalErrors.EnglishText(failureCode) : null,
            ErrorCode = failed ? failureCode : null,
        };
    }
}
