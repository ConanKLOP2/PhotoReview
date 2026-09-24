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
/// skip unknown members) still read new files.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null);

public sealed class OperationJournal
{
    private const long FullScanThresholdBytes = 1 * 1024 * 1024;
    private const int StartupCommittedMoveLimit = 200;
    private readonly string _path;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public OperationJournal()
        : this(PhotoReview.Core.AppPaths.FromEnvironment(), new PhysicalFileSystem(), new SystemClock())
    {
    }

    public OperationJournal(IAppPaths paths, IFileSystem fileSystem, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _path = paths.JournalFile;
    }

    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                _fileSystem.CreateDirectory(dir);
            }
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            using var stream = _fileSystem.OpenAppendDurable(_path);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            if (stream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
            else
            {
                stream.Flush();
            }
        }
    }

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

    private static List<JournalEntry> ReadCommittedMovesReverse(Stream stream)
    {
        if (!stream.CanSeek)
            return [];

        var window = 256 * 1024;
        while (true)
        {
            var start = Math.Max(0, stream.Length - window);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[(int)(stream.Length - start)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var entries = new List<JournalEntry>(StartupCommittedMoveLimit);
            var lineStart = 0;
            for (var i = 0; i <= read; i++)
            {
                if (i != read && buffer[i] is not (byte)'\n') continue;
                var length = i - lineStart;
                if (length > 0 && buffer[lineStart + length - 1] == '\r') length--;
                if (length > 0)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<JournalEntry>(buffer.AsSpan(lineStart, length));
                        if (entry is { Type: FileOperationType.Move, State: JournalState.Committed })
                            entries.Add(entry);
                    }
                    catch (JsonException) { }
                }
                lineStart = i + 1;
            }

            if (entries.Count >= StartupCommittedMoveLimit || start == 0)
            {
                return entries.Count <= StartupCommittedMoveLimit
                    ? entries
                    : entries.Skip(entries.Count - StartupCommittedMoveLimit).ToList();
            }
            window *= 2;
        }
    }

    public IReadOnlyList<JournalEntry> ReadPendingOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == JournalState.Prepared).ToList();

    public IReadOnlyList<JournalEntry> ReadFailedOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == JournalState.Failed).ToList();

    // Retries append a new Prepared/Committed/Failed entry under the SAME Id as the
    // attempt they're retrying (RecoveryRetryService.RetryMoveOrCopy), so an Id's
    // state must be resolved from its most recent entry, not from "was any terminal
    // entry ever appended for this Id" — otherwise an old Failed entry permanently
    // shadows a later, still-in-flight retry under the same Id.
    private Dictionary<string, JournalEntry> ComputeLatestEntries()
    {
        var latest = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        ReadEntries(entry => latest[entry.Id] = entry);
        return latest;
    }

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
                    var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                    if (entry is not null) handle(entry);
                }
                catch (JsonException) { }
            }
        }
    }

    public IReadOnlyList<JournalEntry> ReconcilePendingOperations()
    {
        var reconciled = new List<JournalEntry>();
        foreach (var pending in ReadPendingOperations())
        {
            if (pending.Type == FileOperationType.Recycle)
            {
                var state = _fileSystem.FileExists(pending.Source) ? JournalState.Failed : JournalState.Committed;
                var entry = WithOutcome(pending, state, JournalErrors.SourceStillExistsAfterRecovery);
                Append(entry);
                reconciled.Add(entry);
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
                Append(entry);
                reconciled.Add(entry);
            }
        }
        return reconciled;
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
