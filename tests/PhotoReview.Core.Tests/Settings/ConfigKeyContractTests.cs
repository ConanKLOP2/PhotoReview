using System.Text.Json;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Audit C-01 (2026-10-10): renaming a property of <see cref="AppSettings"/> (or its JSON name) silently drops that setting from every
/// existing user's config.json on upgrade, and nothing failed (mutant: rename ClickZoomKeyTogglesFit, 0 tests red) because every
/// round-trip test uses the same serializer context. This freezes the PUBLISHED key names of config.json.
/// Adding a key is fine: append it to <see cref="FrozenKeys"/> (and give it a default that keeps old files working).
/// Renaming or removing a key needs a migration in SettingsNormalizer/SettingsStore and an explicit decision, not a silent edit.
/// </summary>
public sealed class ConfigKeyContractTests
{
    private static readonly string[] FrozenKeys =
    [
        "ConfigVersion",
        "InitialViewMode",
        "LoadingMode",
        "LoggingEnabled",
        "ImageSortMode",
        "CompareHashEnabled",
        "CompareSizeEnabled",
        "ScalingQuality",
        "DecoderBackend",
        "ImageCacheCapacityBytes",
        "ImageCacheRamPercent",
        "MemoryReserveBytes",
        "PreloadWorkerCount",
        "PreloadMemoryLoadLimit",
        "PreviewDiskCacheCapacityBytes",
        "UseSourceBytesCache",
        "SourceBytesCapacityBytes",
        "Actions",
        "Shortcuts",
        "UiLanguage",
        "JournalDurability",
        "AllowPermanentDeleteWithoutRecycleBin",
        "LastMoveToFolder",
        "LastCopyToFolder",
        "MoveCopyReuseLastFolder",
        "InstanceMode",
        "ShowInfoOverlay",
        "ShowFileInfo",
        "ShowFolderInfo",
        "ShowExifInfo",
        "ExifInfoFields",
        "MouseWheelAction",
        "ClickToZoomEnabled",
        "ClickZoomPercent",
        "KineticPanEnabled",
        "PreloadForwardCount",
        "PreloadBackwardCount",
        "ToolbarAutoHide",
        "ToolbarAutoHideDelayMs",
        "InfoOverlayFontSize",
        "TitleBarFields",
        "KeyboardZoomStepPercent",
        "ConfirmBeforeDelete",
        "ShowZoomIndicator",
        "ExternalEditorPath",
        "KineticGlideSmoothing",
        "InfoOverlayAutoHide",
        "InfoOverlayAutoHideDelayMs",
        "ToolbarOpacityPercent",
        "ArrowKeyNavigatesAtZoomEdge",
        "ClickZoomKeyTogglesFit",
        "ArrowPanStepPercent",
        "KeepZoomAcrossImages",
        "FitWidthAnchor",
        "FitWidthAnchor2",
        "MiddleClickAction",
        "KeyboardZoomAnchor",
        "ImageTransition",
        "ImageTransitionMs",
        "SetZoomAlsoSetsClickLevel",
        "ShowFolderMenuItems",
        "ShowZoomMenuItems",
        "ShowRecycleMenuItem",
        "HiddenContextMenuItems",
        "RawSupportEnabled",
        "RawFullDecode",
        "RawPairMode",
        "WebpHeicSupportEnabled",
        "TouchpadSwipeEnabled",
        "TouchpadSwipeDistancePerImage",
    ];

    private static string[] CurrentKeys()
    {
        var json = JsonSerializer.Serialize(new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    [Fact(DisplayName = "config.json keeps every published key (a rename or removal would silently drop users' settings)")]
    public void EveryPublishedKey_IsStillWritten()
    {
        var current = CurrentKeys();

        var missing = FrozenKeys.Except(current, StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0,
            "config.json keys disappeared (renamed or removed): " + string.Join(", ", missing)
            + ". Existing users would lose these settings on upgrade: keep the key or add a migration + decision.");
    }

    [Fact(DisplayName = "a new config.json key must be added to the frozen list on purpose")]
    public void NewKeys_AreAddedToTheFrozenListOnPurpose()
    {
        var current = CurrentKeys();

        var added = current.Except(FrozenKeys, StringComparer.Ordinal).ToArray();

        Assert.True(added.Length == 0,
            "new config.json keys not in ConfigKeyContractTests.FrozenKeys: " + string.Join(", ", added)
            + ". Append them to the list (this is the only place that pins the published file format).");
    }

    [Fact(DisplayName = "the frozen key list has no duplicates")]
    public void FrozenKeys_HaveNoDuplicates() =>
        Assert.Equal(FrozenKeys.Length, FrozenKeys.Distinct(StringComparer.Ordinal).Count());
}
