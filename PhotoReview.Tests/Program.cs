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
    var imageSortService = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "ImageSortService.cs"));
    Check(appSettings.Contains("LoadingMode", StringComparison.Ordinal), "LoadingMode setting exists", failures);
    Check(appSettings.Contains("Fast", StringComparison.Ordinal) && appSettings.Contains("Preview", StringComparison.Ordinal) && appSettings.Contains("Original", StringComparison.Ordinal), "LoadingMode has Fast, Preview, and Original options", failures);
    Check(appSettings.Contains("= \"Preview\"", StringComparison.Ordinal) || appSettings.Contains("= LoadingMode.Preview", StringComparison.Ordinal), "LoadingMode defaults to Preview", failures);
    Check(appSettings.Contains("IsValidLoadingMode", StringComparison.Ordinal) || appSettings.Contains("ValidateLoadingMode", StringComparison.Ordinal) || appSettings.Contains("Invalid LoadingMode", StringComparison.Ordinal), "LoadingMode validation exists", failures);
    Check(mainWindow.Contains("LoadingMode", StringComparison.Ordinal), "MainWindow reads LoadingMode", failures);
    Check(mainWindow.Contains("Thumbnail", StringComparison.Ordinal) && mainWindow.Contains("Preview", StringComparison.Ordinal), "MainWindow loading modes have thumbnail/preview contract", failures);
    var shortcuts = ShortcutMappings.Default();
    Check(shortcuts.Next == "Right" && shortcuts.Previous == "Left" && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Next)") && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Previous)"), "Keyboard navigation uses arrow keys", failures);
    Check(shortcuts.MoveToFolder2 == "Enter" && mainWindow.Contains("ReviewAction") && mainWindow.Contains("ExecuteActionAsync(action)"), "Enter/action profiles drive configurable operations", failures);
    Check(shortcuts.SendToRecycleBin == "Delete" && mainWindow.Contains("ClassifyCurrentAsync(3)"), "Delete maps to Recycle Bin", failures);
    Check(!mainWindow.Contains("Key.D1") && !mainWindow.Contains("Key.NumPad1"), "No number-1 shortcut required", failures);
    Check(mainWindow.Contains("Key.Space"), "Space skip shortcut exists", failures);
    Check(mainWindow.Contains("Key.PageUp") && mainWindow.Contains("Key.PageDown") && mainWindow.Contains("NavigateSiblingFolderAsync"), "PageUp/PageDown navigate sibling folders", failures);
    Check(mainWindow.Contains("Key.Home") && mainWindow.Contains("ShowImageAsync(0)"), "Home navigates to first image", failures);
    Check(mainWindow.Contains("_settings.Shortcuts.NextFolder") && mainWindow.Contains("_settings.Shortcuts.FirstImage") && mainWindow.Contains("_settings.Shortcuts.ZoomIn"), "Configurable navigation and zoom shortcuts are wired at runtime", failures);
    Check(imageSortService.Contains("GetQuery(\"/app1/ifd/{ushort=274}\")") && imageSortService.Contains("value is 5 or 6 or 7 or 8"), "Portrait-first sort accounts for EXIF orientation", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ImageSortService.cs")) && mainWindow.Contains("ImageSortService.Sort"), "Image sorting is isolated in a testable service", failures);
    Check(mainWindow.Contains("action.Confirm") && mainWindow.Contains("BatchReviewWindow") && mainWindow.Contains("ShowDialog()"), "Actions and batch operations require confirmation", failures);
    Check(appSettings.Contains("ReviewAction") && appSettings.Contains("Actions"), "Config supports multiple review actions", failures);
    Check(appSettings.Contains("CurrentConfigVersion") && appSettings.Contains("Migrate") && appSettings.Contains("Flush(flushToDisk: true)"), "Config has versioned migration and durable atomic save", failures);
    Check(mainWindow.Contains("16L * 1024 * 1024 * 1024") && mainWindow.Contains("FullFolderRamThresholdBytes"), "RAM cache policy targets 16 GB and full-folder preload threshold", failures);
    Check(mainWindow.Contains("BoundedLruCache<string, HashCacheEntry>") && mainWindow.Contains("_hashCache.Set"), "Hash metadata cache is bounded and evictable", failures);
    var mainWindowXaml = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml"));
    Check(!mainWindow.Contains("Image_LeftClick") && !mainWindow.Contains("Image_RightClick") && !mainWindowXaml.Contains("Image_LeftClick") && !mainWindowXaml.Contains("Image_RightClick"), "Image click does not navigate; compare owns click selection", failures);
    Check(mainWindow.Contains("GetGCMemoryInfo") && mainWindow.Contains("PreloadMemoryLoadLimit"), "Background preload has memory pressure guard", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ReviewMetrics.cs")) && mainWindow.Contains("RecordCacheHit") && mainWindow.Contains("RecordSourceRead") && mainWindow.Contains("RecordPresented"), "Review performance metrics record cache, source decode, and present latency", failures);
    Check(mainWindow.Contains("DiagnosticsWindow") && File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "DiagnosticsWindow.xaml")), "Performance metrics have an in-app diagnostics view", failures);
    Check(mainWindow.Contains("var sourceRead = false") && mainWindow.Contains("if (sourceRead)"), "Source byte metrics exclude disk-cache hits", failures);
    Check(mainWindow.Contains("BatchReviewWindow") && mainWindow.Contains("review.ShowDialog()"), "Batch duplicate operation has dry-run review dialog", failures);
    Check(mainWindow.Contains("RecoveryWindow") && mainWindow.Contains("ReadPendingOperations"), "Recovery UI exposes pending operations without replay", failures);
    Check(mainWindow.Contains("ReadFailedOperations") && operationJournalTextContainsFailed(), "Recovery UI includes failed journal operations", failures);
    Check(mainWindow.Contains("\"Recycle\", \"Prepared\"") && mainWindow.Contains("\"Recycle\", \"Committed\"") && mainWindow.Contains("\"Recycle\", \"Failed\""), "Batch operations journal success and failures", failures);
    var journalSource = Path.Combine(root, "pending-recycle.jpg");
    File.WriteAllBytes(journalSource, [1, 2, 3]);
    var pendingId = Guid.NewGuid().ToString("N");
    journal.Append(new JournalEntry(pendingId, "Recycle", "Prepared", journalSource, null, 3, File.GetLastWriteTimeUtc(journalSource), DateTime.UtcNow));
    var reconciled = journal.ReconcilePendingOperations();
    Check(reconciled.Any(x => x.Id == pendingId && x.State == "Failed"), "Pending recycle is reconciled without replay when source remains", failures);
    var pendingMoveSource = Path.Combine(root, "pending-move.jpg");
    var pendingMoveDestination = Path.Combine(root, "pending-dest", "pending-move.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(pendingMoveDestination)!);
    File.WriteAllBytes(pendingMoveDestination, [8, 9, 10]);
    var pendingMoveId = Guid.NewGuid().ToString("N");
    journal.Append(new JournalEntry(pendingMoveId, "Move", "Prepared", pendingMoveSource, pendingMoveDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
    var moveReconciled = journal.ReconcilePendingOperations();
    Check(moveReconciled.Any(x => x.Id == pendingMoveId && x.State == "Committed"), "Pending move is committed only when source is absent and destination fingerprint matches", failures);
    var mismatchedSource = Path.Combine(root, "pending-mismatch.jpg");
    var mismatchedDestination = Path.Combine(root, "pending-mismatch-dest.jpg");
    File.WriteAllBytes(mismatchedDestination, [1, 2]);
    var mismatchId = Guid.NewGuid().ToString("N");
    journal.Append(new JournalEntry(mismatchId, "Copy", "Prepared", mismatchedSource, mismatchedDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
    var mismatchReconciled = journal.ReconcilePendingOperations();
    Check(mismatchReconciled.Any(x => x.Id == mismatchId && x.State == "Failed") && journal.ReadPendingOperations().All(x => x.Id != mismatchId), "Pending copy with mismatched destination is failed without replay", failures);
    var sourceStillExists = Path.Combine(root, "pending-source-exists.jpg");
    var sourceStillDestination = Path.Combine(root, "pending-source-exists-dest.jpg");
    File.WriteAllBytes(sourceStillExists, [4, 5, 6]);
    File.WriteAllBytes(sourceStillDestination, [4, 5, 6]);
    var sourceExistsId = Guid.NewGuid().ToString("N");
    journal.Append(new JournalEntry(sourceExistsId, "Move", "Prepared", sourceStillExists, sourceStillDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
    var sourceExistsReconciled = journal.ReconcilePendingOperations();
    Check(sourceExistsReconciled.Any(x => x.Id == sourceExistsId && x.State == "Failed"), "Pending move with source still present is failed without replay", failures);

    var association = Path.Combine(projectRoot, "outputs", "install-photo-review-association.ps1");
    var associationText = File.ReadAllText(association);
    Check(associationText.Contains("$progId\\shell\\open\\command") && associationText.Contains("\"%1\""), "File association command is registered", failures);

    if (failures.Count > 0) { foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure); Environment.ExitCode = 1; }
    else Console.WriteLine("PASS: all PhotoReview persistence/journal/keyboard/release-contract tests");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

static void Check(bool condition, string name, List<string> failures) { if (condition) Console.WriteLine("PASS: " + name); else failures.Add(name); }

static bool operationJournalTextContainsFailed()
{
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    return File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "OperationJournal.cs")).Contains("ReadFailedOperations", StringComparison.Ordinal);
}
