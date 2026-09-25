using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.TestSupport;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Default actions: names come from the catalog (language at creation time), folders are language-neutral.</summary>
[Collection("GlobalState")]
public sealed class ReviewActionDefaultsTests
{
    [Fact(DisplayName = "Default action names follow the UI language and the destination folders are language-neutral")]
    public void Defaults_NamesAreLocalized_FoldersAreNeutral()
    {
        List<ReviewAction> english, vietnamese;
        using (TestLocalization.Use(TestLocalization.English)) english = ReviewAction.Defaults();
        using (TestLocalization.Use(TestLocalization.Vietnamese)) vietnamese = ReviewAction.Defaults();

        Assert.Equal(["Action 2", "Action 3", "Action 4", "Backup"], english.Select(a => a.Name));
        Assert.Equal(["Loại 2", "Loại 3", "Loại 4", "Sao lưu"], vietnamese.Select(a => a.Name));
        Assert.Equal(["Group-2", "Group-3", "Group-4", "Backup"], english.Select(a => a.Destination));
        Assert.Equal(english.Select(a => a.Destination), vietnamese.Select(a => a.Destination));
        Assert.Equal([FileOperationType.Move, FileOperationType.Move, FileOperationType.Move, FileOperationType.Copy], english.Select(a => a.Operation));
    }
}
