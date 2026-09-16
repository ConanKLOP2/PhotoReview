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

    public IReadOnlyList<JournalEntry> ReadPendingOperations()
    {
        var prepared = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        ReadEntries(entry =>
        {
            if (entry.State == "Prepared") prepared[entry.Id] = entry;
            if (entry.State is "Committed" or "Failed") completed.Add(entry.Id);
        });
        return prepared.Where(x => !completed.Contains(x.Key)).Select(x => x.Value).ToList();
    }

    public IReadOnlyList<JournalEntry> ReadFailedOperations()
    {
        var failures = new List<JournalEntry>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        ReadEntries(entry =>
        {
            if (entry.State == "Failed") failures.Add(entry);
            if (entry.State == "Committed") completed.Add(entry.Id);
        });
        return failures.Where(entry => !completed.Contains(entry.Id)).ToList();
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
                var state = !sourceExists && destinationMatches ? "Committed" : "Failed";
                var error = state == "Failed" ? "Không thể xác nhận operation pending; không tự động replay." : null;
                var entry = pending with { State = state, TimestampUtc = DateTime.UtcNow, Error = error };
                Append(entry); reconciled.Add(entry);
            }
        }
        return reconciled;
    }
}
