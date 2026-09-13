using PhotoReview.App;
using System.IO;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "PhotoReview-Test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", Path.Combine(root, "app-data"));
try
{
    var sessionStore = new SessionStore();
    var state = new SessionState { Folder = root, CurrentPath = Path.Combine(root, "one.jpg"), Skipped = [Path.Combine(root, "skip.jpg")] };
    sessionStore.Save(state);
    var loaded = sessionStore.Load(root);
    Check(loaded.CurrentPath == state.CurrentPath && loaded.Skipped.Count == 1, "Session save/load", failures);

    var journal = new OperationJournal();
    var source = Path.Combine(root, "source.jpg");
    var destination = Path.Combine(root, "dest", "source.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(source, [1, 2, 3, 4]);
    var info = new FileInfo(source);
    var operationId = Guid.NewGuid().ToString("N");
    journal.Append(new JournalEntry(operationId, "Move", "Prepared", source, destination, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
    File.Move(source, destination);
    journal.Append(new JournalEntry(operationId, "Move", "Committed", source, destination, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
    var committed = journal.ReadCommittedMoves();
    Check(committed.Any(x => x.Source == source && x.Destination == destination), "Journal committed Move", failures);
    Check(journal.ReadPendingOperations().Count == 0, "Journal has no pending committed Move", failures);
    Check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Move preserves bytes", failures);

    if (failures.Count > 0) { foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure); Environment.ExitCode = 1; }
    else Console.WriteLine("PASS: all PhotoReview persistence/journal tests");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

static void Check(bool condition, string name, List<string> failures) { if (condition) Console.WriteLine("PASS: " + name); else failures.Add(name); }
