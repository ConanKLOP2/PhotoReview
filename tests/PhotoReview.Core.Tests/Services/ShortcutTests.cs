using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Cross-scope shortcut conflict validation.</summary>
[Trait("Category", "HotPath")]
public sealed class ShortcutTests
{
    [Fact(DisplayName = "Shortcut validator reports cross-scope conflicts")]
    public void ShortcutValidatorReportsCrossScopeConflicts()
    {
        var conflictingSettings = new AppSettings();
        conflictingSettings.Actions[0].Shortcut = conflictingSettings.Shortcuts.Next;
        // AR11a: AppSettings.ValidateShortcuts (static) is gone; SettingsStore.ValidateShortcuts is the instance
        // replacement (KeyNames defaults to the same permissive fallback the old static Validator used unassigned).
        var store = new SettingsStore(new AppPaths(@"C:\Users\test\AppData\Local"), new PhotoReview.Core.IO.PhysicalFileSystem(), NullLog.Instance);
        Assert.True(store.ValidateShortcuts(conflictingSettings)?
            .Contains("bị dùng trùng", StringComparison.OrdinalIgnoreCase) == true);
    }
}

