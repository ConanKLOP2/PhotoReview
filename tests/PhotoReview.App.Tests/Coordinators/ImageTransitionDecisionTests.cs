using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>feat/image-crossfade: pure decision logic behind the optional image-change fade.</summary>
[Trait("Category", "HotPath")]
public sealed class ImageTransitionDecisionTests
{
    [Fact]
    public void ShouldTransition_DifferentFile_ReturnsTrue()
    {
        var result = ImageTransitionDecision.ShouldTransition(@"C:\photos\a.jpg", @"C:\photos\b.jpg", isCompareVisible: false);

        Assert.True(result);
    }

    [Fact]
    public void ShouldTransition_SameFileUpgrade_ReturnsFalse()
    {
        // thumbnail -> preview -> original within the same navigation: identical path, must stay instant.
        var result = ImageTransitionDecision.ShouldTransition(@"C:\photos\a.jpg", @"C:\photos\a.jpg", isCompareVisible: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldTransition_SameFileDifferentCasing_ReturnsFalse()
    {
        // Windows paths are case-insensitive; a re-cased entry (e.g. metadata refresh) is still the same file.
        var result = ImageTransitionDecision.ShouldTransition(@"C:\photos\A.JPG", @"C:\photos\a.jpg", isCompareVisible: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldTransition_FirstImageAfterFolderOpen_ReturnsFalse()
    {
        // No previously presented file: nothing to fade from.
        var result = ImageTransitionDecision.ShouldTransition(null, @"C:\photos\a.jpg", isCompareVisible: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldTransition_ClearingTheDisplay_ReturnsFalse()
    {
        // newPath is null when the display is being cleared (error/empty state), never a fade target.
        var result = ImageTransitionDecision.ShouldTransition(@"C:\photos\a.jpg", null, isCompareVisible: false);

        Assert.False(result);
    }

    [Fact]
    public void ShouldTransition_CompareVisible_ReturnsFalseEvenForADifferentFile()
    {
        var result = ImageTransitionDecision.ShouldTransition(@"C:\photos\a.jpg", @"C:\photos\b.jpg", isCompareVisible: true);

        Assert.False(result);
    }
}
