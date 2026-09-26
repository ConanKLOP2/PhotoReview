using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Tests.Localization;

// The "hash could not be computed" compare fragment follows the ambient language -> GlobalState.
[Collection("GlobalState")]
public sealed class CompareHashUnknownTextTests : IDisposable
{
    public void Dispose() => TestLocalization.UseVietnamese();

    [Fact]
    public void CompareHashUnknown_FollowsTheCurrentLanguage()
    {
        TestLocalization.UseVietnamese();
        Assert.Equal(" | hash không xác định", StatusFormatter.CompareHashUnknown());

        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal(" | hash unknown", StatusFormatter.CompareHashUnknown());
        }
    }
}
