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
        var lines = ReadLinesSnapshot();
        if (lines.Length == 0) return [];
        var entries = new List<JournalEntry>();
        foreach (var line in lines)
        {
            try { var entry = JsonSerializer.Deserialize<JournalEntry>(line); if (entry is not null && entry.Type == "Move" && entry.State == "Committed") entries.Add(entry); }
            catch (JsonException) { }
        }
        return entries;
    }

    public IReadOnlyList<JournalEntry> ReadPendingOperations()
    {
        var lines = ReadLinesSnapshot();
        if (lines.Length == 0) return [];
        var prepared = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                if (entry is null) continue;
                if (entry.State == "Prepared") prepared[entry.Id] = entry;
                if (entry.State is "Committed" or "Failed") completed.Add(entry.Id);
            }
            catch (JsonException) { }
        }
        return prepared.Where(x => !completed.Contains(x.Key)).Select(x => x.Value).ToList();
    }

    public IReadOnlyList<JournalEntry> ReadFailedOperations()
    {
        var lines = ReadLinesSnapshot();
        if (lines.Length == 0) return [];
        var failures = new List<JournalEntry>();
        foreach (var line in lines)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                if (entry?.State == "Failed") failures.Add(entry);
            }
            catch (JsonException) { }
        }
        return failures;
    }

    private string[] ReadLinesSnapshot()
    {
        lock (_gate)
        {
            return File.Exists(_path) ? File.ReadAllLines(_path) : [];
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
                var state = !sourceExists && destinationMatches ? "Committed" : "Failed";
                var error = state == "Failed" ? "Không thể xác nhận operation pending; không tự động replay." : null;
                var entry = pending with { State = state, TimestampUtc = DateTime.UtcNow, Error = error };
                Append(entry); reconciled.Add(entry);
            }
        }
        return reconciled;
    }
}
