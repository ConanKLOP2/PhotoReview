using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Chooses the file a settings-triggered folder reload opens at. The presented capture member is kept only when it is still
/// listed after the reload; otherwise a removed RAW member would make the load fall back to the first image.
/// </summary>
internal static class SettingsReloadPath
{
    /// <param name="presentedPath">The toggled capture member shown now, or null when none / stale.</param>
    /// <param name="group">The capture group of the current entry, if any.</param>
    /// <param name="entryPath">The current entry's own path (its representative).</param>
    /// <param name="rawEnabled">RAW support as it will be after the reload.</param>
    /// <param name="pairMode">The pair mode as it will be after the reload.</param>
    public static string? Choose(string? presentedPath, CaptureGroup? group, string? entryPath, bool rawEnabled, RawPairMode pairMode)
    {
        // No toggled member: the entry's representative (the RAW under PreferRaw) is the candidate, and the RAW-off rule applies to it too.
        var candidate = presentedPath ?? entryPath;
        if (candidate is null) return null;
        // Separate: each member is its own entry, so either path stays valid. PreferJpeg / PreferRaw: the group still
        // resolves both members to the one entry. Only RAW off removes the RAW member from the listing.
        _ = pairMode;
        if (!rawEnabled && group is not null
            && string.Equals(candidate, group.RawPath, StringComparison.OrdinalIgnoreCase))
        {
            return group.JpegPath;
        }
        return candidate;
    }
}
