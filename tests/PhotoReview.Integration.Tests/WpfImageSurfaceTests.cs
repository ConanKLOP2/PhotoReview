using System.Windows;
using System.Windows.Controls;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T66 (ViewportSize half): the viewport the fit/zoom math sees is the ScrollViewer's size minus its border, never negative.
/// <c>PointerPosition</c> reads the real mouse (<c>Mouse.GetPosition</c>) and cannot be driven deterministically from a test
/// without moving the user's cursor, so it is not covered here.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WpfImageSurfaceTests
{
    private static async Task<(double Width, double Height)> ViewportOfAsync(double width, double height, Thickness border)
    {
        (double, double) viewport = default;
        await StaTestHost.RunAsync(() =>
        {
            var scroll = new ScrollViewer { Width = width, Height = height, BorderThickness = border };
            scroll.Measure(new Size(width, height));
            scroll.Arrange(new Rect(0, 0, width, height));
            var surface = new WpfImageSurface(scroll, new Image(), new ViewerState(), () => true, () => { });
            viewport = surface.ViewportSize;
            return Task.CompletedTask;
        });
        return viewport;
    }

    [Fact]
    public async Task ViewportSize_SubtractsBorderThickness_OnBothAxes()
    {
        var (width, height) = await ViewportOfAsync(200, 100, new Thickness(left: 3, top: 4, right: 5, bottom: 6));

        Assert.Equal(192, width);
        Assert.Equal(90, height);
    }

    [Fact]
    public async Task ViewportSize_WithoutBorder_EqualsActualSize()
    {
        var (width, height) = await ViewportOfAsync(200, 100, new Thickness(0));

        Assert.Equal(200, width);
        Assert.Equal(100, height);
    }

    [Fact]
    public async Task ViewportSize_BorderLargerThanElement_ClampsToZero()
    {
        var (width, height) = await ViewportOfAsync(10, 10, new Thickness(20));

        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }
}
