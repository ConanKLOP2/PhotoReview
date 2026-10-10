using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Services;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// perf/startup-first-image: the startup window is put in its saved placement while still hidden
/// (<see cref="WindowPlacementService.RestoreBeforeShow"/>) and cloaked until its first frame (<see cref="StartupWindowReveal"/>).
/// Windows here only get a native handle (EnsureHandle) and are never shown: a restore that showed the window would fail
/// the "still hidden" assertions.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class StartupFirstShowTests
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    // ---- StartupWindowReveal ----

    private static List<(IntPtr Handle, bool Cloaked)> WithRecordedCloak(bool accept, Action<List<(IntPtr, bool)>> body)
    {
        var calls = new List<(IntPtr, bool)>();
        var previous = StartupWindowReveal.SetCloaked;
        StartupWindowReveal.SetCloaked = (handle, cloaked) => { calls.Add((handle, cloaked)); return accept; };
        try { body(calls); }
        finally { StartupWindowReveal.SetCloaked = previous; }
        return calls;
    }

    [Fact]
    public void Reveal_AfterHide_UncloaksTheSameWindowExactlyOnce()
    {
        var calls = WithRecordedCloak(accept: true, _ =>
        {
            var gate = new StartupWindowReveal();
            gate.Hide(new IntPtr(42));
            Assert.True(gate.IsHidden);
            gate.Reveal();
            gate.Reveal(); // timeout + ContentRendered may both fire
            Assert.False(gate.IsHidden);
        });

        Assert.Equal([(new IntPtr(42), true), (new IntPtr(42), false)], calls);
    }

    [Fact]
    public void Hide_IsAppliedOnlyOnce_AndNeverAfterReveal()
    {
        var calls = WithRecordedCloak(accept: true, _ =>
        {
            var gate = new StartupWindowReveal();
            gate.Hide(new IntPtr(7));
            gate.Hide(new IntPtr(7));
            gate.Reveal();
            gate.Hide(new IntPtr(7)); // a later SourceInitialized (never expected) must not hide the window again
            Assert.False(gate.IsHidden);
        });

        Assert.Equal([(new IntPtr(7), true), (new IntPtr(7), false)], calls);
    }

    [Fact]
    public void Hide_RefusedByDwm_LeavesTheWindowVisibleAndRevealDoesNothing()
    {
        var calls = WithRecordedCloak(accept: false, _ =>
        {
            var gate = new StartupWindowReveal();
            gate.Hide(new IntPtr(9));
            Assert.False(gate.IsHidden);
            gate.Reveal();
        });

        Assert.Equal([(new IntPtr(9), true)], calls);
    }

    [Fact]
    public void Hide_WithoutAHandle_IsIgnored()
    {
        var calls = WithRecordedCloak(accept: true, _ =>
        {
            var gate = new StartupWindowReveal();
            gate.Hide(IntPtr.Zero);
            Assert.False(gate.IsHidden);
        });

        Assert.Empty(calls);
    }

    // ---- WindowPlacementService.RestoreBeforeShow ----

    [Theory]
    [InlineData(ShowMaximized, WindowState.Maximized)]
    [InlineData(ShowNormal, WindowState.Normal)]
    [InlineData(2 /* SW_SHOWMINIMIZED */, WindowState.Normal)]
    [InlineData(0 /* SW_HIDE */, WindowState.Normal)]
    public void PlanStateBeforeShow_ReopensOnlyMaximizedOrNormal(int saved, WindowState expected) =>
        Assert.Equal(expected, WindowPlacementService.PlanStateBeforeShow(saved));

    [Theory]
    [InlineData(ShowMaximized, WindowState.Maximized)]
    [InlineData(ShowNormal, WindowState.Normal)]
    public void RestoreBeforeShow_VisiblePlacement_SetsBoundsAndStateWithoutShowingTheWindow(int showCommand, WindowState expected)
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var work = FirstWorkArea();
            var path = WritePlacementFile(directory, showCommand, work.Left + 100, work.Top + 100, work.Left + 600, work.Top + 450);
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert.True(WindowPlacementService.RestoreBeforeShow(window, path));

                Assert.False(IsWindowVisible(handle), "Restoring before Show() must not show the window (it would flash at the wrong state)");
                Assert.Equal(expected, window.WindowState);
                var applied = ReadPlacement(handle).NormalPosition;
                Assert.InRange(applied.Left, work.Left + 98, work.Left + 102);
                Assert.InRange(applied.Top, work.Top + 98, work.Top + 102);
                Assert.InRange(applied.Right - applied.Left, 498, 502);
                Assert.InRange(applied.Bottom - applied.Top, 348, 352);
            }
            finally { window.Close(); }
        }));
    }

    [Fact]
    public void RestoreBeforeShow_MaximizedOnAMonitorThatIsGone_StillReopensMaximized()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var path = WritePlacementFile(directory, ShowMaximized, -30000, -30000, -29500, -29600);
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var before = ReadPlacement(handle).NormalPosition;
            try
            {
                Assert.True(WindowPlacementService.RestoreBeforeShow(window, path));

                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.False(IsWindowVisible(handle));
                var after = ReadPlacement(handle).NormalPosition; // the unreachable bounds are never applied
                Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
            }
            finally { window.Close(); }
        }));
    }

    [Fact]
    public void RestoreBeforeShow_NormalOnAMonitorThatIsGone_IsIgnored()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var path = WritePlacementFile(directory, ShowNormal, -30000, -30000, -29500, -29600);
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var before = ReadPlacement(handle).NormalPosition;
            try
            {
                Assert.False(WindowPlacementService.RestoreBeforeShow(window, path));

                Assert.Equal(WindowState.Normal, window.WindowState);
                var after = ReadPlacement(handle).NormalPosition;
                Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
            }
            finally { window.Close(); }
        }));
    }

    [Fact]
    public void RestoreBeforeShow_MissingOrDamagedFile_AppliesNothing()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var damaged = Path.Combine(directory, "damaged.json");
            File.WriteAllText(damaged, "{ not json");
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert.False(WindowPlacementService.RestoreBeforeShow(window, Path.Combine(directory, "never-written.json")));
                Assert.False(WindowPlacementService.RestoreBeforeShow(window, damaged));
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally { window.Close(); }
        }));
    }

    [Fact]
    public void RestoreBeforeShow_ForAWindowWithoutAHandle_AppliesNothing()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var work = FirstWorkArea();
            var path = WritePlacementFile(directory, ShowMaximized, work.Left + 100, work.Top + 100, work.Left + 600, work.Top + 450);
            var window = new Window();

            Assert.False(WindowPlacementService.RestoreBeforeShow(window, path));

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        }));
    }

    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    private static System.Drawing.Rectangle FirstWorkArea()
    {
        var areas = (List<System.Drawing.Rectangle>)typeof(WindowPlacementService)
            .GetMethod("GetMonitorWorkAreas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.NotEmpty(areas);
        return areas[0];
    }

    private static WindowPlacementService.WindowPlacement ReadPlacement(IntPtr handle)
    {
        var placement = new WindowPlacementService.WindowPlacement { Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>() };
        var ok = (bool)typeof(WindowPlacementService)
            .GetMethod("GetWindowPlacement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [handle, placement])!;
        Assert.True(ok);
        return placement;
    }

    private static string WritePlacementFile(string directory, int showCommand, int left, int top, int right, int bottom)
    {
        var path = Path.Combine(directory, "placement.json");
        var placement = new WindowPlacementService.WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>(),
            ShowCommand = showCommand,
            NormalPosition = new WindowPlacementService.Rectangle { Left = left, Top = top, Right = right, Bottom = bottom },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(placement, PlacementJson));
        return path;
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhotoReview_StartupShow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
