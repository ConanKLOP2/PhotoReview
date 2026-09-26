namespace PhotoReview.App.Coordinators;

/// <summary>
/// Pure decision logic for the top-left toolbar's auto-hide feature (feat/ui-dark-chrome-toolbar):
/// whether the toolbar must stay visible regardless of the mouse/hide-timer state. Kept dependency-free
/// (no WPF types) so the rule is unit-testable without a Dispatcher or a real window.
/// </summary>
public static class ToolbarAutoHidePolicy
{
    /// <summary>
    /// True when the toolbar must be forced visible: auto-hide is off, no folder is open yet, the
    /// toolbar's Tools popup is open, or keyboard focus is currently inside the toolbar.
    /// </summary>
    public static bool MustStayVisible(bool autoHideEnabled, bool hasFolderOpen, bool isToolsPopupOpen, bool isKeyboardFocusInsideToolbar) =>
        !autoHideEnabled || !hasFolderOpen || isToolsPopupOpen || isKeyboardFocusInsideToolbar;

    /// <summary>Whether the mouse position is inside the toolbar's hot zone (its bounds, inflated by <paramref name="margin"/> on every side).</summary>
    public static bool IsInsideHotZone(double mouseX, double mouseY, double toolbarLeft, double toolbarTop, double toolbarWidth, double toolbarHeight, double margin) =>
        mouseX >= toolbarLeft - margin && mouseX <= toolbarLeft + toolbarWidth + margin &&
        mouseY >= toolbarTop - margin && mouseY <= toolbarTop + toolbarHeight + margin;
}
