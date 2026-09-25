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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Undo = null);

public sealed class OperationJournal
{
    private const long FullScanThresholdBytes = 1 * 1024 * 1024;
    private const int StartupCommittedMoveLimit = 200;
    private readonly string _path;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Func<JournalDurability> _durability;
    private readonly Action<TimeSpan> _appendRetryDelay;

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
    public OperationJournal(IAppPaths paths, IFileSystem fileSystem, IClock clock, Func<JournalDurability>? durability = null,
        Action<TimeSpan>? appendRetryDelay = null)
    {
        _durability = durability ?? (() => JournalDurability.Fast);
        _appendRetryDelay = appendRetryDelay ?? Thread.Sleep;
        ArgumentNullException.ThrowIfNull(paths);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _path = paths.JournalFile;
    }

    /// <summary>Mode applied to the next write (re-read from the provider every time, so a Settings change needs no restart).</summary>
    public JournalDurability Durability => _durability();

    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AppendLines([entry]);
    }

    // Clearing a Recovery item appends a Dismissed entry under the same Id (the journal is
    // append-only); latest-entry resolution then drops it from pending/failed. No file is touched.
    public IReadOnlyList<JournalEntry> Dismiss(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = _clock.UtcNow;
        var dismissed = entries.Select(entry => entry with { State = JournalState.Dismissed, TimestampUtc = now, Error = null }).ToList();
        if (dismissed.Count > 0) AppendLines(dismissed);
        return dismissed;
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
            // Perf (CORE-08): a compact-written Recycle/Copy line cannot be a Move, so skip its JSON parse. The exact
            // token never occurs inside a string value (there quotes are escaped as \"), so a Move line is never skipped.
            if (line.IsEmpty || IsRecycleOrCopyLine(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                if (entry is { Type: FileOperationType.Move, State: JournalState.Committed })
                    entries.Add(entry);
            }
            catch (JsonException) { }
        }
        return entries;
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

    private static bool IsReconcileCode(string? code) =>
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
                try
                {
                    // The lenient enum converters map an unknown/missing Type or State to the first member (Move/Prepared);
                    // for a journal that would invent a pending move, so such lines are skipped instead.
                    if (!HasRecognizedEnums(line)) continue;
                    var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                    // Valid JSON can still lack required members (records do not enforce them); skip such lines.
                    if (entry is not null && !string.IsNullOrEmpty(entry.Id) && entry.Source is not null) handle(entry);
                }
                catch (JsonException) { }
            }
        }
    }

    private static bool HasRecognizedEnums(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && IsKnown<FileOperationType>(root, nameof(JournalEntry.Type), "Delete")
            && IsKnown<JournalState>(root, nameof(JournalEntry.State), alias: null);
    }

    private static bool IsKnown<T>(JsonElement root, string property, string? alias) where T : struct, Enum
    {
        if (!root.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { } text
                && (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                    || (alias is not null && string.Equals(text.Trim(), alias, StringComparison.OrdinalIgnoreCase))),
            JsonValueKind.Number => value.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number),
            _ => false,
        };
    }

    /// <param name="preparedBeforeUtc">
    /// Only Prepared entries stamped before this instant are reconciled. Startup passes the moment it began, so an
    /// action this process starts while the (background) reconcile runs is never judged mid-flight.
    /// </param>
    public IReadOnlyList<JournalEntry> ReconcilePendingOperations(DateTime? preparedBeforeUtc = null)
    {
        var reconciled = new List<JournalEntry>();
        foreach (var pending in ReadPendingOperations())
        {
            if (preparedBeforeUtc is { } cutoff && pending.TimestampUtc >= cutoff) continue;
            if (pending.Type == FileOperationType.Recycle)
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
            Append(outcome);
            return true;
        }
    }

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
