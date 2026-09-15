using PhotoReview.App;

/// <summary>Behavior contracts for cache/preload/Explorer regressions from ERROR-HISTORY.</summary>
internal static class CacheExplorerRegressionTests
{
    public static void Run(string root, List<string> failures)
    {
        var folder = Path.Combine(root, "explorer-regression");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "photo 2.jpg");
        var b = Path.Combine(folder, "photo 10.jpg");
        var c = Path.Combine(folder, "photo 11.jpg");
        File.WriteAllBytes(a, [1, 2]);
        File.WriteAllBytes(b, [3, 4, 5]);
        File.WriteAllBytes(c, [6, 7, 8, 9]);

        var cache = new BoundedLruCache<ImageCacheKey, string>(64, value => value.Length);
        var preview = ImageCacheKey.Create(a, false, 2048);
        var original = ImageCacheKey.Create(a, true, 0);
        cache.Set(preview, "preview");
        Check(cache.TryGet(preview, out var value) && value == "preview", "RAM cache hit returns the same decoded variant", failures);
        Check(!cache.TryGet(original, out _), "Original quality does not reuse preview cache entry", failures);
        File.WriteAllBytes(a, [9, 9, 9, 9, 9]);
        var replaced = ImageCacheKey.Create(a, false, 2048);
        Check(replaced != preview && !cache.TryGet(replaced, out _), "Replaced source cannot reuse stale decoded cache", failures);

        var partial = PreloadOrderService.Build(1, 5, false).ToArray();
        Check(partial.SequenceEqual(new[] { 2, 3, 4, 0 }) && partial.Distinct().Count() == partial.Length,
            "Preload order prioritizes next images then previous images without duplicates", failures);
        var full = PreloadOrderService.Build(2, 100, true).ToArray();
        Check(full.Length == 99 && full.Distinct().Count() == 99 && !full.Contains(2),
            "Full-folder preload schedules every catalog item exactly once", failures);

        var snapshot = new ExplorerViewSnapshot(folder, [c, a, b], [], ExplorerGroupState.None,
            ExplorerOrderStatus.Available, null, DateTime.UtcNow);
        Check(ExplorerSnapshotValidator.TryValidate(snapshot, [a, b, c], out var ordered, out _)
            && ordered.SequenceEqual(new[] { c, a, b }), "Explorer validator preserves complete native order", failures);
        Check(!ExplorerSnapshotValidator.TryValidate(snapshot with { OrderedPaths = [c, c, a] }, [a, b, c], out _, out var duplicateReason)
            && duplicateReason?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true,
            "Explorer validator rejects duplicate native items", failures);
        Check(!ExplorerSnapshotValidator.TryValidate(snapshot with { OrderedPaths = [c, a, Path.Combine(root, "outside.jpg")] }, [a, b, c], out _, out var outsideReason)
            && outsideReason?.Contains("outside", StringComparison.OrdinalIgnoreCase) == true,
            "Explorer validator rejects an item outside the folder", failures);
        Check(!ExplorerSnapshotValidator.TryValidate(snapshot with { OrderedPaths = [c, a] }, [a, b, c], out _, out var missingReason)
            && missingReason?.Contains("complete", StringComparison.OrdinalIgnoreCase) == true,
            "Explorer validator rejects incomplete snapshots", failures);
        Check(!ExplorerSnapshotValidator.TryValidate(snapshot with { OrderedPaths = ["not-a-path"] }, [a, b, c], out _, out _),
            "Explorer validator falls back on malformed native paths", failures);

        // The UI must keep the selected path after native reindex; this contract protects E-001/E-002/E-013 wiring.
        var mainWindowPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PhotoReview.App", "MainWindow.xaml.cs");
        if (File.Exists(mainWindowPath))
        {
            var source = File.ReadAllText(mainWindowPath);
            Check(source.Contains("currentPath", StringComparison.Ordinal) && source.Contains("Explorer native order applied", StringComparison.Ordinal),
                "Explorer reindex uses current path and records the applied order", failures);
            Check(source.Contains("_catalogInteractionGeneration", StringComparison.Ordinal)
                && source.Contains("Explorer native order ignored after catalog interaction", StringComparison.Ordinal),
                "Late Explorer snapshots cannot reorder an interacted catalog", failures);
        }
    }

    private static void Check(bool condition, string name, List<string> failures)
    {
        if (!condition) failures.Add(name);
    }
}
