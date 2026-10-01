namespace PhotoReview.App.Tests.Services;

/// <summary>
/// RV-T32: the visibility rule of a saved placement is "at least 80x80 px inside a monitor WORK area" (taskbar excluded).
/// The private <c>IsVisible</c> reads the real monitors, so the scenarios are derived from the first real work area
/// (reached by reflection) instead of hard-coded coordinates: they hold for any resolution, DPI or taskbar position.
/// </summary>
public sealed class WindowPlacementVisibilityTests
{
    private static System.Drawing.Rectangle FirstWorkArea()
    {
        var areas = (List<System.Drawing.Rectangle>)typeof(WindowPlacementService)
            .GetMethod("GetMonitorWorkAreas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.NotEmpty(areas);
        var work = areas[0];
        Assert.True(work.Width >= 400 && work.Height >= 400, "The monitor work area is unexpectedly small.");
        return work;
    }

    /// <summary>True when ANOTHER monitor's work area would make these bounds valid anyway (multi-monitor layouts): the negative scenarios are then meaningless.</summary>
    private static bool AnotherMonitorInterferes(int left, int top, int right, int bottom)
    {
        var areas = (List<System.Drawing.Rectangle>)typeof(WindowPlacementService)
            .GetMethod("GetMonitorWorkAreas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
        var bounds = System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
        return areas.Skip(1).Any(area =>
        {
            var part = System.Drawing.Rectangle.Intersect(bounds, area);
            return part.Width >= 80 && part.Height >= 80;
        });
    }

    private static bool IsVisible(int left, int top, int right, int bottom)
    {
        var bounds = new WindowPlacementService.Rectangle { Left = left, Top = top, Right = right, Bottom = bottom };
        return (bool)typeof(WindowPlacementService)
            .GetMethod("IsVisible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [bounds])!;
    }

    [Fact]
    public void IsVisible_PlacementFullyInsideTheWorkArea_IsValid()
    {
        var work = FirstWorkArea();

        Assert.True(IsVisible(work.Left + 50, work.Top + 50, work.Left + 450, work.Top + 450));
    }

    [Fact]
    public void IsVisible_PlacementPartlyUnderTheTaskbarButWithEnoughWorkAreaPixels_StaysValid()
    {
        var work = FirstWorkArea();

        // Hangs 400 px past the work area's bottom-right corner (under the taskbar / off-monitor); 100x100 px remain inside it.
        Assert.True(IsVisible(work.Right - 100, work.Bottom - 100, work.Right + 400, work.Bottom + 400));
    }

    [Fact]
    public void IsVisible_PlacementWithOnly79PixelsInsideTheWorkArea_IsRejected()
    {
        var work = FirstWorkArea();

        if (!AnotherMonitorInterferes(work.Right - 79, work.Bottom - 200, work.Right + 400, work.Bottom + 400))
            Assert.False(IsVisible(work.Right - 79, work.Bottom - 200, work.Right + 400, work.Bottom + 400));
        if (!AnotherMonitorInterferes(work.Right - 200, work.Bottom - 79, work.Right + 400, work.Bottom + 400))
            Assert.False(IsVisible(work.Right - 200, work.Bottom - 79, work.Right + 400, work.Bottom + 400));
        Assert.True(IsVisible(work.Right - 80, work.Bottom - 80, work.Right + 400, work.Bottom + 400)); // exactly 80x80
    }

    [Fact]
    public void IsVisible_PlacementEntirelyBelowTheWorkArea_IsRejectedEvenThoughItMayBeOnTheMonitor()
    {
        var work = FirstWorkArea();

        // Starts at the work area's bottom edge: in the taskbar strip (or past the monitor), never inside the work area.
        if (AnotherMonitorInterferes(work.Left + 50, work.Bottom, work.Left + 450, work.Bottom + 300)) return;
        Assert.False(IsVisible(work.Left + 50, work.Bottom, work.Left + 450, work.Bottom + 300));
    }

    [Theory]
    [InlineData(100, 100, 100, 400)]   // zero width
    [InlineData(100, 100, 400, 100)]   // zero height
    [InlineData(400, 400, 100, 100)]   // inverted
    public void IsVisible_EmptyOrInvertedBounds_AreRejected(int left, int top, int right, int bottom)
    {
        Assert.False(IsVisible(left, top, right, bottom));
    }
}
