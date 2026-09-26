using PhotoReview.App.Coordinators;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>feat/ui-dark-chrome-toolbar: pure "must the toolbar stay visible" / "is the mouse in its hot zone" rules.</summary>
public sealed class ToolbarAutoHidePolicyTests
{
    [Fact]
    public void MustStayVisible_AutoHideDisabled_IsTrueRegardlessOfOtherState()
    {
        Assert.True(ToolbarAutoHidePolicy.MustStayVisible(autoHideEnabled: false, hasFolderOpen: true, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: false));
    }

    [Fact]
    public void MustStayVisible_NoFolderOpen_IsTrue()
    {
        Assert.True(ToolbarAutoHidePolicy.MustStayVisible(autoHideEnabled: true, hasFolderOpen: false, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: false));
    }

    [Fact]
    public void MustStayVisible_ToolsPopupOpen_IsTrue()
    {
        Assert.True(ToolbarAutoHidePolicy.MustStayVisible(autoHideEnabled: true, hasFolderOpen: true, isToolsPopupOpen: true, isKeyboardFocusInsideToolbar: false));
    }

    [Fact]
    public void MustStayVisible_KeyboardFocusInsideToolbar_IsTrue()
    {
        Assert.True(ToolbarAutoHidePolicy.MustStayVisible(autoHideEnabled: true, hasFolderOpen: true, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: true));
    }

    [Fact]
    public void MustStayVisible_AutoHideEnabledFolderOpenPopupClosedNoFocus_IsFalse()
    {
        Assert.False(ToolbarAutoHidePolicy.MustStayVisible(autoHideEnabled: true, hasFolderOpen: true, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: false));
    }

    // Q-R34: the toolbar hides fully and ONLY the mouse entering its hot zone (or a must-stay-visible rule) brings it back.
    // IsShown deliberately has no key-press / image-change / mouse-elsewhere input: those cannot show a hidden toolbar.
    [Fact]
    public void IsShown_HiddenState_OnlyTheHotZoneBringsItBack()
    {
        Assert.False(ToolbarAutoHidePolicy.IsShown(autoHideEnabled: true, hasFolderOpen: true, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: false, isMouseInsideHotZone: false));
        Assert.True(ToolbarAutoHidePolicy.IsShown(autoHideEnabled: true, hasFolderOpen: true, isToolsPopupOpen: false, isKeyboardFocusInsideToolbar: false, isMouseInsideHotZone: true));
    }

    [Theory]
    [InlineData(false, true, false, false, false)] // auto-hide off
    [InlineData(true, false, false, false, false)] // no folder
    [InlineData(true, true, true, false, false)]   // Tools popup open
    [InlineData(true, true, false, true, false)]   // keyboard focus in the toolbar
    public void IsShown_MustStayVisibleRules_ShowItWithoutTheMouse(bool enabled, bool folder, bool popup, bool focus, bool mouse)
    {
        Assert.True(ToolbarAutoHidePolicy.IsShown(enabled, folder, popup, focus, mouse));
    }

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(60, 0.6)]
    [InlineData(20, 0.2)]
    [InlineData(5, 0.2)]    // below the minimum -> the minimum, never (almost) invisible
    [InlineData(0, 0.2)]
    [InlineData(-30, 0.2)]
    [InlineData(500, 1.0)]  // above 100 -> fully opaque
    public void TargetOpacity_IsTheClampedPercentAsFraction(int percent, double expected)
    {
        Assert.Equal(expected, ToolbarAutoHidePolicy.TargetOpacity(percent), precision: 6);
    }

    [Fact]
    public void DelayMs_UsesOnlyTheToolbarDelay_IndependentOfTheInfoDelay()
    {
        var settings = new AppSettings { ToolbarAutoHideDelayMs = 700, InfoOverlayAutoHideDelayMs = 9000 };
        Assert.Equal(700, ToolbarAutoHidePolicy.DelayMs(settings));
        settings.InfoOverlayAutoHideDelayMs = 1;
        Assert.Equal(700, ToolbarAutoHidePolicy.DelayMs(settings));
        settings.ToolbarAutoHideDelayMs = 2500;
        Assert.Equal(2500, ToolbarAutoHidePolicy.DelayMs(settings));
    }

    [Theory]
    [InlineData(0, 0, true)]     // top-left corner of the toolbar itself
    [InlineData(50, 20, true)]   // inside the toolbar
    [InlineData(-10, -10, true)] // inside the margin, just outside the toolbar's own bounds
    [InlineData(110, 40, true)]  // inside the margin, past the toolbar's right/bottom edge
    [InlineData(-11, 0, false)]  // just past the margin, left
    [InlineData(0, -11, false)]  // just past the margin, top
    [InlineData(111, 20, false)] // just past the margin, right
    [InlineData(50, 41, false)]  // just past the margin, bottom
    public void IsInsideHotZone_MarginInflatesBoundsOnEverySide(double x, double y, bool expected)
    {
        // Toolbar occupies (0,0)-(100,30); a 10px margin inflates it to (-10,-10)-(110,40).
        var actual = ToolbarAutoHidePolicy.IsInsideHotZone(x, y, toolbarLeft: 0, toolbarTop: 0, toolbarWidth: 100, toolbarHeight: 30, margin: 10);
        Assert.Equal(expected, actual);
    }
}
