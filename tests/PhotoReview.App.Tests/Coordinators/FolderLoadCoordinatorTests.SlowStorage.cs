using System.Globalization;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// NAS report (2026-09-26): opening a ~500-file JPEG folder over wifi took ~40 s before the first image
/// appeared. Root cause was the pre-AR16 scan (<see cref="Core.Abstractions.IFileSystem.EnumerateReadableFilesWithStat"/>),
/// which opened every file (one FileStream per file, sequentially) before the catalog existed. AR16
/// (commit 67c4502, PR #107, v2.0.111+) already fixed this by listing the directory once and running the
/// per-file readability probe in the background AFTER the first present. These tests guard the general
/// property directly -- per-file file-system calls before the first frame do not grow with folder size --
/// rather than the probe-specific behaviour already covered by FolderLoadCoordinatorTests.Probe.cs.
/// </summary>
public sealed partial class FolderLoadCoordinatorTests
{
    private readonly record struct SlowStorageSnapshot(
        int PerFileCallCountAtFirst,
        int ProbeCountAtFirst,
        int StatCountAtFirst,
        int ProbeCountAfterLoad);

    /// <summary>
    /// Builds an independent fake set (own FakeFileSystem/sink/catalog/coordinator) sized to
    /// <paramref name="fileCount"/> files, loads it, and snapshots the fake's per-file counters at the
    /// exact moment of the first present (via <see cref="FakeFolderLoadSink.OnFirstPresent"/>) and again
    /// once the readability probe (which still runs, just after the first frame) has finished.
    /// </summary>
    private static async Task<SlowStorageSnapshot> LoadAndSnapshotAsync(int fileCount)
    {
        var fs = new FakeFileSystem();
        var paths = new FakeAppPaths();
        var settingsStore = new SettingsStore(paths, fs, new PhotoReview.Core.Diagnostics.NullLog());
        var sessionStore = new SessionStore(paths, fs);
        var catalog = new ReviewCatalog();
        var genClock = new GenerationClock();
        var explorerOrder = new FakeExplorerOrderProvider();
        var sink = new FakeFolderLoadSink();

        var folder = @"C:\nas\folder" + fileCount.ToString(CultureInfo.InvariantCulture);
        CreateImages(fs, folder, fileCount);

        var perFileAtFirst = -1;
        var probeAtFirst = -1;
        var statAtFirst = -1;
        sink.OnFirstPresent = () =>
        {
            perFileAtFirst = fs.PerFileCallCount;
            probeAtFirst = fs.ProbeCount;
            statAtFirst = fs.StatCount;
        };

        using var coordinator = new FolderLoadCoordinator(catalog, genClock, explorerOrder, fs, sessionStore, settingsStore, sink);
        await coordinator.LoadAsync(folder).WithTimeout(Wait.DefaultTimeout, $"load of {fileCount} files");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.True(perFileAtFirst >= 0, "OnFirstPresent never fired");
        return new SlowStorageSnapshot(perFileAtFirst, probeAtFirst, statAtFirst, fs.ProbeCount);
    }

    [Fact(DisplayName = "NAS report: per-file I/O before the first frame is a small folder-size-independent constant, not O(N)")]
    public async Task Load_PerFileCallsBeforeFirstFrame_DoNotGrowWithFolderSize()
    {
        var small = await LoadAndSnapshotAsync(20);
        var large = await LoadAndSnapshotAsync(500);

        // The only per-file-system call the coordinator makes before PresentAsync, for either folder
        // size, is SessionStore.Load's single FileExists check for the SESSION file (one file, not one
        // per catalog entry -- see SessionStore.Load, called once per LoadAsync). DirectoryExists is
        // also called once but for the folder itself, and PerFileCallCount does not track it (it is
        // per-load/per-folder, not per-file, so it does not matter here). Nothing else -- no
        // GetFileStat, no additional FileExists, no OpenReadShared, no TryProbeReadable -- runs before
        // the first frame, for 20 files or for 500.
        Assert.Equal(small.PerFileCallCountAtFirst, large.PerFileCallCountAtFirst);
        Assert.True(
            large.PerFileCallCountAtFirst <= 2,
            $"expected a small folder-size-independent constant (<=2), got {large.PerFileCallCountAtFirst}");

        // The scan itself never calls GetFileStat per file (FakeFileSystem.EnumerateFilesWithStat gets
        // stat data from the one listing, mirroring PhysicalFileSystem) -- it must stay far below N.
        Assert.True(small.StatCountAtFirst < 20);
        Assert.True(large.StatCountAtFirst < 500);

        // AR16: the readability probe has not even started when the first frame is presented...
        Assert.Equal(0, small.ProbeCountAtFirst);
        Assert.Equal(0, large.ProbeCountAtFirst);
        // ...but it does still run, just after, and still covers every file.
        Assert.Equal(20, small.ProbeCountAfterLoad);
        Assert.Equal(500, large.ProbeCountAfterLoad);
    }

    // Load_FirstFrame_DoesNotWaitForSlowPerFileProbe_500Files was planned but is intentionally not added:
    // its core scenario (first frame presented while a per-file probe is held) is the same
    // HoldProbeOf/FirstPresented pattern FolderLoadCoordinatorTests.Probe.cs's
    // Probe_FirstFrameDoesNotWaitForProbe_ThenUnreadableIsRemovedAndReported already exercises, just at
    // 3 files instead of 500 -- the file count does not change what that scenario proves. The one new
    // idea, asserting ProbeCount stays "<= ProbeParallelism (8)" while one file's probe is held, turned
    // out not to be a reliable assertion against this in-memory fake: TryProbeReadable does no real I/O,
    // so once one Parallel.For slot blocks on the held file, the other (up to) 7 slots race through the
    // remaining ~499 essentially instantly and ProbeCount shoots past 8 long before any observation point
    // -- the bound only holds against real (slow) I/O, which this fake cannot represent without a banned
    // fixed delay. Load_PerFileCallsBeforeFirstFrame_DoNotGrowWithFolderSize above already asserts the
    // property that actually matters here: ProbeCount is 0 at the first frame for both 20 and 500 files.
}
