using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Services;

[Trait("Category", "HotPath")]
public sealed class ViewportSizeSourceTests
{
    [Fact]
    public void TargetDecodeBox_Default_IsUnbounded()
    {
        Assert.True(new ViewportSizeSource().TargetDecodeBox.IsUnbounded);
    }

    [Theory]
    [InlineData(2304, 1280)]
    [InlineData(4000, 4000)]
    [InlineData(128, 0)]
    [InlineData(0, 1664)]
    public void TargetDecodeBox_PublishesWidthAndHeightTogether(int width, int height)
    {
        var source = new ViewportSizeSource { TargetDecodeBox = new DecodeBox(width, height) };

        Assert.Equal(new DecodeBox(width, height), source.TargetDecodeBox);
    }
}
