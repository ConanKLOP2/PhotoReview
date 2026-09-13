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
}
