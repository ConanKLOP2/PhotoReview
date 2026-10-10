using System.IO;
using System.Text.Json;
using PhotoReview.App.Windowing;
using Xunit;

namespace PhotoReview.App.Tests.Windowing;

/// <summary>
/// NO-WPF WP-08 (C-14): <see cref="JsonWindowPlacementStore"/> must read and write the exact window-placement.json that
/// the old WPF code produced (N-3: no format change). "Old code" here is the legacy <c>WindowPlacementService.WindowPlacement</c>
/// class serialized with the options the old service used (IncludeFields, WriteIndented). Monitor layout is injected, so
/// nothing depends on the machine's displays.
/// </summary>
public sealed class JsonWindowPlacementStoreRoundTripTests : IDisposable
{
    private const string GoldenFile =
        "{\n" +
        "  \"Length\": 44,\n" +
        "  \"Flags\": 2,\n" +
        "  \"ShowCommand\": 3,\n" +
        "  \"MinPosition\": {\n" +
        "    \"X\": -1,\n" +
        "    \"Y\": -2\n" +
        "  },\n" +
        "  \"MaxPosition\": {\n" +
        "    \"X\": -8,\n" +
        "    \"Y\": -31\n" +
        "  },\n" +
        "  \"NormalPosition\": {\n" +
        "    \"Left\": 100,\n" +
        "    \"Top\": 120,\n" +
        "    \"Right\": 1300,\n" +
        "    \"Bottom\": 920\n" +
        "  }\n" +
        "}";

    private static readonly JsonSerializerOptions LegacyOptions = new() { IncludeFields = true, WriteIndented = true };

    private static readonly WindowPlacementData Sample = new(44, 2, 3, -1, -2, -8, -31, 100, 120, 1300, 920);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PhotoReview_PlacementStore_" + Guid.NewGuid().ToString("N"));

    public JsonWindowPlacementStoreRoundTripTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FixedLayout(params ScreenRect[] works) : IMonitorLayout
    {
        public nint MonitorOf(nint hwnd) => 1;

        public bool TryGetWork(nint monitor, out ScreenRect work)
        {
            work = works[0];
            return true;
        }

        public IReadOnlyList<ScreenRect> WorkAreas() => works;
    }

    private static JsonWindowPlacementStore StoreWith(params ScreenRect[] works) => new(new FixedLayout(works));

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string LegacyJson(WindowPlacementData d) => JsonSerializer.Serialize(new WindowPlacementService.WindowPlacement
    {
        Length = d.Length,
        Flags = d.Flags,
        ShowCommand = d.ShowCommand,
        MinPosition = new WindowPlacementService.Point { X = d.MinX, Y = d.MinY },
        MaxPosition = new WindowPlacementService.Point { X = d.MaxX, Y = d.MaxY },
        NormalPosition = new WindowPlacementService.Rectangle { Left = d.NormalLeft, Top = d.NormalTop, Right = d.NormalRight, Bottom = d.NormalBottom },
    }, LegacyOptions);

    [Fact(DisplayName = "WP-08: a file written by the old code reads back, and writing it again is byte-identical (line endings normalized)")]
    public void LegacyFile_ReadThenSave_IsByteIdentical()
    {
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, LegacyJson(Sample));
        var original = File.ReadAllText(path);
        var store = StoreWith(new ScreenRect(0, 0, 1920, 1040));

        var read = JsonWindowPlacementStore.ReadRaw(path);
        Assert.Equal(Sample, read);
        var again = Path.Combine(_directory, "again.json");
        store.Save(again, read!);

        Assert.Equal(Normalize(original), Normalize(File.ReadAllText(again)));
    }

    [Fact(DisplayName = "WP-08: the JSON field names and layout are the ones window-placement.json always had (golden)")]
    public void Save_WritesTheGoldenLayout()
    {
        var path = Path.Combine(_directory, "golden.json");

        StoreWith().Save(path, Sample);

        Assert.Equal(GoldenFile, Normalize(File.ReadAllText(path)));
        Assert.Equal(GoldenFile, Normalize(LegacyJson(Sample)));
    }

    [Fact(DisplayName = "WP-08: a file missing ShowCommand keeps the legacy default SW_SHOWMAXIMIZED")]
    public void ReadRaw_MissingShowCommand_DefaultsToMaximized()
    {
        var path = Path.Combine(_directory, "partial.json");
        File.WriteAllText(path, "{\"NormalPosition\":{\"Left\":1,\"Top\":2,\"Right\":3,\"Bottom\":4}}");

        var read = JsonWindowPlacementStore.ReadRaw(path);

        Assert.NotNull(read);
        Assert.Equal(3, read.ShowCommand);
        Assert.Equal((1, 2, 3, 4), (read.NormalLeft, read.NormalTop, read.NormalRight, read.NormalBottom));
    }

    [Fact(DisplayName = "WP-08: Save creates the missing directory and leaves no temp file behind")]
    public void Save_CreatesDirectory_LeavesNoTempFile()
    {
        var nested = Path.Combine(_directory, "a", "b");
        var path = Path.Combine(nested, "window-placement.json");

        StoreWith().Save(path, Sample);

        Assert.Equal([path], Directory.GetFiles(nested));
    }

    [Fact(DisplayName = "WP-08: Read returns a visible placement, and null for missing, damaged and off-screen files")]
    public void Read_FiltersMissingDamagedAndInvisible()
    {
        var store = StoreWith(new ScreenRect(0, 0, 1920, 1040));
        var visible = Path.Combine(_directory, "visible.json");
        store.Save(visible, Sample);
        var offscreen = Path.Combine(_directory, "offscreen.json");
        store.Save(offscreen, Sample with { NormalLeft = 5000, NormalRight = 6000 });
        var damaged = Path.Combine(_directory, "damaged.json");
        File.WriteAllText(damaged, "{ not json");

        Assert.Equal(Sample, store.Read(visible));
        Assert.Null(store.Read(offscreen));
        Assert.Null(store.Read(damaged));
        Assert.Null(store.Read(Path.Combine(_directory, "never-written.json")));
    }

    [Fact(DisplayName = "WP-08: a damaged file makes ReadRaw throw, so callers can log it")]
    public void ReadRaw_Damaged_Throws()
    {
        var damaged = Path.Combine(_directory, "damaged.json");
        File.WriteAllText(damaged, "{ not json");

        Assert.ThrowsAny<JsonException>(() => JsonWindowPlacementStore.ReadRaw(damaged));
    }

    [Theory(DisplayName = "WP-08: a placement is visible only when 80x80 px of it lies inside a work area")]
    [InlineData(1840, 960, 3000, 2000, true)]    // exactly 80x80 inside
    [InlineData(1841, 960, 3000, 2000, false)]   // 79 wide
    [InlineData(1840, 961, 3000, 2000, false)]   // 79 high
    [InlineData(100, 100, 100, 500, false)]      // empty
    [InlineData(500, 500, 400, 600, false)]      // inverted
    public void IsVisible_RequiresAnEightyPixelIntersection(int left, int top, int right, int bottom, bool expected)
    {
        var store = StoreWith(new ScreenRect(0, 0, 1920, 1040));

        Assert.Equal(expected, store.IsVisible(Sample with { NormalLeft = left, NormalTop = top, NormalRight = right, NormalBottom = bottom }));
    }

    [Fact(DisplayName = "WP-08: the prefetched placement is unfiltered, taken once and only for the same path")]
    public async Task Prefetch_IsUnfilteredAndTakenOnceForTheSamePath()
    {
        var store = StoreWith(new ScreenRect(0, 0, 1920, 1040));
        var path = Path.Combine(_directory, "prefetch.json");
        var offscreen = Sample with { NormalLeft = 5000, NormalRight = 6000 };
        store.Save(path, offscreen);

        var loaded = await store.Prefetch(path);

        Assert.Equal(offscreen, loaded); // a Maximized window saved off-screen must still reopen Maximized
        Assert.Null(store.TakePrefetched(Path.Combine(_directory, "other.json")));
        Assert.Equal(offscreen, store.TakePrefetched(path.ToUpperInvariant()));
        Assert.Null(store.TakePrefetched(path)); // consumed
    }

    [Fact(DisplayName = "WP-08: a failed prefetch yields null and nothing to take, so the caller reads (and logs) the file itself")]
    public async Task Prefetch_DamagedFile_YieldsNullAndNothingToTake()
    {
        var store = StoreWith();
        var damaged = Path.Combine(_directory, "damaged.json");
        File.WriteAllText(damaged, "{ not json");

        Assert.Null(await store.Prefetch(damaged));
        Assert.Null(store.TakePrefetched(damaged));
    }

    [Theory(DisplayName = "WP-08: only normal and maximized show commands survive (rules)")]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 1)]
    [InlineData(-5, 1)]
    public void Rules_Normalize(int input, int expected)
    {
        Assert.Equal(expected, WindowPlacementRules.NormalizeShowCommand(input));
        Assert.Equal(expected == 3 ? WindowShowState.Maximized : WindowShowState.Normal, WindowPlacementRules.PlanStateBeforeShow(input));
    }

    [Theory(DisplayName = "WP-08: closing in fullscreen saves the pre-fullscreen state; otherwise the normalized current command")]
    [InlineData(3, WindowShowState.Normal, 1)]
    [InlineData(3, WindowShowState.Minimized, 1)]
    [InlineData(1, WindowShowState.Maximized, 3)]
    [InlineData(3, null, 3)]
    [InlineData(2, null, 1)]
    public void Rules_ResolveShowCommand(int current, WindowShowState? before, int expected) =>
        Assert.Equal(expected, WindowPlacementRules.ResolveShowCommand(current, before));

    [Fact(DisplayName = "WP-08: the fullscreen controller on a window without an HWND records the state and asks the framework to maximize")]
    public void Controller_WithoutHwnd_DelegatesToTheFramework()
    {
        var controller = new FullscreenController(new FixedLayout());

        controller.Enter(0, WindowShowState.Maximized);
        Assert.Equal(WindowShowState.Maximized, controller.StateBefore);
        Assert.True(controller.EnterNeedsMaximizedState);
        Assert.Null(controller.NormalBounds);

        controller.Exit(0);
        Assert.True(controller.ExitNeedsStateRestore);
    }

    [Fact(DisplayName = "WP-08: a minimized window counts as Normal before fullscreen")]
    public void Controller_Minimized_CountsAsNormal()
    {
        var controller = new FullscreenController(new FixedLayout());

        controller.Enter(0, WindowShowState.Minimized);

        Assert.Equal(WindowShowState.Normal, controller.StateBefore);
    }

    [Theory(DisplayName = "WP-08: the caption band of a saved Normal rect stays usable at exactly 120x16 px inside a work area, not below")]
    [InlineData(680, 0, 2000, 1000, true)]    // 120 px wide (800 - 680)
    [InlineData(681, 0, 2000, 1000, false)]   // 119 px wide
    [InlineData(0, -100, 1000, 16, true)]     // 16 px of the band above the work area's bottom edge
    [InlineData(0, -100, 1000, 15, false)]    // 15 px
    [InlineData(0, 16, 1000, 1000, true)]     // band is 32 px tall: 16 px below the work area's top edge
    [InlineData(0, 17, 1000, 1000, false)]    // 15 px
    public void ResolveNormalExitRect_CaptionBandBoundaries(int left, int top, int right, int bottom, bool keptAsIs)
    {
        var saved = new ScreenRect(0, 0, 800, 600);

        var resolved = FullscreenController.ResolveNormalExitRect(saved, [new ScreenRect(left, top, right, bottom)]);

        Assert.Equal(keptAsIs, resolved == saved);
    }
}
