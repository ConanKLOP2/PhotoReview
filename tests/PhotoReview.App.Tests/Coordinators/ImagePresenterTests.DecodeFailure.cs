using System.IO;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed partial class ImagePresenterTests
{
    [Fact(DisplayName = "A decode failure of a DIFFERENT entry clears the previous photo, so Delete/Move cannot act on an unseen file")]
    public async Task PresentAsync_WhenDifferentEntryFailsToDecode_ClearsPreviousImage()
    {
        var good = CreateFakeImageFile("good.png");
        var bad = Path.Combine(_tempDir, "bad.png");
        File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8]);
        _catalog.Reset([good, bad]);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0);
        Assert.NotNull(presenter.CurrentImage);

        await presenter.PresentAsync(1);

        Assert.Null(presenter.CurrentImage);
        Assert.Null(_sink.CurrentImage);
        Assert.Equal([good, bad], _catalog.Paths);
        Assert.Contains("bad.png", presenter.StatusText, System.StringComparison.Ordinal);
    }
}
