namespace PhotoReview.Core.Settings;

/// <summary>
/// feat/mouse-zoom: the Settings window rebuilds <see cref="AppSettings"/> from the fields it knows, and it has no
/// controls for the mouse settings yet (a later redesign adds them). Without this, saving that window would reset
/// <see cref="AppSettings.MouseWheelAction"/>, <see cref="AppSettings.ClickToZoomEnabled"/>,
/// <see cref="AppSettings.ClickZoomPercent"/> and <see cref="AppSettings.KineticPanEnabled"/> to their defaults.
/// Remove once the Settings window copies them itself.
/// </summary>
public static class MouseSettingsCarryOver
{
    /// <summary>Copies the mouse settings from <paramref name="source"/> to <paramref name="target"/>; true when any value changed.</summary>
    public static bool Apply(AppSettings source, AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var changed = target.MouseWheelAction != source.MouseWheelAction
            || target.ClickToZoomEnabled != source.ClickToZoomEnabled
            || target.ClickZoomPercent != source.ClickZoomPercent
            || target.KineticPanEnabled != source.KineticPanEnabled;
        target.MouseWheelAction = source.MouseWheelAction;
        target.ClickToZoomEnabled = source.ClickToZoomEnabled;
        target.ClickZoomPercent = source.ClickZoomPercent;
        target.KineticPanEnabled = source.KineticPanEnabled;
        return changed;
    }
}
