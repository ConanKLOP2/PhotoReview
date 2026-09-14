using System.Text.Json;
using System.IO;

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
            File.AppendAllText(_path, line);
        }
    }

    public IReadOnlyList<JournalEntry> ReadCommittedMoves()
    {
        if (!File.Exists(_path)) return [];
        var entries = new List<JournalEntry>();
        foreach (var line in File.ReadLines(_path))
        {
            try { var entry = JsonSerializer.Deserialize<JournalEntry>(line); if (entry is not null && entry.Type == "Move" && entry.State == "Committed") entries.Add(entry); }
            catch (JsonException) { }
        }
        return entries;
    }

    public IReadOnlyList<JournalEntry> ReadPendingOperations()
    {
        if (!File.Exists(_path)) return [];
        var prepared = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(_path))
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
        }
        return reconciled;
    }
}
