using System.Windows;
using PhotoReview.App.Coordinators;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// <see cref="FullscreenWindowPlacer"/> on a window that has no OS handle yet (never shown): there is nothing to flip with
/// SetWindowPos, so Enter/Exit fall back to the WPF properties. The handle-based path is covered by the Integration tests.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class FullscreenWindowPlacerUnshownTests
{
    [Theory]
    [InlineData(WindowState.Normal, WindowState.Normal)]
    [InlineData(WindowState.Maximized, WindowState.Maximized)]
    [InlineData(WindowState.Minimized, WindowState.Normal)]
    public void Enter_WithoutAHandle_GoesBorderlessMaximized_AndRemembersTheStateBefore(WindowState before, WindowState remembered)
    {
        StaUi.Run(() =>
        {
            var window = new Window { WindowState = before };
            var placer = new FullscreenWindowPlacer();

            placer.Enter(window);

            Assert.Equal(remembered, placer.StateBefore);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Null(placer.NormalBounds); // no OS window, so no restore rect was saved
        });
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void Exit_WithoutAHandle_RestoresTheChromeAndTheSavedState(WindowState before)
    {
        StaUi.Run(() =>
        {
            var window = new Window { WindowState = before };
            var placer = new FullscreenWindowPlacer();
            placer.Enter(window);

            placer.Exit(window);

            Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.Equal(before, window.WindowState);
        });
    }
}
