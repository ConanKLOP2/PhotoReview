using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Services;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// P-1 startup: <see cref="WindowPlacementService.Prefetch"/> reads the placement file on the thread pool and
/// <see cref="WindowPlacementService.RestoreBeforeShow"/> takes that result exactly once, for the same path only.
/// The prefetched value is told apart from a fresh read by rewriting the file after the prefetch finished: a window that
/// restores Maximized got the prefetched placement, one that restores Normal read the file again. Windows only get a
/// native handle (EnsureHandle) and are never shown. The prefetch slot is static: every test ends with it consumed.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WindowPlacementPrefetchTests
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true };

    private static string WritePlacement(string directory, string fileName, int showCommand)
    {
        var area = FirstWorkArea();
        var path = Path.Combine(directory, fileName);
        var placement = new WindowPlacementService.WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>(),
            ShowCommand = showCommand,
            NormalPosition = new WindowPlacementService.Rectangle
            {
                Left = area.Left + 100, Top = area.Top + 100, Right = area.Left + 600, Bottom = area.Top + 450,
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(placement, PlacementJson));
        return path;
    }

    private static System.Drawing.Rectangle FirstWorkArea()
    {
        var areas = (List<System.Drawing.Rectangle>)typeof(WindowPlacementService)
            .GetMethod("GetMonitorWorkAreas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.NotEmpty(areas);
        return areas[0];
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhotoReview_PlacementPrefetch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Restores <paramref name="path"/> into a fresh hidden window and returns the state it ended in (null: nothing applied).</summary>
    private static WindowState? RestoreIntoNewWindow(string path)
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            return WindowPlacementService.RestoreBeforeShow(window, path) ? window.WindowState : null;
        }
        finally { window.Close(); }
    }

    [Fact(DisplayName = "A prefetched placement is used by the next RestoreBeforeShow, once; the one after reads the file again")]
    public async Task RestoreBeforeShow_AfterPrefetch_UsesPrefetchedPlacementExactlyOnce()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = WritePlacement(directory, "placement.json", ShowMaximized);
            var prefetched = await WindowPlacementService.Prefetch(path);
            Assert.Equal(ShowMaximized, prefetched!.ShowCommand);
            WritePlacement(directory, "placement.json", ShowNormal); // the file no longer says what was prefetched

            WindowState? first = null, second = null;
            StaUi.Run(() =>
            {
                first = RestoreIntoNewWindow(path);
                // The first restore consumed the prefetch (and reshaped that object), so the next one must read the file,
                // which now says Maximized again: a prefetch that stayed around would reopen Normal here.
                WritePlacement(directory, "placement.json", ShowMaximized);
                second = RestoreIntoNewWindow(path);
            });

            Assert.Equal(WindowState.Maximized, first); // prefetched value, not the Normal on disk
            Assert.Equal(WindowState.Maximized, second); // read from the file after the prefetch was used up
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(DisplayName = "A prefetch of another path is not used for a different file, and stays available for its own path")]
    public async Task RestoreBeforeShow_PrefetchOfAnotherPath_IsIgnoredButKept()
    {
        var directory = NewTempDirectory();
        try
        {
            var prefetchedPath = WritePlacement(directory, "prefetched.json", ShowMaximized);
            var otherPath = WritePlacement(directory, "other.json", ShowNormal);
            await WindowPlacementService.Prefetch(prefetchedPath);
            WritePlacement(directory, "prefetched.json", ShowNormal);

            WindowState? other = null, own = null;
            StaUi.Run(() =>
            {
                other = RestoreIntoNewWindow(otherPath);   // must read other.json, not take the foreign prefetch
                own = RestoreIntoNewWindow(prefetchedPath); // the prefetch is still there for its own path
            });

            Assert.Equal(WindowState.Normal, other);
            Assert.Equal(WindowState.Maximized, own);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(DisplayName = "The prefetch is matched to the path ignoring case, like the file system")]
    public async Task RestoreBeforeShow_PrefetchedPathDifferingOnlyByCase_IsUsed()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = WritePlacement(directory, "placement.json", ShowMaximized);
            await WindowPlacementService.Prefetch(path);
            WritePlacement(directory, "placement.json", ShowNormal);

            WindowState? state = null;
            StaUi.Run(() => state = RestoreIntoNewWindow(path.ToUpperInvariant()));

            Assert.Equal(WindowState.Maximized, state);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(DisplayName = "A prefetch of a missing file leaves RestoreBeforeShow to read (and find) nothing")]
    public async Task RestoreBeforeShow_PrefetchOfMissingFile_AppliesNothing()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "never-written.json");
            Assert.Null(await WindowPlacementService.Prefetch(path));

            WindowState? state = WindowState.Maximized; // overwritten by the restore (null = nothing applied)
            StaUi.Run(() => state = RestoreIntoNewWindow(path));

            Assert.Null(state);
        }
        finally { Directory.Delete(directory, true); }
    }
}
