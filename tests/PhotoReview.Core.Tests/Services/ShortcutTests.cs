using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

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
        Assert.True(AppSettings.ValidateShortcuts(conflictingSettings)?
            .Contains("bị dùng trùng", StringComparison.OrdinalIgnoreCase) == true);
    }
}

