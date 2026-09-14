using PhotoReview.App;
using System.IO;

var failures = new List<string>();
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml.cs")))
    projectRoot = Directory.GetCurrentDirectory();
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

    var cache = new BoundedLruCache<string, string>(4, value => value.Length, StringComparer.Ordinal);
    cache.Set("a", "aa");
    cache.Set("b", "b");
    Check(cache.TryGet("a", out _), "LRU retains recently accessed entry", failures);
    cache.Set("c", "cc");
    Check(!cache.TryGet("b", out _), "LRU evicts least recently used entry", failures);
    Check(cache.CurrentSize <= 4, "LRU enforces byte capacity", failures);

    var mainWindow = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml.cs"));
    var appSettings = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "AppSettings.cs"));
    Check(appSettings.Contains("LoadingMode", StringComparison.Ordinal), "LoadingMode setting exists", failures);
    Check(appSettings.Contains("Fast", StringComparison.Ordinal) && appSettings.Contains("Preview", StringComparison.Ordinal) && appSettings.Contains("Original", StringComparison.Ordinal), "LoadingMode has Fast, Preview, and Original options", failures);
    Check(appSettings.Contains("= \"Preview\"", StringComparison.Ordinal) || appSettings.Contains("= LoadingMode.Preview", StringComparison.Ordinal), "LoadingMode defaults to Preview", failures);
    Check(appSettings.Contains("IsValidLoadingMode", StringComparison.Ordinal) || appSettings.Contains("ValidateLoadingMode", StringComparison.Ordinal) || appSettings.Contains("Invalid LoadingMode", StringComparison.Ordinal), "LoadingMode validation exists", failures);
    Check(mainWindow.Contains("LoadingMode", StringComparison.Ordinal), "MainWindow reads LoadingMode", failures);
    Check(mainWindow.Contains("Thumbnail", StringComparison.Ordinal) && mainWindow.Contains("Preview", StringComparison.Ordinal), "MainWindow loading modes have thumbnail/preview contract", failures);
    var shortcuts = ShortcutMappings.Default();
    Check(shortcuts.Next == "Right" && shortcuts.Previous == "Left" && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Next)") && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Previous)"), "Keyboard navigation uses arrow keys", failures);
    Check(shortcuts.MoveToFolder2 == "Enter" && mainWindow.Contains("ClassifyCurrentAsync(2)"), "Enter maps to category 2 move", failures);
    Check(shortcuts.SendToRecycleBin == "Delete" && mainWindow.Contains("ClassifyCurrentAsync(3)"), "Delete maps to Recycle Bin", failures);
    Check(!mainWindow.Contains("Key.D1") && !mainWindow.Contains("Key.NumPad1"), "No number-1 shortcut required", failures);
    Check(mainWindow.Contains("Key.Space"), "Space skip shortcut exists", failures);
    Check(mainWindow.Contains("Key.PageUp") && mainWindow.Contains("Key.PageDown") && mainWindow.Contains("NavigateSiblingFolderAsync"), "PageUp/PageDown navigate sibling folders", failures);
    Check(mainWindow.Contains("Key.Home") && mainWindow.Contains("ShowImageAsync(0)"), "Home navigates to first image", failures);

    var association = Path.Combine(projectRoot, "outputs", "install-photo-review-association.ps1");
    var associationText = File.ReadAllText(association);
    Check(associationText.Contains("$progId\\shell\\open\\command") && associationText.Contains("\"%1\""), "File association command is registered", failures);

    if (failures.Count > 0) { foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure); Environment.ExitCode = 1; }
    else Console.WriteLine("PASS: all PhotoReview persistence/journal/keyboard/release-contract tests");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

static void Check(bool condition, string name, List<string> failures) { if (condition) Console.WriteLine("PASS: " + name); else failures.Add(name); }
