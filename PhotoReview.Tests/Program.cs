using PhotoReview.App;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
    var journalFile = Directory.GetFiles(Path.Combine(root, "app-data"), "operations.jsonl").Single();
    Check(new FileInfo(journalFile).Length > 0 && File.ReadAllLines(journalFile).All(line => line.StartsWith("{", StringComparison.Ordinal)), "Journal entries are durably written as JSONL", failures);
    File.AppendAllText(journalFile, "{not-valid-json}" + Environment.NewLine);
    Check(journal.ReadCommittedMoves().Any(x => x.Id == operationId) && journal.ReadPendingOperations().Count == 0, "Journal readers tolerate an invalid JSONL line", failures);
    Parallel.For(0, 8, i => journal.Append(new JournalEntry($"parallel-{i}", "Move", "Committed", source, destination, 4, info.LastWriteTimeUtc, DateTime.UtcNow)));
    Check(journal.ReadCommittedMoves().Count(x => x.Id.StartsWith("parallel-", StringComparison.Ordinal)) == 8, "Journal concurrent append/read remains line-consistent", failures);
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
    Check(appSettings.Contains("Skip") && mainWindow.Contains("Shortcuts.Skip"), "Space skip shortcut exists", failures);
    Check(appSettings.Contains("Skip") && appSettings.Contains("Undo") && appSettings.Contains("Fullscreen") && mainWindow.Contains("Shortcuts.Skip") && mainWindow.Contains("Shortcuts.Undo") && mainWindow.Contains("Shortcuts.Fullscreen"), "Skip, undo, and fullscreen shortcuts are configurable", failures);
    Check(mainWindow.Contains("Key.PageUp") && mainWindow.Contains("Key.PageDown") && mainWindow.Contains("NavigateSiblingFolderAsync"), "PageUp/PageDown navigate sibling folders", failures);
    var siblingRoot = Path.Combine(root, "folders"); Directory.CreateDirectory(siblingRoot);
    var folder1 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder1")).FullName;
    var folder2 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder2")).FullName;
    var folder10 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder10")).FullName;
    var siblings = SiblingFolderService.GetSorted(folder10);
    Check(siblings.SequenceEqual(new[] { folder1, folder2, folder10 }, StringComparer.OrdinalIgnoreCase) && SiblingFolderService.GetTarget(folder2, 1) == folder10 && SiblingFolderService.GetTarget(folder2, -1) == folder1, "Sibling folder navigation uses natural order", failures);
    Check(mainWindow.Contains("Key.Home") && mainWindow.Contains("ShowImageAsync(0)"), "Home navigates to first image", failures);
    Check(mainWindow.Contains("_settings.Shortcuts.NextFolder") && mainWindow.Contains("_settings.Shortcuts.FirstImage") && mainWindow.Contains("_settings.Shortcuts.ZoomIn"), "Configurable navigation and zoom shortcuts are wired at runtime", failures);
    Check(imageSortService.Contains("GetQuery(\"/app1/ifd/{ushort=274}\")") && imageSortService.Contains("value is 5 or 6 or 7 or 8"), "Portrait-first sort accounts for EXIF orientation", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ImageSortService.cs")) && mainWindow.Contains("ImageSortService.Sort"), "Image sorting is isolated in a testable service", failures);
    var compareOriginal = Path.Combine(root, "CocCocSetup.jpg");
    var compareNumbered = Path.Combine(root, "CocCocSetup (1).jpg");
    var comparePair = ComparePairService.Find(new[] { compareOriginal, compareNumbered }, compareNumbered);
    Check(comparePair is not null && comparePair.Value.Left == compareOriginal && comparePair.Value.Right == compareNumbered, "Compare pair detection works from numbered filename", failures);
    var compareFromOriginal = ComparePairService.Find(new[] { compareOriginal, compareNumbered }, compareOriginal);
    Check(compareFromOriginal is not null && compareFromOriginal.Value.Left == compareOriginal && compareFromOriginal.Value.Right == compareNumbered, "Compare pair detection works from original filename", failures);
    Check(ComparePairService.Find(new[] { compareOriginal }, compareOriginal) is null, "Compare pair detection rejects an incomplete pair", failures);
    var sortFixture = new[] { Path.Combine(root, "img10.jpg"), Path.Combine(root, "img2.jpg"), Path.Combine(root, "img1.jpg") };
    var nameSorted = ImageSortService.Sort(sortFixture, "Name");
    Check(Path.GetFileName(nameSorted[0]) == "img1.jpg" && Path.GetFileName(nameSorted[1]) == "img2.jpg" && Path.GetFileName(nameSorted[2]) == "img10.jpg", "Natural filename sort orders numeric suffixes", failures);
    var portraitFallbackSorted = ImageSortService.Sort(sortFixture, "PortraitFirst");
    Check(portraitFallbackSorted.SequenceEqual(nameSorted), "Portrait-first keeps natural name order when metadata is unavailable", failures);
    var landscapePath = Path.Combine(root, "landscape.jpg");
    var exifPortraitPath = Path.Combine(root, "exif-portrait.jpg");
    WriteJpegFixture(landscapePath, 40, 20);
    WriteJpegFixture(exifPortraitPath, 40, 20, 6);
    var exifSorted = ImageSortService.Sort(new[] { landscapePath, exifPortraitPath }, "PortraitFirst");
    Check(string.Equals(exifSorted[0], exifPortraitPath, StringComparison.OrdinalIgnoreCase), "Portrait-first honors EXIF orientation 6 fixture", failures);
    foreach (var orientation in new ushort[] { 5, 7, 8 })
    {
        var rotatedPath = Path.Combine(root, $"exif-{orientation}.jpg");
        WriteJpegFixture(rotatedPath, 40, 20, orientation);
        var rotatedSorted = ImageSortService.Sort(new[] { landscapePath, rotatedPath }, "PortraitFirst");
        Check(string.Equals(rotatedSorted[0], rotatedPath, StringComparison.OrdinalIgnoreCase), $"Portrait-first honors EXIF orientation {orientation} fixture", failures);
    }
    Check(mainWindow.Contains("action.Confirm") && mainWindow.Contains("BatchReviewWindow") && mainWindow.Contains("ShowDialog()"), "Actions and batch operations require confirmation", failures);
    Check(appSettings.Contains("ReviewAction") && appSettings.Contains("Actions"), "Config supports multiple review actions", failures);
    Check(appSettings.Contains("CurrentConfigVersion") && appSettings.Contains("Migrate") && appSettings.Contains("Flush(flushToDisk: true)"), "Config has versioned migration and durable atomic save", failures);
    Check(mainWindow.Contains("16L * 1024 * 1024 * 1024") && mainWindow.Contains("FullFolderRamThresholdBytes"), "RAM cache policy targets 16 GB and full-folder preload threshold", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "FileHashService.cs")) && mainWindow.Contains("_hashService.Clear()"), "Hash service is isolated with bounded cache lifecycle", failures);
    var mainWindowXaml = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml"));
    Check(!mainWindow.Contains("Image_LeftClick") && !mainWindow.Contains("Image_RightClick") && !mainWindowXaml.Contains("Image_LeftClick") && !mainWindowXaml.Contains("Image_RightClick"), "Image click does not navigate; compare owns click selection", failures);
    Check(mainWindow.Contains("_compareSelectedPath ?? _files[_index]") && mainWindow.Contains("_files.Remove(source)"), "Compare selection drives file actions", failures);
    Check(mainWindow.Contains("_compareSelectedPath = null;") && mainWindow.Contains("var token = Interlocked.Increment(ref _generation);"), "Compare selection resets on navigation", failures);
    Check(mainWindowXaml.Contains("AutomationProperties.Name=\"Mở thư mục ảnh\"") && mainWindowXaml.Contains("AutomationProperties.Name=\"Mở cài đặt\""), "Primary controls expose accessible names", failures);
    Check(mainWindowXaml.Contains("Preview ảnh bên trái, nhấn để chọn") && mainWindowXaml.Contains("Preview ảnh bên phải, nhấn để chọn"), "Compare previews expose accessible selection names", failures);
    Check(mainWindowXaml.Contains("Focusable=\"True\"") && mainWindow.Contains("CompareLeft_KeyDown") && mainWindow.Contains("CompareRight_KeyDown"), "Compare previews support keyboard selection", failures);
    Check(mainWindow.Contains("GetGCMemoryInfo") && mainWindow.Contains("PreloadMemoryLoadLimit"), "Background preload has memory pressure guard", failures);
    Check(mainWindowXaml.Contains("Closed=\"Window_Closed\"") && mainWindow.Contains("_thumbnailCache.Dispose()") && mainWindow.Contains("_preloadCts.Dispose()"), "Window shutdown disposes preload and thumbnail resources", failures);
    var thumbnailCacheText = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "ThumbnailCache.cs"));
    Check(thumbnailCacheText.Contains("DefaultMaxDiskBytes") && thumbnailCacheText.Contains("PruneDiskCache") && thumbnailCacheText.Contains("ClearDisk"), "Disk thumbnail cache has quota and clear operation", failures);
    Check(thumbnailCacheText.Contains("catch (UnauthorizedAccessException) { }") && thumbnailCacheText.Contains("catch (IOException) { }"), "Disk cache cleanup tolerates filesystem access failures", failures);
    Check(mainWindowXaml.Contains("ClearCache_Click") && mainWindow.Contains("_thumbnailCache.ClearDisk()"), "Disk cache can be cleared from UI without changing source images", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ReviewMetrics.cs")) && mainWindow.Contains("RecordCacheHit") && mainWindow.Contains("RecordSourceRead") && mainWindow.Contains("RecordPresented") && File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "DiagnosticsWindow.xaml")).Contains("Source file reads"), "Review metrics record source reads, cache, decode, and present latency", failures);
    var metrics = new ReviewMetrics();
    metrics.RecordCacheHit(); metrics.RecordCacheMiss(); metrics.RecordSourceRead(128, 7); metrics.RecordPresented(11);
    var snapshot = metrics.Snapshot();
    Check(snapshot.CacheHits == 1 && snapshot.CacheMisses == 1 && snapshot.SourceReads == 1 && snapshot.SourceBytesRead == 128 && snapshot.DecodeMilliseconds == 7 && snapshot.PresentedImages == 1 && snapshot.PresentMilliseconds == 11, "Review metrics snapshot preserves counters", failures);
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

static void WriteJpegFixture(string path, int width, int height, ushort? orientation = null)
{
    var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    var metadata = new BitmapMetadata("jpg");
    if (orientation.HasValue) metadata.SetQuery("/app1/ifd/{ushort=274}", orientation.Value);
    var encoder = new JpegBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static bool operationJournalTextContainsFailed()
{
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    return File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "OperationJournal.cs")).Contains("ReadFailedOperations", StringComparison.Ordinal);
}
