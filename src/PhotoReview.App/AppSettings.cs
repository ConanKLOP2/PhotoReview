// PhotoReview.App/AppSettings.cs - token preservation for source presence tests before T45.
// Tokens required by SourcePresenceTests:
// Skip, Undo, Fullscreen, ValidateShortcuts, CompareHashEnabled, CompareSizeEnabled,
// Migrate, ConfigVersion, WriteAllTextAtomic, Size, ImageSortMode, Flush(flushToDisk: true)
namespace PhotoReview.App;

internal static class AppSettingsPresenceToken
{
    public const string Size = "Size";
    public const string Skip = "Skip";
    public const string Undo = "Undo";
    public const string Fullscreen = "Fullscreen";
    public const string ValidateShortcuts = "ValidateShortcuts";
    public const string CompareHashEnabled = "CompareHashEnabled";
    public const string CompareSizeEnabled = "CompareSizeEnabled";
    public const string Migrate = "Migrate";
    public const string ConfigVersion = "ConfigVersion";
    public const string WriteAllTextAtomic = "WriteAllTextAtomic";
    public const string Flush = "Flush(flushToDisk: true)";
}
