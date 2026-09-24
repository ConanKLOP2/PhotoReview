using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

public sealed record JournalEntry(
    string Id,
    FileOperationType Type,
    JournalState State,
    string Source,
    string? Destination,
    long Size,
    DateTime LastWriteUtc,
    DateTime TimestampUtc,
    string? Error = null);

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
            using var stream = _fileSystem.OpenAppendDurable(_path);
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
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
                var error = state == JournalState.Failed ? "Nguồn vẫn tồn tại sau khi khôi phục phiên." : null;
                var entry = pending with { State = state, TimestampUtc = _clock.UtcNow, Error = error };
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
                var error = state == JournalState.Failed ? "Không thể xác nhận operation pending; không tự động replay." : null;
                var entry = pending with { State = state, TimestampUtc = _clock.UtcNow, Error = error };
                Append(entry);
                reconciled.Add(entry);
            }
        }
        return reconciled;
    }
}
