using PhotoReview.App.Input;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Input;

/// <summary>Middle button: <see cref="MiddleClickAction"/> maps to the existing command the keyboard runs (pure function).</summary>
[Trait("Category", "HotPath")]
public sealed class MiddleClickResolverTests
{
    [Theory]
    [InlineData(MiddleClickAction.ClickZoom, ReviewCommandType.ClickZoom)]
    [InlineData(MiddleClickAction.ActualSize, ReviewCommandType.ZoomActualSize)]
    [InlineData(MiddleClickAction.Fit, ReviewCommandType.ToggleFit)]
    [InlineData(MiddleClickAction.FitWidth, ReviewCommandType.FitWidth)]
    [InlineData(MiddleClickAction.FitWidth2, ReviewCommandType.FitWidth2)]
    [InlineData(MiddleClickAction.PreviousImage, ReviewCommandType.Previous)]
    [InlineData(MiddleClickAction.NextImage, ReviewCommandType.Next)]
    [InlineData(MiddleClickAction.PreviousFolder, ReviewCommandType.PreviousFolder)]
    [InlineData(MiddleClickAction.NextFolder, ReviewCommandType.NextFolder)]
    [InlineData(MiddleClickAction.OpenFolder, ReviewCommandType.OpenFolder)]
    public void Resolve_EachAction_MapsToItsCommand(MiddleClickAction action, ReviewCommandType expected) =>
        Assert.Equal(expected, MiddleClickResolver.Resolve(action)?.Type);

    [Theory]
    [InlineData(MiddleClickAction.None)]
    [InlineData((MiddleClickAction)99)]
    [InlineData((MiddleClickAction)(-1))]
    public void Resolve_NoneAndUndefined_DoNothing(MiddleClickAction action) =>
        Assert.Null(MiddleClickResolver.Resolve(action));

    [Fact]
    public void Resolve_EveryDefinedActionExceptNone_ResolvesToACommand()
    {
        foreach (var action in Enum.GetValues<MiddleClickAction>().Where(a => a != MiddleClickAction.None))
            Assert.NotNull(MiddleClickResolver.Resolve(action));
    }

    [Fact]
    public void DefaultSetting_IsFullsize()
    {
        Assert.Equal(MiddleClickAction.ActualSize, new AppSettings().MiddleClickAction);
        Assert.Equal(ReviewCommandType.ZoomActualSize, MiddleClickResolver.Resolve(new AppSettings().MiddleClickAction)?.Type);
    }
}
