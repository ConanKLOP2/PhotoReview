using System.Text.Json;
using System.IO;
using System.Text;

namespace PhotoReview.App;

public sealed record JournalEntry(string Id, string Type, string State, string Source, string? Destination, long Size, DateTime LastWriteUtc, DateTime TimestampUtc, string? Error = null);

public sealed class OperationJournal
{
    private readonly string _path = Path.Combine(Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "Data"), "operations.jsonl");
    private readonly object _gate = new();

    public void Append(JournalEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<JournalEntry> ReadCommittedMoves()
    {
        var entries = new List<JournalEntry>();
        ReadEntries(entry => { if (entry.Type == "Move" && entry.State == "Committed") entries.Add(entry); });
        return entries;
    }

    public IReadOnlyList<JournalEntry> ReadPendingOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == "Prepared").ToList();

    public IReadOnlyList<JournalEntry> ReadFailedOperations() =>
        ComputeLatestEntries().Values.Where(entry => entry.State == "Failed").ToList();

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
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
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
            if (pending.Type == "Recycle")
            {
                var state = File.Exists(pending.Source) ? "Failed" : "Committed";
                var error = state == "Failed" ? "Nguồn vẫn tồn tại sau khi khôi phục phiên." : null;
                var entry = pending with { State = state, TimestampUtc = DateTime.UtcNow, Error = error };
                Append(entry); reconciled.Add(entry);
            }
            else if (pending.Type is "Move" or "Copy")
            {
                var sourceExists = File.Exists(pending.Source);
                var destinationExists = pending.Destination is not null && File.Exists(pending.Destination);
                var destinationMatches = destinationExists && new FileInfo(pending.Destination!).Length == pending.Size;
                // Move must have removed the source to count as done; Copy is expected
                // to leave the source in place, so requiring its absence would reconcile
                // every genuinely-successful pending Copy as Failed.
                var state = pending.Type == "Move"
                    ? (!sourceExists && destinationMatches ? "Committed" : "Failed")
                    : (destinationMatches ? "Committed" : "Failed");
                var error = state == "Failed" ? "Không thể xác nhận operation pending; không tự động replay." : null;
                var entry = pending with { State = state, TimestampUtc = DateTime.UtcNow, Error = error };
                Append(entry); reconciled.Add(entry);
            }
        }
        return reconciled;
    }
}
