using PhotoReview.App.Coordinators;

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
