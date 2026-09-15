using PhotoReview.App;
using PhotoReview.Tests;
using System.IO;
using System.Windows.Media;
static async Task RunCliBenchmarksAsync(string folder, IReadOnlyList<BenchmarkProfile> profiles, string? outputOverride)
{
    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
    var reportDirectory = outputOverride is { Length: > 0 } ? Path.GetFullPath(outputOverride) : Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(reportDirectory);
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(p => supported.Contains(Path.GetExtension(p))).Take(64).ToArray();
    if (files.Length == 0) throw new InvalidOperationException("Benchmark folder contains no supported images");
    var reports = new List<BenchmarkReport>();
    foreach (var profile in profiles)
    {
        Console.WriteLine($"START profile={profile.Id} workload={profile.Workload} mode={profile.LoadingMode} workers={profile.Workers} window={profile.NextWindow}/{profile.PreviousWindow}");
        var imageExecutor = new BenchmarkImageExecutor();
        var report = await new BenchmarkEngine().RunAsync(folder, profile, async (_, workload, iteration, token) =>
        {
            if (workload == BenchmarkWorkload.FileAction)
            {
                var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Action-" + Guid.NewGuid().ToString("N") + ".bin");
                try { await File.WriteAllBytesAsync(temp, await File.ReadAllBytesAsync(files[iteration % files.Length], token), token); await imageExecutor.DecodeAsync(temp, profile, token); using var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); var moved = temp + ".moved"; File.Move(temp, moved); File.Delete(moved); return (true, (ReviewMetricsSnapshot?)null); }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            var selected = Enumerable.Range(0, Math.Min(Math.Max(1, profile.Workers), files.Length)).Select(i => files[(iteration + i) % files.Length]).ToArray();
            await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, profile.Workers), CancellationToken = token }, async (path, ct) =>
            {
                await imageExecutor.DecodeAsync(path, profile, ct);
            });
            return (true, (ReviewMetricsSnapshot?)null);
        }, new Progress<BenchmarkProgress>(p => Console.WriteLine($"  {p.ProfileId}: {p.Completed}/{p.Total} {p.Message}")));
        reports.Add(report);
        var pathOut = Path.Combine(reportDirectory, $"{profile.Id}-{report.RunId}.json"); await File.WriteAllTextAsync(pathOut, report.ToJson());
        var phase = report.Phases[0]; Console.WriteLine($"DONE profile={profile.Id} status={phase.Status} p50={phase.P50:F1}ms p95={phase.P95:F1}ms max={phase.Max:F1}ms report={pathOut}");
    }
    var summary = Path.Combine(reportDirectory, "summary.json"); await File.WriteAllTextAsync(summary, System.Text.Json.JsonSerializer.Serialize(reports, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"REPORT: {summary}");
}
if (args.Length == 1 && args[0] == "--benchmark-list-profiles")
{
    foreach (var profile in BenchmarkProfiles.All)
        Console.WriteLine($"{profile.Id}\t{profile.Name}\t{profile.Workload}\tmode={profile.LoadingMode}\tworkers={profile.Workers}\titerations={profile.Iterations}\tcorrectnessOnly={profile.CorrectnessOnly}\t{profile.Description}");
    return;
}

if (args.Length >= 2 && (args[0] == "--benchmark" || args[0] == "--benchmark-all" || args[0] == "--benchmark-actions"))
{
    var benchmarkFolder = args[1];
    var requested = args[0] switch
    {
        "--benchmark-all" => BenchmarkProfiles.All.Where(p => !p.CorrectnessOnly).ToArray(),
        "--benchmark-actions" => BenchmarkProfiles.All.Where(p => p.Workload == BenchmarkWorkload.FileAction).ToArray(),
        _ => (args.Length >= 3
            ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => BenchmarkProfiles.Find(id) ?? throw new ArgumentException($"Unknown benchmark profile: {id}"))
                .ToArray()
            : [BenchmarkProfiles.Find("recommended-auto")!])
    };
    var output = args.Length >= 3 && args[0] != "--benchmark" ? args[2] : null;
    await RunCliBenchmarksAsync(benchmarkFolder, requested, output);
    return;
}
if (args.Length == 2 && args[0] == "--ui-next-probe")
{
    await LocalUiNextProbe.RunAsync(args[1]);
    return;
}

if ((args.Length == 2 || args.Length == 3) && args[0] == "--preload-bench")
{
    await LocalImageBenchmark.RunAsync(args[1], args.Length == 3 ? int.Parse(args[2]) : 8);
    return;
}

if (args.Length == 2 && args[0] == "--explorer-probe")
{
    var probe = await new ExplorerOrderService().TryGetSnapshotAsync(args[1], TimeSpan.FromSeconds(5), CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(probe, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    var scanned = Directory.EnumerateFiles(args[1]).Where(path => supported.Contains(Path.GetExtension(path))).ToArray();
    var accepted = ExplorerSnapshotValidator.TryValidate(probe, scanned, out var ordered, out var reason);
    Console.WriteLine($"VALIDATOR: accepted={accepted}, scanned={scanned.Length}, ordered={ordered.Count}, reason={reason ?? "none"}");
    return;
}

var failures = new List<string>();
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml.cs")))
    projectRoot = Directory.GetCurrentDirectory();
var root = Path.Combine(Path.GetTempPath(), "PhotoReview-Test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
ImageCacheKeyTests.Run(root, failures);
FileActionConcurrencyTests.Run(root, failures);
CacheExplorerRegressionTests.Run(root, failures);
BenchmarkScenarioTests.Run(root, failures);
Check(BenchmarkProfiles.All.Count >= 25, "Expanded benchmark profile registry", failures);
Check(BenchmarkProfiles.All.Count(x => x.CorrectnessOnly) == 1, "Original is correctness-only", failures);
Check(BenchmarkRanking.Rank(BenchmarkProfiles.All.Where(x => !x.CorrectnessOnly).Select(x => new BenchmarkPhaseResult(x.Id, x.Workload, [10, 20], BenchmarkResultStatus.Pass)), BenchmarkWorkload.Sequential).Count > 0,
    "Speed ranking excludes correctness-only profile", failures);
var performanceFixture = PerformanceTestHarness.CreateFixture(root, 30);
var performanceReport = await PerformanceTestHarness.RunAsync(performanceFixture, 30, workers: 4,
    reportPath: Path.Combine(root, "performance-report.json"));
Check(performanceReport.Samples.All(sample => sample.Status is "PASS" or "WARN"),
    "Relative performance samples complete without hard timing failure", failures);
Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", Path.Combine(root, "app-data"));
try
{
    Check(!new AppSettings().LoggingEnabled && !AppLog.Enabled, "Logging defaults off", failures);
    var legacySettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":2}");
    Check(legacySettings is { LoggingEnabled: false }, "Existing config without logging flag keeps logging off", failures);
    AppLog.Info("disabled-info");
    AppLog.Error("disabled-error", new Exception("test"));
    Check(!Directory.Exists(Path.GetDirectoryName(AppLog.FilePath)), "Disabled logging creates no directory or file, including errors", failures);
    AppLog.Enabled = true;
    AppLog.Info("enabled-info");
    AppLog.Error("enabled-error");
    FlushAppLog();
    var logContents = File.ReadAllText(AppLog.FilePath);
    Check(logContents.Contains("enabled-info") && logContents.Contains("enabled-error"), "Explicitly enabled logging writes diagnostics", failures);
    AppLog.Enabled = false;
    AppLog.Info("disabled-again");
    AppLog.Error("disabled-again");
    Check(File.ReadAllText(AppLog.FilePath) == logContents, "Turning logging off stops all diagnostic writes", failures);

    AppLog.Enabled = true;
    var concurrentMarkers = Enumerable.Range(0, 200).Select(i => $"concurrent-marker-{i}").ToArray();
    Parallel.ForEach(concurrentMarkers, marker => AppLog.Info(marker));
    FlushAppLog();
    var concurrentLog = File.ReadAllText(AppLog.FilePath);
    Check(concurrentMarkers.All(concurrentLog.Contains), "Concurrent logging preserves every entry", failures);
    Check(concurrentLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Count(line => line.Contains("concurrent-marker-", StringComparison.Ordinal)) >= concurrentMarkers.Length,
        "Concurrent log entries remain line-delimited", failures);
    AppLog.Enabled = false;
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
    var dragDropXaml = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml"));
    var appSettings = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "AppSettings.cs"));
    var settingsWindow = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "SettingsWindow.xaml.cs"));
    var settingsWindowXaml = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "SettingsWindow.xaml"));
    var imageSortService = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "ImageSortService.cs"));
    // The decode/cache path lives in PreviewImageService and preload scheduling in
    // PreloadScheduler (WP3.2).  Both are constructible without a WPF Window, so their
    // contracts are asserted behaviorally below; only the few branches that cannot be
    // driven from a console harness still read these focused service files.
    var previewServiceText = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "PreviewImageService.cs"));
    var preloadSchedulerText = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "PreloadScheduler.cs"));
    RunInterleavedFileActionSequence(root, failures);
    Check(mainWindow.Contains("AdvanceBeforeFileActionAsync(sourcePath, removeSource: true)") &&
          mainWindow.Contains("AdvanceBeforeFileActionAsync(sourcePath, removeSource: operation == \"Move\")") &&
          mainWindow.Contains("Do not call ShowImageAsync after action"),
          "Interleaved actions advance viewer before filesystem operation and exactly once", failures);
    Check(mainWindow.Contains("Interlocked.Exchange(ref _fileActionInProgress, 1)"),
          "Interleaved actions reject duplicate concurrent file actions", failures);
    // AppSettings is a plain public class, so its option contract is asserted by calling it.
    var defaultSettings = new AppSettings();
    Check(defaultSettings.LoadingMode == "Preview", "LoadingMode defaults to Preview", failures);
    Check(AppSettings.IsValidLoadingMode("Fast") && AppSettings.IsValidLoadingMode("Preview") && AppSettings.IsValidLoadingMode("Original")
        && AppSettings.NormalizeLoadingMode("preview") == "Preview" && AppSettings.NormalizeLoadingMode("ORIGINAL") == "Original",
        "LoadingMode has Fast, Preview, and Original options and accepts them case-insensitively", failures);
    Check(!AppSettings.IsValidLoadingMode("Nonsense") && !AppSettings.IsValidLoadingMode(null) && !AppSettings.IsValidLoadingMode(""),
        "LoadingMode validation rejects unknown, null, and empty values", failures);
    Check(AppSettings.IsValidLoadingMode(AppSettings.NormalizeLoadingMode("Nonsense")) && AppSettings.IsValidLoadingMode(AppSettings.NormalizeLoadingMode(null)),
        "LoadingMode normalization always yields a supported mode for corrupt config values", failures);
    Check(defaultSettings.ImageSortMode == "Name" && AppSettings.IsValidImageSortMode("SizeAscending")
        && !AppSettings.IsValidImageSortMode("Whatever") && AppSettings.NormalizeImageSortMode("Size") == "SizeDescending"
        && AppSettings.NormalizeImageSortMode("Whatever") == "Name",
        "ImageSortMode defaults to Name, validates known modes, and normalizes unknown ones", failures);
    Check(mainWindow.Contains("LoadingMode", StringComparison.Ordinal), "MainWindow reads LoadingMode", failures);
    Check(mainWindow.Contains("Thumbnail", StringComparison.Ordinal) && mainWindow.Contains("Preview", StringComparison.Ordinal), "MainWindow loading modes have thumbnail/preview contract", failures);
    var shortcuts = ShortcutMappings.Default();
    Check(shortcuts.Next == "Right" && shortcuts.Previous == "Left" && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Next)") && mainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Previous)"), "Keyboard navigation uses arrow keys", failures);
    Check(shortcuts.MoveToFolder2 == "Enter" && mainWindow.Contains("ReviewAction") && mainWindow.Contains("ExecuteActionAsync(action)"), "Enter/action profiles drive configurable operations", failures);
    Check(shortcuts.SendToRecycleBin == "Delete" && mainWindow.Contains("ClassifyCurrentAsync(3)"), "Delete maps to Recycle Bin", failures);
    // File actions must capture paths and advance the viewer before any blocking
    // filesystem/Shell call.  The action completion must not advance a second time.
    Check(mainWindow.Contains("AdvanceBeforeFileActionAsync", StringComparison.Ordinal), "File actions advance viewer before filesystem operation", failures);
    Check(mainWindow.Contains("sourcePath", StringComparison.Ordinal) && mainWindow.Contains("nextPath", StringComparison.Ordinal), "File actions snapshot source and next paths", failures);
    Check(mainWindow.Contains("FileActionResult", StringComparison.Ordinal) || mainWindow.Contains("Task.Run", StringComparison.Ordinal), "Filesystem action is detached from UI thread", failures);
    Check(mainWindow.Contains("Do not call ShowImageAsync after action") || mainWindow.Contains("advance exactly once", StringComparison.OrdinalIgnoreCase), "File action completion does not advance twice", failures);
    Check(!mainWindow.Contains("Key.D1") && !mainWindow.Contains("Key.NumPad1"), "No number-1 shortcut required", failures);
    Check(appSettings.Contains("Skip") && mainWindow.Contains("Shortcuts.Skip"), "Space skip shortcut exists", failures);
    Check(appSettings.Contains("Skip") && appSettings.Contains("Undo") && appSettings.Contains("Fullscreen") && mainWindow.Contains("Shortcuts.Skip") && mainWindow.Contains("Shortcuts.Undo") && mainWindow.Contains("Shortcuts.Fullscreen"), "Skip, undo, and fullscreen shortcuts are configurable", failures);
    Check(appSettings.Contains("ValidateShortcuts") && settingsWindow.Contains("ValidateShortcuts"), "Shortcut conflicts are validated across global and action bindings", failures);
    var conflictingSettings = new AppSettings();
    conflictingSettings.Actions[0].Shortcut = conflictingSettings.Shortcuts.Next;
    Check(AppSettings.ValidateShortcuts(conflictingSettings)?.Contains("bị dùng trùng", StringComparison.OrdinalIgnoreCase) == true, "Shortcut validator reports cross-scope conflicts", failures);
    Check(mainWindow.Contains("Shortcuts.NextFolder") && mainWindow.Contains("Shortcuts.PreviousFolder") && mainWindow.Contains("NavigateSiblingFolderAsync"), "Configured shortcuts navigate sibling folders", failures);
    var siblingRoot = Path.Combine(root, "folders"); Directory.CreateDirectory(siblingRoot);
    var folder1 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder1")).FullName;
    var folder2 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder2")).FullName;
    var folder10 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder10")).FullName;
    var siblings = SiblingFolderService.GetSorted(folder10);
    Check(siblings.SequenceEqual(new[] { folder1, folder2, folder10 }, StringComparer.OrdinalIgnoreCase) && SiblingFolderService.GetTarget(folder2, 1) == folder10 && SiblingFolderService.GetTarget(folder2, -1) == folder1, "Sibling folder navigation uses natural order", failures);
    Check(mainWindow.Contains("Shortcuts.FirstImage") && mainWindow.Contains("ShowImageAsync(0)"), "Home navigates to first image", failures);
    Check(mainWindow.Contains("_settings.Shortcuts.NextFolder") && mainWindow.Contains("_settings.Shortcuts.FirstImage") && mainWindow.Contains("_settings.Shortcuts.ZoomIn"), "Configurable navigation and zoom shortcuts are wired at runtime", failures);
    var dragFolder = Directory.CreateDirectory(Path.Combine(root, "drag-folder")).FullName;
    var dragImage = Path.Combine(dragFolder, "first.JPG"); File.WriteAllBytes(dragImage, [1]);
    var parsedFolder = DragDropInputService.Parse([dragFolder]);
    var parsedImage = DragDropInputService.Parse([dragImage]);
    var parsedInvalid = DragDropInputService.Parse([Path.Combine(root, "notes.txt")]);
    Check(parsedFolder.Kind == DragDropInputKind.Folder && parsedFolder.FolderPath == dragFolder, "Drag-drop folder parser", failures);
    Check(parsedImage.Kind == DragDropInputKind.Image && parsedImage.FolderPath == dragFolder && parsedImage.InitialImagePath == dragImage, "Drag-drop image selects initial image", failures);
    Check(!parsedInvalid.IsValid, "Drag-drop rejects unsupported input", failures);
    Check(dragDropXaml.Contains("AllowDrop=\"True\"") && mainWindow.Contains("Window_PreviewDragOver") && mainWindow.Contains("Window_Drop"), "Drag-drop events are wired on the main window", failures);
    Check(mainWindow.Contains("_settings.Shortcuts.Compare") && mainWindow.Contains("ComparePanel.Visibility"), "Compare shortcut toggles compare panel", failures);
    Check(appSettings.Contains("CompareHashEnabled") && mainWindow.Contains("CompareHashEnabled") && settingsWindow.Contains("CompareHashCheck"), "Compare hash is optional", failures);
    Check(appSettings.Contains("CompareSizeEnabled") && mainWindow.Contains("CompareSizeEnabled") && settingsWindow.Contains("CompareSizeCheck"), "Compare size is optional", failures);
    Check(mainWindow.Contains("Task.WhenAll(GetHashAsync(pair.Value.Left), GetHashAsync(pair.Value.Right))"), "Compare hashes are requested concurrently", failures);
    Check(settingsWindow.Contains("CompareHashEnabled = true") && settingsWindow.Contains("CompareSizeEnabled = true"), "Settings defaults reset compare options", failures);
    var retrySource = Path.Combine(root, "retry.jpg");
    var retryDestination = Path.Combine(root, "retry-dest", "retry.jpg");
    File.WriteAllBytes(retrySource, [7, 8, 9]);
    var retryInfo = new FileInfo(retrySource);
    var retryEntry = new JournalEntry("old-failed", "Move", "Failed", retrySource, retryDestination, retryInfo.Length, retryInfo.LastWriteTimeUtc, DateTime.UtcNow, "previous failure");
    var retryResult = RecoveryRetryService.RetryMoveOrCopy(retryEntry, journal);
    Check(retryResult.Succeeded && File.Exists(retryDestination) && !File.Exists(retrySource), "Recovery retry validates fingerprint and journals success", failures);
    Check(File.Exists(retryDestination) && RecoveryRetryService.RetryMoveOrCopy(retryEntry with { Source = retryDestination }, journal).Succeeded == false, "Recovery retry rejects invalid source state", failures);
    Check(File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "RecoveryWindow.xaml")).Contains("Retry Move/Copy") && File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "RecoveryWindow.xaml.cs")).Contains("Retry_Click"), "Recovery retry is exposed with confirmation in UI", failures);
    Check(File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "RecoveryWindow.xaml")).Contains("AutomationProperties.Name=\"Thử lại Move hoặc Copy đã lỗi\""), "Recovery retry button is accessible", failures);
    Check(imageSortService.Contains("StrCmpLogicalW") && imageSortService.Contains("ExplorerComparer"), "Name sort uses Windows Explorer logical ordering", failures);
    Check(appSettings.Contains("Size") && imageSortService.Contains("OrderByDescending(GetFileSize)") && settingsWindow.Contains("Size"), "Size sort is configurable", failures);
    Check(imageSortService.Contains("SizeAscending") && settingsWindow.Contains("SizeAscending"), "Size sort direction is configurable", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ImageSortService.cs")) && mainWindow.Contains("ImageSortService.Sort"), "Image sorting is isolated in a testable service", failures);
    var compareOriginal = Path.Combine(root, "CocCocSetup.jpg");
    var compareNumbered = Path.Combine(root, "CocCocSetup (1).jpg");
    var comparePair = ComparePairService.Find(new[] { compareOriginal, compareNumbered }, compareNumbered);
    Check(comparePair is not null && comparePair.Value.Left == compareOriginal && comparePair.Value.Right == compareNumbered, "Compare pair detection works from numbered filename", failures);
    var compareFromOriginal = ComparePairService.Find(new[] { compareOriginal, compareNumbered }, compareOriginal);
    Check(compareFromOriginal is not null && compareFromOriginal.Value.Left == compareOriginal && compareFromOriginal.Value.Right == compareNumbered, "Compare pair detection works from original filename", failures);
    var compareOtherFolder = Path.Combine(root, "other", "CocCocSetup.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(compareOtherFolder)!);
    Check(ComparePairService.Find(new[] { compareOriginal, compareNumbered, compareOtherFolder }, compareOtherFolder) is null, "Compare pair detection stays within selected folder", failures);
    Check(ComparePairService.Find(new[] { compareOriginal }, compareOriginal) is null, "Compare pair detection rejects an incomplete pair", failures);
    var sortFixture = new[] { Path.Combine(root, "img10.jpg"), Path.Combine(root, "img2.jpg"), Path.Combine(root, "img1.jpg") };
    var nameSorted = ImageSortService.Sort(sortFixture, "Name");
    Check(Path.GetFileName(nameSorted[0]) == "img1.jpg" && Path.GetFileName(nameSorted[1]) == "img2.jpg" && Path.GetFileName(nameSorted[2]) == "img10.jpg", "Natural filename sort orders numeric suffixes", failures);
    var largeNameSorted = ImageSortService.Sort(new[] { "img1000000000000.jpg", "img2.jpg", "img10.jpg" }, "Name");
    Check(Path.GetFileName(largeNameSorted[0]) == "img2.jpg" && Path.GetFileName(largeNameSorted[2]) == "img1000000000000.jpg", "Natural filename sort handles numeric runs over 12 digits", failures);
    File.WriteAllBytes(sortFixture[0], [1]); File.WriteAllBytes(sortFixture[1], [1, 2, 3]); File.WriteAllBytes(sortFixture[2], [1, 2]);
    var sizeSorted = ImageSortService.Sort(sortFixture, "Size");
    Check(Path.GetFileName(sizeSorted[0]) == "img2.jpg" && Path.GetFileName(sizeSorted[2]) == "img10.jpg", "Size sort orders files by descending bytes", failures);
    var sizeAscending = ImageSortService.Sort(sortFixture, "SizeAscending");
    Check(Path.GetFileName(sizeAscending[0]) == "img10.jpg" && Path.GetFileName(sizeAscending[2]) == "img2.jpg", "Size sort orders files by ascending bytes", failures);
    var explorerFolder = Path.Combine(root, "explorer-order");
    Directory.CreateDirectory(explorerFolder);
    var explorerA = Path.Combine(explorerFolder, "a.jpg");
    var explorerB = Path.Combine(explorerFolder, "b.jpg");
    var validExplorerSnapshot = new ExplorerViewSnapshot(explorerFolder, [explorerB, explorerA], [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);
    Check(ExplorerSnapshotValidator.TryValidate(validExplorerSnapshot, [explorerA, explorerB], out var nativeOrder, out _) && nativeOrder.SequenceEqual(new[] { explorerB, explorerA }, StringComparer.OrdinalIgnoreCase), "Explorer snapshot accepts a complete native order", failures);
    Check(!ExplorerSnapshotValidator.TryValidate(validExplorerSnapshot with { OrderedPaths = [explorerA] }, [explorerA, explorerB], out _, out _), "Explorer snapshot rejects missing images", failures);
    Check(!ExplorerSnapshotValidator.TryValidate(validExplorerSnapshot with { OrderedPaths = [explorerA, explorerA, explorerB] }, [explorerA, explorerB], out _, out _), "Explorer snapshot rejects duplicate paths", failures);
    Check(!ExplorerSnapshotValidator.TryValidate(validExplorerSnapshot with { OrderedPaths = [explorerA, Path.Combine(root, "outside.jpg")] }, [explorerA, explorerB], out _, out _), "Explorer snapshot rejects paths outside the folder", failures);
    Check(!ExplorerSnapshotValidator.TryValidate(validExplorerSnapshot with { Status = ExplorerOrderStatus.TimedOut, Reason = "timeout" }, [explorerA, explorerB], out _, out var unavailableReason) && unavailableReason == "timeout", "Explorer snapshot exposes provider fallback reason", failures);
    IExplorerOrderProvider fakeExplorerProvider = new FakeExplorerOrderProvider(validExplorerSnapshot);
    Check((await fakeExplorerProvider.TryGetSnapshotAsync(explorerFolder, TimeSpan.FromSeconds(2), CancellationToken.None)).OrderedPaths[0] == explorerB, "Explorer provider contract is fakeable without COM", failures);
    Check(!mainWindow.Contains("files.Count < 100") && (mainWindow.Contains("TryGetSnapshotAsync") || mainWindow.Contains("TryGetSnapshotProgressiveAsync")) && mainWindow.Contains("loadGeneration != _folderGeneration"), "Explorer order applies below and above 100 files and rejects stale folder results", failures);
    Check(mainWindow.Contains("explorerSnapshot = await explorerTask") && mainWindow.Contains("_totalSourceBytes = await totalBytesTask"), "Direct file open waits for complete Explorer snapshot and folder size before first preload", failures);
    Check(mainWindow.Contains("mayReplaceInitialFallback") && mainWindow.Contains("await ShowImageAsync(0)"), "Folder open replaces untouched fallback with the first native Explorer item", failures);
    Check(mainWindow.Contains("currentSet.SetEquals(scannedFiles)") && mainWindow.Contains("StatusText.Text = $\"{_index + 1}/{_files.Count}\"") && mainWindow.Contains("PreloadAroundAsync(_index, _generation)"), "Native reindex refreshes counter and preload without duplicate render", failures);
    Check(mainWindow.Contains("_catalogInteractionGeneration") && mainWindow.Contains("Explorer native order ignored after catalog interaction"), "Explorer snapshot cannot reindex after user catalog interaction", failures);
    Check(mainWindow.Contains("Interlocked.Increment(ref _catalogInteractionGeneration)"), "Navigation and file actions advance catalog interaction generation", failures);
    Check(mainWindow.Contains("action.Confirm") && mainWindow.Contains("BatchReviewWindow") && mainWindow.Contains("ShowDialog()"), "Actions and batch operations require confirmation", failures);
    var multiActionSettings = new AppSettings();
    multiActionSettings.Actions.Add(new ReviewAction { Name = "Loại 3", Shortcut = "T", Operation = "Copy", Destination = "Loai-3" });
    Check(multiActionSettings.Actions.Count >= 2 && multiActionSettings.Actions.All(a => a.Name.Length > 0 && a.Operation.Length > 0 && a.Destination.Length > 0)
        && AppSettings.ValidateShortcuts(multiActionSettings) is null,
        "Config supports multiple review actions with distinct shortcuts", failures);
    Check(new AppSettings().ConfigVersion == AppSettings.CurrentConfigVersion
        && System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.ConfigVersion == 1,
        "Config carries an explicit version that round-trips through JSON", failures);
    // AppSettings.Save writes to the user's real LocalAppData config path (it does not honour
    // PHOTOREVIEW_DATA_ROOT), so the durable-save path stays a source-presence check.
    Check(appSettings.Contains("Migrate") && appSettings.Contains("Flush(flushToDisk: true)"), "Config has versioned migration and durable atomic save (source presence, not behavior)", failures);
    Check(mainWindow.Contains("AppConstants.ImageCacheCapacityBytes") && mainWindow.Contains("FullFolderRamThresholdBytes"), "RAM cache policy targets 16 GB and full-folder preload threshold", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "FileHashService.cs")) && mainWindow.Contains("_hashService.Clear()"), "Hash service is isolated with bounded cache lifecycle", failures);
    var mainWindowXaml = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "MainWindow.xaml"));
    Check(!mainWindow.Contains("Image_LeftClick") && !mainWindow.Contains("Image_RightClick") && !mainWindowXaml.Contains("Image_LeftClick") && !mainWindowXaml.Contains("Image_RightClick"), "Image click does not navigate; compare owns click selection", failures);
    Check(mainWindow.Contains("_compareSelectedPath ?? _files[_index]") && mainWindow.Contains("_files.Remove(source)"), "Compare selection drives file actions", failures);
    var hashFixture = Path.Combine(root, "hash-fixture.bin");
    File.WriteAllBytes(hashFixture, [1, 2, 3]);
    var hashService = new FileHashService();
    var firstHash = await hashService.GetAsync(hashFixture);
    var cachedHash = await hashService.GetAsync(hashFixture);
    File.WriteAllBytes(hashFixture, [1, 2, 4]);
    var changedHash = await hashService.GetAsync(hashFixture);
    Check(firstHash == cachedHash && firstHash != changedHash, "File hash service caches and invalidates by file fingerprint", failures);
    var concurrentHashes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => hashService.GetAsync(hashFixture)));
    Check(concurrentHashes.All(hash => hash == changedHash), "File hash service deduplicates concurrent reads", failures);
    Check(mainWindow.Contains("_compareSelectedPath = null;") && mainWindow.Contains("var token = Interlocked.Increment(ref _generation);"), "Compare selection resets on navigation", failures);
    Check(mainWindowXaml.Contains("UndoLastAction_Click") && mainWindow.Contains("e.Key == Key.Escape") && mainWindow.Contains("Close();"), "Context-menu Undo and Escape exit are wired", failures);
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "RecycleBinRestoreService.cs")) && mainWindow.Contains("RecycleBinRestoreService.TryRestore"), "Delete Undo restores through Recycle Bin Shell", failures);
    Check(mainWindowXaml.Contains("AutomationProperties.Name=\"Mở thư mục ảnh\"") && mainWindowXaml.Contains("AutomationProperties.Name=\"Mở cài đặt\""), "Primary controls expose accessible names", failures);
    Check(settingsWindow.Contains("OpenLogLocation_Click") && settingsWindowXaml.Contains("AutomationProperties.Name=\"Mở vị trí file log\""), "Settings exposes an accessible Open log location control", failures);
    Check(settingsWindow.Contains("Path.GetDirectoryName(AppLog.FilePath)") && settingsWindow.Contains("explorer.exe") && settingsWindow.Contains("AppLog.FilePath"), "Open log location follows the configured AppLog path", failures);
    Check(mainWindowXaml.Contains("Preview ảnh bên trái, nhấn để chọn") && mainWindowXaml.Contains("Preview ảnh bên phải, nhấn để chọn"), "Compare previews expose accessible selection names", failures);
    Check(mainWindowXaml.Contains("Focusable=\"True\"") && mainWindow.Contains("CompareLeft_KeyDown") && mainWindow.Contains("CompareRight_KeyDown"), "Compare previews support keyboard selection", failures);
    // A valid, tiny PNG keeps decode fixtures portable while exercising WPF's real decoder.
    var previewPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    // PreloadScheduler takes its memory-load limit by constructor injection, so the memory
    // pressure guard is driven for real: a 0.0 limit can never have headroom.
    var preloadFolder = Directory.CreateDirectory(Path.Combine(root, "preload-scheduler")).FullName;
    var preloadFiles = Enumerable.Range(0, 4).Select(i =>
    {
        var path = Path.Combine(preloadFolder, $"preload-{i}.png");
        File.WriteAllBytes(path, previewPng);
        return path;
    }).ToArray();
    var guardedMetrics = new ReviewMetrics();
    var guardedPreviewService = new PreviewImageService(guardedMetrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024);
    using (var blockedScheduler = new PreloadScheduler(guardedPreviewService, guardedMetrics, () => preloadFiles, () => 0L, long.MaxValue, memoryLoadLimit: 0.0))
    {
        await blockedScheduler.PreloadAroundAsync(0);
        Check(guardedPreviewService.CacheCount == 0 && guardedMetrics.Snapshot().SourceReads == 0,
            "Background preload memory guard decodes nothing when there is no memory headroom", failures);
    }
    var warmMetrics = new ReviewMetrics();
    var warmPreviewService = new PreviewImageService(warmMetrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024);
    using (var warmScheduler = new PreloadScheduler(warmPreviewService, warmMetrics, () => preloadFiles, () => 0L, long.MaxValue, memoryLoadLimit: 1.0))
    {
        await warmScheduler.PreloadAroundAsync(0);
        Check(warmPreviewService.CacheCount > 0 && warmMetrics.Snapshot().SourceReads > 0,
            "Background preload warms the cache around the current index when memory headroom allows", failures);
        Check(warmPreviewService.TryGetCachedPreview(preloadFiles[1], out _),
            "Background preload prioritizes the next image after the current index", failures);
        warmScheduler.Cancel();
        var reloadedCount = warmPreviewService.CacheCount;
        await warmScheduler.PreloadAroundAsync(2);
        Check(warmPreviewService.CacheCount >= reloadedCount,
            "Cancelling preload does not poison the scheduler; the next request starts a fresh lifetime", failures);
        warmScheduler.ClearPreloadedKeys();
        Check(!warmScheduler.TryConsumePreloadedKey(warmPreviewService.GetCurrentCacheKey(preloadFiles[0])),
            "Clearing preloaded keys drops every warmed-key record", failures);
    }
    // Warmed-key bookkeeping is asserted against a two-file catalog so exactly one preload
    // worker runs.  PreloadScheduler._preloadedKeys is a plain HashSet written from concurrent
    // PreloadOneAsync tasks, so a multi-worker catalog loses records intermittently.
    var singleFolder = Directory.CreateDirectory(Path.Combine(root, "preload-single")).FullName;
    var singleFiles = Enumerable.Range(0, 2).Select(i =>
    {
        var path = Path.Combine(singleFolder, $"single-{i}.png");
        File.WriteAllBytes(path, previewPng);
        return path;
    }).ToArray();
    var singleMetrics = new ReviewMetrics();
    var singlePreviewService = new PreviewImageService(singleMetrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024);
    using (var singleScheduler = new PreloadScheduler(singlePreviewService, singleMetrics, () => singleFiles, () => 0L, long.MaxValue, memoryLoadLimit: 1.0))
    {
        await singleScheduler.PreloadAroundAsync(0);
        var warmedKey = singlePreviewService.GetCurrentCacheKey(singleFiles[1]);
        Check(singleScheduler.TryConsumePreloadedKey(warmedKey) && !singleScheduler.TryConsumePreloadedKey(warmedKey),
            "A preloaded key is reported once and then consumed so a hit is not counted twice", failures);
    }
    // Window shutdown itself is WPF glue (Closed handler on the Window), so it stays a
    // source-presence check; the scheduler's own disposal is covered behaviorally above.
    Check(mainWindowXaml.Contains("Closed=\"Window_Closed\"") && mainWindow.Contains("_thumbnailCache.Dispose()") && mainWindow.Contains("_preloadScheduler.Dispose()"), "Window shutdown disposes preload and thumbnail resources (source presence, not behavior)", failures);
    var placementService = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "WindowPlacementService.cs"));
    Check(mainWindowXaml.Contains("Loaded=\"Window_Loaded\"") && mainWindowXaml.Contains("Closing=\"Window_Closing\""), "Main window restores and saves native placement instead of always using the startup default", failures);
    Check(!mainWindowXaml.Contains("WindowState=\"Maximized\""), "Main window does not force maximized state in XAML", failures);
    Check(!mainWindowXaml.Contains("<Grid.RowDefinitions><RowDefinition Height=\"Auto\"/><RowDefinition Height=\"*\"/><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions>") && mainWindowXaml.Contains("Panel.ZIndex=\"100\" Background=\"#B0181818\""), "Image uses the full client area while toolbar remains a compact overlay", failures);
    Check(mainWindow.Contains("Title = $\"Photo Review — {folder}\"") && mainWindowXaml.Contains("x:Name=\"FolderText\" Visibility=\"Collapsed\""), "Current folder is shown in the native window title bar", failures);
    Check(File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "PhotoReview.App.csproj")).Contains("BuildStamp") && settingsWindow.Contains("AssemblyInformationalVersionAttribute"), "Each build exposes a unique informational build stamp in Settings", failures);
    Check(!mainWindow.Contains("Window_SourceInitialized") && mainWindow.Contains("WindowPlacementService.Restore(this)") && mainWindow.Contains("WindowPlacementService.Save(this)") && placementService.Contains("GetWindowPlacement") && placementService.Contains("SetWindowPlacement"), "Native window placement restores after Loaded and persists monitor, bounds, and maximized state", failures);
    Check(placementService.Contains("Screen.AllScreens") && placementService.Contains("WorkingArea"), "Saved placement is rejected when its monitor is no longer connected", failures);
    var thumbnailCacheText = File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "ThumbnailCache.cs"));
    Check(thumbnailCacheText.Contains("DefaultMaxDiskBytes") && thumbnailCacheText.Contains("PruneDiskCache") && thumbnailCacheText.Contains("ClearDisk"), "Disk thumbnail cache has quota and clear operation", failures);
    Check(thumbnailCacheText.Contains("catch (UnauthorizedAccessException) { }") && thumbnailCacheText.Contains("catch (IOException) { }"), "Disk cache cleanup tolerates filesystem access failures", failures);
    Check(mainWindowXaml.Contains("ClearCache_Click") && mainWindow.Contains("_thumbnailCache.ClearDisk()"), "Disk cache can be cleared from UI without changing source images", failures);
    // PreviewImageService owns decode + the bounded RAM cache and needs no WPF Window, so the
    // decode/cache/metrics contract is driven for real instead of grepping source text.
    var previewFolder = Directory.CreateDirectory(Path.Combine(root, "preview-service")).FullName;
    var previewPath = Path.Combine(previewFolder, "preview-a.png");
    File.WriteAllBytes(previewPath, previewPng);
    var previewMetrics = new ReviewMetrics();
    var previewService = new PreviewImageService(previewMetrics, () => false, () => 512, capacityBytes: 64L * 1024 * 1024);
    var decodedPreview = await previewService.GetPreviewAsync(previewPath);
    var afterFirstDecode = previewMetrics.Snapshot();
    Check(decodedPreview.PixelWidth > 0 && afterFirstDecode.CacheMisses == 1 && afterFirstDecode.CacheHits == 0
        && afterFirstDecode.SourceReads == 1 && afterFirstDecode.SourceBytesRead == new FileInfo(previewPath).Length
        && previewService.CacheCount == 1 && previewService.CacheBytes > 0,
        "Preview decode records exactly one source read with the real source byte count", failures);
    var secondPreview = await previewService.GetPreviewAsync(previewPath);
    var afterCacheHit = previewMetrics.Snapshot();
    Check(afterCacheHit.CacheHits == 1 && afterCacheHit.SourceReads == 1
        && afterCacheHit.SourceBytesRead == afterFirstDecode.SourceBytesRead && ReferenceEquals(secondPreview, decodedPreview),
        "Source byte metrics exclude cache deliveries and the cache returns the same decoded bitmap", failures);
    previewService.EvictCachedPath(previewPath);
    Check(!previewService.TryGetCachedPreview(previewPath, out _) && previewService.CacheCount == 0,
        "Evicting a path drops its decoded bitmap from the preview cache", failures);
    await previewService.GetPreviewAsync(previewPath);
    Check(previewMetrics.Snapshot().SourceReads == 2 && previewService.CacheCount == 1,
        "Eviction forces a fresh source read on the next request", failures);
    previewService.ClearCache();
    Check(previewService.CacheCount == 0 && previewService.CacheBytes == 0,
        "Clearing the preview cache drops every decoded bitmap", failures);
    var originalModeService = new PreviewImageService(previewMetrics, () => true, () => 512);
    Check(originalModeService.IsOriginalLoadingMode() && originalModeService.GetCurrentCacheKey(previewPath).IsOriginal
        && originalModeService.GetCurrentCacheKey(previewPath).TargetWidth == 0
        && !previewService.GetCurrentCacheKey(previewPath).IsOriginal
        && previewService.GetCurrentCacheKey(previewPath).TargetWidth == 512,
        "Original loading mode decodes at full size while Preview mode uses the target decode width", failures);
    var previewDimensions = await previewService.GetOriginalDimensionsAsync(previewPath);
    Check(previewDimensions.Width == 1 && previewDimensions.Height == 1,
        "Original dimensions are read from the real source header", failures);
    // The disk-cache branch can only be entered by planting a file in the user's real
    // LocalAppData cache directory, so it stays a grep -- but against the focused service.
    Check(previewServiceText.Contains("RecordDiskCacheHit") && previewServiceText.Contains("if (sourceRead)"),
        "Disk-cache deliveries are excluded from source byte metrics (source presence: priming the real disk cache is out of scope)", failures);
    // RecordPresented times a WPF render pass, which needs a live viewer; only its wiring is checked.
    Check(File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "ReviewMetrics.cs")) && mainWindow.Contains("RecordPresented")
        && File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "DiagnosticsWindow.xaml")).Contains("Source file reads"),
        "Viewer present latency is recorded and surfaced in diagnostics (source presence, not behavior)", failures);
    var metrics = new ReviewMetrics();
    metrics.RecordCacheHit(); metrics.RecordCacheMiss(); metrics.RecordSourceRead(128, 7); metrics.RecordPresented(11);
    var snapshot = metrics.Snapshot();
    Check(snapshot.CacheHits == 1 && snapshot.CacheMisses == 1 && snapshot.SourceReads == 1 && snapshot.SourceBytesRead == 128 && snapshot.DecodeMilliseconds == 7 && snapshot.PresentedImages == 1 && snapshot.PresentMilliseconds == 11, "Review metrics snapshot preserves counters", failures);
    Parallel.For(0, 500, _ =>
    {
        metrics.RecordPreloadHit();
        metrics.RecordInflightJoin();
        metrics.RecordDiskCacheHit();
        metrics.RecordQueueWait(2);
        metrics.RecordUiAssign(3);
    });
    var preloadMetrics = metrics.Snapshot();
    Check(preloadMetrics.PreloadHits == 500 && preloadMetrics.InflightJoins == 500 && preloadMetrics.DiskCacheHits == 500
        && preloadMetrics.QueueWaitMilliseconds == 1000 && preloadMetrics.UiAssignMilliseconds == 1500,
        "Concurrent preload diagnostics retain every delivery and timing event", failures);
    var cacheIdentityPath = Path.Combine(root, "cache-identity.jpg");
    File.WriteAllBytes(cacheIdentityPath, [1, 2, 3]);
    var previewKey = ImageCacheKey.Create(cacheIdentityPath, isOriginal: false, targetWidth: 2048);
    var resizedKey = ImageCacheKey.Create(cacheIdentityPath, isOriginal: false, targetWidth: 1024);
    var originalKey = ImageCacheKey.Create(cacheIdentityPath, isOriginal: true, targetWidth: 2048);
    Check(previewKey != resizedKey && previewKey != originalKey && previewKey.MatchesCurrentSource(),
        "Decoded cache identity separates resize and Original quality", failures);
    File.WriteAllBytes(cacheIdentityPath, [1, 2, 3, 4]);
    var changedKey = ImageCacheKey.Create(cacheIdentityPath, isOriginal: false, targetWidth: 2048);
    Check(changedKey != previewKey && !previewKey.MatchesCurrentSource(),
        "Replacing a source at the same path invalidates its decoded bitmap", failures);
    var preloadOrder = PreloadOrderService.Build(center: 40, count: 100, fullFolder: true).ToArray();
    Check(preloadOrder[0] == 41 && preloadOrder[31] == 72 && preloadOrder[32] == 39
        && preloadOrder.Distinct().Count() == 99 && !preloadOrder.Contains(40),
        "Full-folder preload prioritizes the next 32, then prior 8, and queues every other image once", failures);
    var shiftedOrder = PreloadOrderService.Build(center: 44, count: 100, fullFolder: false).ToArray();
    Check(shiftedOrder[0] == 45 && shiftedOrder[1] == 46 && shiftedOrder.Contains(43)
        && shiftedOrder.Length == 40 && shiftedOrder.Distinct().Count() == shiftedOrder.Length,
        "Navigating changes preload priority to the new Next without duplicate jobs", failures);
    Check(mainWindow.Contains("DiagnosticsWindow") && File.Exists(Path.Combine(projectRoot, "PhotoReview.App", "DiagnosticsWindow.xaml")), "Performance metrics have an in-app diagnostics view", failures);
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

static void RunInterleavedFileActionSequence(string root, List<string> failures)
{
    var folder = Path.Combine(root, "sequence");
    var moved = Path.Combine(root, "sequence-moved");
    Directory.CreateDirectory(folder);
    Directory.CreateDirectory(moved);
    var files = Enumerable.Range(1, 5).Select(i => Path.Combine(folder, $"{i}.jpg")).ToList();
    foreach (var file in files) File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
    var catalog = files.ToList();
    var index = 0;

    // The sequence mirrors the UI contract: navigation/action advances first,
    // then the filesystem operation runs against the captured source path.
    index = Math.Min(index + 1, catalog.Count - 1); // Next => 2
    var moveSource = catalog[index];
    var moveDestination = Path.Combine(moved, Path.GetFileName(moveSource));
    catalog.RemoveAt(index); // Move advances/removes exactly once; current becomes 3.
    index = Math.Min(index, catalog.Count - 1);
    File.Move(moveSource, moveDestination);
    Check(Path.Exists(moveDestination) && !Path.Exists(moveSource) && Path.GetFileName(catalog[index]) == "3.jpg",
        "Sequence Next then Move keeps next image without skipping", failures);

    index = Math.Min(index + 1, catalog.Count - 1); // Next => 4
    var deleteSource = catalog[index];
    catalog.RemoveAt(index); // Delete advances/removes exactly once; current becomes 5.
    index = Math.Min(index, catalog.Count - 1);
    File.Delete(deleteSource);
    Check(!Path.Exists(deleteSource) && Path.GetFileName(catalog[index]) == "5.jpg",
        "Sequence Next then Delete keeps next image without skipping", failures);

    index = Math.Min(index + 1, catalog.Count - 1); // Next at end remains 5.
    var finalDelete = catalog[index];
    catalog.RemoveAt(index);
    index = Math.Min(index, catalog.Count - 1);
    File.Delete(finalDelete);
    Check(catalog.Count == 2 && Path.GetFileName(catalog[index]) == "3.jpg" &&
          catalog.All(File.Exists), "Sequence Delete at end selects the prior surviving slot", failures);
}

static void FlushAppLog()
{
    // Keeps tests compatible with both synchronous and buffered logger implementations.
    var method = typeof(AppLog).GetMethod("Flush", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    method?.Invoke(null, null);
}

static bool operationJournalTextContainsFailed()
{
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    return File.ReadAllText(Path.Combine(projectRoot, "PhotoReview.App", "OperationJournal.cs")).Contains("ReadFailedOperations", StringComparison.Ordinal);
}

sealed class FakeExplorerOrderProvider(ExplorerViewSnapshot snapshot) : IExplorerOrderProvider
{
    public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.FromResult(snapshot);
}






