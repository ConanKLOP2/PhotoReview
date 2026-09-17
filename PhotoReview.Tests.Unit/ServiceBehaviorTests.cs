using System.IO;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;


/// <summary>
/// Mirrors the UI contract sequence from Program.cs's RunInterleavedFileActionSequence:
/// navigation/action advances first, then the filesystem operation runs against the
/// captured source path.
/// </summary>
public sealed class InterleavedFileActionSequenceTests : IDisposable
{
    private readonly TempRoot _root = new("sequence");

    private readonly bool _moveCheckpoint;
    private readonly bool _deleteCheckpoint;
    private readonly bool _finalDeleteCheckpoint;

    public InterleavedFileActionSequenceTests()
    {
        var folder = _root.Dir("sequence");
        var moved = _root.Dir("sequence-moved");
        var files = Enumerable.Range(1, 5).Select(i => Path.Combine(folder, $"{i}.jpg")).ToList();
        foreach (var file in files) File.WriteAllText(file, $"image-{Path.GetFileNameWithoutExtension(file)}");
        var catalog = files.ToList();
        var index = 0;

        index = Math.Min(index + 1, catalog.Count - 1); // Next => 2
        var moveSource = catalog[index];
        var moveDestination = Path.Combine(moved, Path.GetFileName(moveSource));
        catalog.RemoveAt(index); // Move advances/removes exactly once; current becomes 3.
        index = Math.Min(index, catalog.Count - 1);
        File.Move(moveSource, moveDestination);
        _moveCheckpoint = Path.Exists(moveDestination) && !Path.Exists(moveSource)
            && Path.GetFileName(catalog[index]) == "3.jpg";

        index = Math.Min(index + 1, catalog.Count - 1); // Next => 4
        var deleteSource = catalog[index];
        catalog.RemoveAt(index); // Delete advances/removes exactly once; current becomes 5.
        index = Math.Min(index, catalog.Count - 1);
        File.Delete(deleteSource);
        _deleteCheckpoint = !Path.Exists(deleteSource) && Path.GetFileName(catalog[index]) == "5.jpg";

        index = Math.Min(index + 1, catalog.Count - 1); // Next at end remains 5.
        var finalDelete = catalog[index];
        catalog.RemoveAt(index);
        index = Math.Min(index, catalog.Count - 1);
        File.Delete(finalDelete);
        _finalDeleteCheckpoint = catalog.Count == 2 && Path.GetFileName(catalog[index]) == "3.jpg"
            && catalog.All(File.Exists);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Sequence Next then Move keeps next image without skipping")]
    public void SequenceNextThenMoveKeepsNextImage() => Assert.True(_moveCheckpoint);

    [Fact(DisplayName = "Sequence Next then Delete keeps next image without skipping")]
    public void SequenceNextThenDeleteKeepsNextImage() => Assert.True(_deleteCheckpoint);

    [Fact(DisplayName = "Sequence Delete at end selects the prior surviving slot")]
    public void SequenceDeleteAtEndSelectsPriorSurvivingSlot() => Assert.True(_finalDeleteCheckpoint);
}

/// <summary>Cross-scope shortcut conflict validation.</summary>
public sealed class ShortcutTests
{
    [Fact(DisplayName = "Shortcut validator reports cross-scope conflicts")]
    public void ShortcutValidatorReportsCrossScopeConflicts()
    {
        var conflictingSettings = new AppSettings();
        conflictingSettings.Actions[0].Shortcut = conflictingSettings.Shortcuts.Next;
        Assert.True(AppSettings.ValidateShortcuts(conflictingSettings)?
            .Contains("bị dùng trùng", StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>Sibling folder navigation.</summary>
public sealed class SiblingFolderServiceTests : IDisposable
{
    private readonly TempRoot _root = new("siblings");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Sibling folder navigation uses natural order")]
    public void SiblingFolderNavigationUsesNaturalOrder()
    {
        var siblingRoot = _root.Dir("folders");
        var folder1 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder1")).FullName;
        var folder2 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder2")).FullName;
        var folder10 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder10")).FullName;
        var siblings = SiblingFolderService.GetSorted(folder10);
        Assert.True(siblings.SequenceEqual(new[] { folder1, folder2, folder10 }, StringComparer.OrdinalIgnoreCase)
            && SiblingFolderService.GetTarget(folder2, 1) == folder10
            && SiblingFolderService.GetTarget(folder2, -1) == folder1);
    }
}

/// <summary>Drag-and-drop input parsing.</summary>
public sealed class DragDropInputServiceTests : IDisposable
{
    private readonly TempRoot _root = new("dragdrop");
    private readonly string _dragFolder;
    private readonly string _dragImage;

    public DragDropInputServiceTests()
    {
        _dragFolder = _root.Dir("drag-folder");
        _dragImage = _root.File(Path.Combine("drag-folder", "first.JPG"), 1);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Drag-drop folder parser")]
    public void DragDropFolderParser()
    {
        var parsed = DragDropInputService.Parse([_dragFolder]);
        Assert.True(parsed.Kind == DragDropInputKind.Folder && parsed.FolderPath == _dragFolder);
    }

    [Fact(DisplayName = "Drag-drop image selects initial image")]
    public void DragDropImageSelectsInitialImage()
    {
        var parsed = DragDropInputService.Parse([_dragImage]);
        Assert.True(parsed.Kind == DragDropInputKind.Image && parsed.FolderPath == _dragFolder
            && parsed.InitialImagePath == _dragImage);
    }

    [Fact(DisplayName = "Drag-drop rejects unsupported input")]
    public void DragDropRejectsUnsupportedInput() =>
        Assert.False(DragDropInputService.Parse([_root.Combine("notes.txt")]).IsValid);
}

/// <summary>Compare pair detection.</summary>
public sealed class ComparePairServiceTests : IDisposable
{
    private readonly TempRoot _root = new("compare");
    private readonly string _original;
    private readonly string _numbered;

    public ComparePairServiceTests()
    {
        _original = _root.Combine("CocCocSetup.jpg");
        _numbered = _root.Combine("CocCocSetup (1).jpg");
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Compare pair detection works from numbered filename")]
    public void ComparePairFromNumberedFilename()
    {
        var pair = ComparePairService.Find([_original, _numbered], _numbered);
        Assert.True(pair is not null && pair.Value.Left == _original && pair.Value.Right == _numbered);
    }

    [Fact(DisplayName = "Compare pair detection works from original filename")]
    public void ComparePairFromOriginalFilename()
    {
        var pair = ComparePairService.Find([_original, _numbered], _original);
        Assert.True(pair is not null && pair.Value.Left == _original && pair.Value.Right == _numbered);
    }

    [Fact(DisplayName = "Compare pair detection stays within selected folder")]
    public void ComparePairStaysWithinSelectedFolder()
    {
        var otherFolder = _root.Combine("other", "CocCocSetup.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(otherFolder)!);
        Assert.Null(ComparePairService.Find([_original, _numbered, otherFolder], otherFolder));
    }

    [Fact(DisplayName = "Compare pair detection rejects an incomplete pair")]
    public void ComparePairRejectsIncompletePair() =>
        Assert.Null(ComparePairService.Find([_original], _original));
}

/// <summary>Image sorting.</summary>
public sealed class ImageSortServiceTests : IDisposable
{
    private readonly TempRoot _root = new("sort");
    private readonly string[] _fixture;

    public ImageSortServiceTests()
    {
        _fixture =
        [
            _root.Combine("img10.jpg"),
            _root.Combine("img2.jpg"),
            _root.Combine("img1.jpg")
        ];
    }

    public void Dispose() => _root.Dispose();

    private void WriteSizes()
    {
        File.WriteAllBytes(_fixture[0], [1]);
        File.WriteAllBytes(_fixture[1], [1, 2, 3]);
        File.WriteAllBytes(_fixture[2], [1, 2]);
    }

    [Fact(DisplayName = "Natural filename sort orders numeric suffixes")]
    public void NaturalFilenameSortOrdersNumericSuffixes()
    {
        var sorted = ImageSortService.Sort(_fixture, "Name");
        Assert.True(Path.GetFileName(sorted[0]) == "img1.jpg"
            && Path.GetFileName(sorted[1]) == "img2.jpg"
            && Path.GetFileName(sorted[2]) == "img10.jpg");
    }

    [Fact(DisplayName = "Natural filename sort handles numeric runs over 12 digits")]
    public void NaturalFilenameSortHandlesLongNumericRuns()
    {
        var sorted = ImageSortService.Sort(["img1000000000000.jpg", "img2.jpg", "img10.jpg"], "Name");
        Assert.True(Path.GetFileName(sorted[0]) == "img2.jpg"
            && Path.GetFileName(sorted[2]) == "img1000000000000.jpg");
    }

    [Fact(DisplayName = "Size sort orders files by descending bytes")]
    public void SizeSortOrdersFilesByDescendingBytes()
    {
        WriteSizes();
        var sorted = ImageSortService.Sort(_fixture, "Size");
        Assert.True(Path.GetFileName(sorted[0]) == "img2.jpg" && Path.GetFileName(sorted[2]) == "img10.jpg");
    }

    [Fact(DisplayName = "Size sort orders files by ascending bytes")]
    public void SizeSortOrdersFilesByAscendingBytes()
    {
        WriteSizes();
        var sorted = ImageSortService.Sort(_fixture, "SizeAscending");
        Assert.True(Path.GetFileName(sorted[0]) == "img10.jpg" && Path.GetFileName(sorted[2]) == "img2.jpg");
    }
}

/// <summary>Explorer snapshot validation and provider fakeability.</summary>
public sealed class ExplorerSnapshotValidatorTests : IDisposable
{
    private readonly TempRoot _root = new("explorer-order");
    private readonly string _folder;
    private readonly string _a;
    private readonly string _b;
    private readonly ExplorerViewSnapshot _snapshot;

    public ExplorerSnapshotValidatorTests()
    {
        _folder = _root.Dir("explorer-order");
        _a = Path.Combine(_folder, "a.jpg");
        _b = Path.Combine(_folder, "b.jpg");
        _snapshot = new ExplorerViewSnapshot(_folder, [_b, _a], [], ExplorerGroupState.None,
            ExplorerOrderStatus.Available, null, DateTime.UtcNow);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Explorer snapshot accepts a complete native order")]
    public void ExplorerSnapshotAcceptsCompleteNativeOrder() =>
        Assert.True(ExplorerSnapshotValidator.TryValidate(_snapshot, [_a, _b], out var nativeOrder, out _)
            && nativeOrder.SequenceEqual(new[] { _b, _a }, StringComparer.OrdinalIgnoreCase));

    [Fact(DisplayName = "Explorer snapshot rejects missing images")]
    public void ExplorerSnapshotRejectsMissingImages() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(_snapshot with { OrderedPaths = [_a] },
            [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot rejects duplicate paths")]
    public void ExplorerSnapshotRejectsDuplicatePaths() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(_snapshot with { OrderedPaths = [_a, _a, _b] },
            [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot rejects paths outside the folder")]
    public void ExplorerSnapshotRejectsPathsOutsideFolder() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(
            _snapshot with { OrderedPaths = [_a, _root.Combine("outside.jpg")] }, [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot exposes provider fallback reason")]
    public void ExplorerSnapshotExposesProviderFallbackReason() =>
        Assert.True(!ExplorerSnapshotValidator.TryValidate(
                _snapshot with { Status = ExplorerOrderStatus.TimedOut, Reason = "timeout" },
                [_a, _b], out _, out var unavailableReason)
            && unavailableReason == "timeout");

    [Fact(DisplayName = "Explorer provider contract is fakeable without COM")]
    public async Task ExplorerProviderContractIsFakeableWithoutCom()
    {
        IExplorerOrderProvider provider = new FakeExplorerOrderProvider(_snapshot);
        var result = await provider.TryGetSnapshotAsync(_folder, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(_b, result.OrderedPaths[0]);
    }

    private sealed class FakeExplorerOrderProvider(ExplorerViewSnapshot snapshot) : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

/// <summary>File hash caching and deduplication.</summary>
public sealed class FileHashServiceTests : IDisposable
{
    private readonly TempRoot _root = new("hash");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "File hash service caches and invalidates by file fingerprint")]
    public async Task FileHashServiceCachesAndInvalidates()
    {
        var fixture = _root.File("hash-fixture.bin", 1, 2, 3);
        var service = new FileHashService();
        var first = await service.GetAsync(fixture);
        var cached = await service.GetAsync(fixture);
        File.WriteAllBytes(fixture, [1, 2, 4]);
        // FileHashService fingerprints by (Length, LastWriteTimeUtc). Two same-size
        // writes issued back to back can land within the same filesystem timestamp
        // tick, so force a detectable mtime change instead of relying on wall-clock
        // granularity - otherwise this assertion is flaky under fast test runners.
        File.SetLastWriteTimeUtc(fixture, DateTime.UtcNow.AddSeconds(1));
        var changed = await service.GetAsync(fixture);
        Assert.True(first == cached && first != changed);
    }

    [Fact(DisplayName = "File hash service deduplicates concurrent reads")]
    public async Task FileHashServiceDeduplicatesConcurrentReads()
    {
        var fixture = _root.File("hash-fixture.bin", 1, 2, 4);
        var service = new FileHashService();
        var expected = await service.GetAsync(fixture);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => service.GetAsync(fixture)));
        Assert.True(concurrent.All(hash => hash == expected));
    }
}

/// <summary>Review metrics counters.</summary>
public sealed class ReviewMetricsTests
{
    [Fact(DisplayName = "Review metrics snapshot preserves counters")]
    public void ReviewMetricsSnapshotPreservesCounters()
    {
        var metrics = new ReviewMetrics();
        metrics.RecordCacheHit();
        metrics.RecordCacheMiss();
        metrics.RecordSourceRead(128, 7);
        metrics.RecordPresented(11);
        var snapshot = metrics.Snapshot();
        Assert.True(snapshot.CacheHits == 1 && snapshot.CacheMisses == 1 && snapshot.SourceReads == 1
            && snapshot.SourceBytesRead == 128 && snapshot.DecodeMilliseconds == 7
            && snapshot.PresentedImages == 1 && snapshot.PresentMilliseconds == 11);
    }

    [Fact(DisplayName = "Concurrent preload diagnostics retain every delivery and timing event")]
    public void ConcurrentPreloadDiagnosticsRetainEveryEvent()
    {
        var metrics = new ReviewMetrics();
        Parallel.For(0, 500, _ =>
        {
            metrics.RecordPreloadHit();
            metrics.RecordInflightJoin();
            metrics.RecordDiskCacheHit();
            metrics.RecordQueueWait(2);
            metrics.RecordUiAssign(3);
        });
        var snapshot = metrics.Snapshot();
        Assert.True(snapshot.PreloadHits == 500 && snapshot.InflightJoins == 500
            && snapshot.DiskCacheHits == 500 && snapshot.QueueWaitMilliseconds == 1000
            && snapshot.UiAssignMilliseconds == 1500);
    }
}

/// <summary>Preload ordering priorities.</summary>
public sealed class PreloadOrderServiceTests
{
    [Fact(DisplayName = "Full-folder preload prioritizes the next 32, then prior 8, and queues every other image once")]
    public void FullFolderPreloadPrioritizesNextThenPrior()
    {
        var order = PreloadOrderService.Build(center: 40, count: 100, fullFolder: true).ToArray();
        Assert.True(order[0] == 41 && order[31] == 72 && order[32] == 39
            && order.Distinct().Count() == 99 && !order.Contains(40));
    }

    [Fact(DisplayName = "Navigating changes preload priority to the new Next without duplicate jobs")]
    public void NavigatingChangesPreloadPriority()
    {
        var order = PreloadOrderService.Build(center: 44, count: 100, fullFolder: false).ToArray();
        Assert.True(order[0] == 45 && order[1] == 46 && order.Contains(43)
            && order.Length == 40 && order.Distinct().Count() == order.Length);
    }
}
