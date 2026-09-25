using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// DF02 - Double-click to Fit gesture tests.
/// These tests verify that double-clicking on the main image applies the Fit operation.
/// All cases expect usage of StaTestHost.WaitForAsync; no Task.Delay.
/// </summary>
[Collection("GlobalState")]
public partial class MainWindowBehaviorTests
{
    /// <summary>
    /// DF02 Case 1: Zoom 200%, double-click main image → Fit converges (Zoom=1, Stretch=Uniform, offset=0).
    /// This is the primary happy path; 
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_From200Percent_ConvergesTo1x()
    {
        using var dataRoot = new DataRootFixture();
        using var fitFolder = new TempRoot("fit");
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(fitFolder.Path, "square.png");
                var hooks = new TestHostHooks
                {
                    OnPresented = path => presented.Add(path),
                };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                // Wait for image load
                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)),
                    "Image never presented");

                // Zoom to 200%
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                Assert.Equal(2.0, window.ViewModel.Viewer.Zoom);
                Assert.False(window.ViewModel.Viewer.IsFit);

                // Simulate double-click
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var source = PresentationSource.FromVisual(window)
                    ?? HwndSource.FromHwnd(handle)
                    ?? throw new InvalidOperationException("No PresentationSource");

                RaiseMainImagePress(window, MouseButton.Left, 2);

                // Wait for Fit to converge
                var fitConverged = await StaTestHost.WaitForAsync(
                    () => window.ViewModel.Viewer.IsFit &&
                          Math.Abs(window.ImageScroll.HorizontalOffset) < 0.5 &&
                          Math.Abs(window.ImageScroll.VerticalOffset) < 0.5,
                    TimeSpan.FromSeconds(5));

                Assert.True(fitConverged,
                    $"Fit did not converge. IsFit={window.ViewModel.Viewer.IsFit}, " +
                    $"Offset=({window.ImageScroll.HorizontalOffset:F1}, {window.ImageScroll.VerticalOffset:F1})");

                Assert.Equal(1.0, window.ViewModel.Viewer.Zoom);
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    /// <summary>
    /// DF02 Case 2: Already in Fit mode, double-click → no visible change (state and dimensions unchanged).
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_AlreadyFit_NoChange()
    {
        using var dataRoot = new DataRootFixture();
        using var fitFolder = new TempRoot("fit");
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(fitFolder.Path, "test.png");
                var hooks = new TestHostHooks { OnPresented = path => presented.Add(path) };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0 && window.ViewModel.Viewer.IsFit,
                        TimeSpan.FromSeconds(10)),
                    "Image not presented in Fit mode");

                var beforeZoom = window.ViewModel.Viewer.Zoom;
                var beforeStretch = window.ViewModel.Viewer.Stretch;
                var beforeWidth = window.MainImage.ActualWidth;
                var beforeHeight = window.MainImage.ActualHeight;

                // Double-click
                RaiseMainImagePress(window, MouseButton.Left, 2);

                // Brief pump to let any transaction complete
                for (int i = 0; i < 5; i++)
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                // Verify no change
                Assert.Equal(beforeZoom, window.ViewModel.Viewer.Zoom);
                Assert.Equal(beforeStretch, window.ViewModel.Viewer.Stretch);
                Assert.True(Math.Abs(window.MainImage.ActualWidth - beforeWidth) < 0.5, "Image width changed");
                Assert.True(Math.Abs(window.MainImage.ActualHeight - beforeHeight) < 0.5, "Image height changed");

                // Positive control: the same gesture on the same window DOES fit once zoomed, so "no change" above is meaningful.
                await AssertDoubleClickFitsFrom200Async(window);
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    /// <summary>
    /// DF02 Case 3: Single-click at zoom 200% → does NOT apply Fit (Zoom remains 2.0).
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_SingleClick_DoesNotFit()
    {
        using var dataRoot = new DataRootFixture();
        using var fitFolder = new TempRoot("fit");
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(fitFolder.Path, "test.png");
                var hooks = new TestHostHooks { OnPresented = path => presented.Add(path) };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                // Zoom
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Single-click (ClickCount=1)
                RaiseMainImagePress(window, MouseButton.Left, 1);

                // Brief wait
                for (int i = 0; i < 3; i++)
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                // Should still be at Zoom 2.0
                Assert.Equal(2.0, window.ViewModel.Viewer.Zoom);
                Assert.False(window.ViewModel.Viewer.IsFit);

                // Positive control: a real double-click from this exact state fits.
                RaiseMainImagePress(window, MouseButton.Left, 2);
                Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.Viewer.IsFit, TimeSpan.FromSeconds(5)),
                    "Positive control failed: a left double-click did not fit");
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    /// <summary>
    /// DF02 Case 4: Drag pan, then double-click while panned → does NOT reset to Fit (pan offset preserved or returns to Fit depending on implementation).
    /// Expected: double-click should still apply Fit (version cancels pending pan).
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_AfterPan_StillFits()
    {
        using var dataRoot = new DataRootFixture();
        using var fitFolder = new TempRoot("fit");
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(fitFolder.Path, 2000, "test.png"); // large enough that 200% overflows the viewport
                var hooks = new TestHostHooks { OnPresented = path => presented.Add(path) };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                // Zoom to enable pan
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Pan by scroll offset
                // The window is never shown: lay it out at an explicit size so the scroll extent exists.
                var layoutRoot = (FrameworkElement)window.Content;
                var contentSize = new Size(1200, 800);
                void LayOut()
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        layoutRoot.Measure(contentSize);
                        layoutRoot.Arrange(new Rect(contentSize));
                        layoutRoot.UpdateLayout();
                    }
                }

                LayOut();
                window.ImageScroll.ScrollToHorizontalOffset(100);
                window.ImageScroll.ScrollToVerticalOffset(100);
                LayOut();

                var panOffsetX = window.ImageScroll.HorizontalOffset;
                Assert.True(panOffsetX > 50, $"Pan offset not applied: extent={window.ImageScroll.ExtentWidth} viewport={window.ImageScroll.ViewportWidth} zoom={window.ViewModel.Viewer.Zoom} image={window.MainImage.ActualWidth}");

                // Double-click while panned
                RaiseMainImagePress(window, MouseButton.Left, 2);

                // Wait for Fit
                var fitConverged = await StaTestHost.WaitForAsync(
                    () => window.ViewModel.Viewer.IsFit &&
                          Math.Abs(window.ImageScroll.HorizontalOffset) < 0.5,
                    TimeSpan.FromSeconds(5));

                Assert.True(fitConverged, "Fit did not converge after pan and double-click");
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    /// <summary>
    /// DF02 Case 6: Right-click or middle-click with ClickCount=2 → does NOT apply Fit (left button only).
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_RightButton_DoesNotFit()
    {
        using var dataRoot = new DataRootFixture();
        using var fitFolder = new TempRoot("fit");
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(fitFolder.Path, "test.png");
                var hooks = new TestHostHooks { OnPresented = path => presented.Add(path) };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Right-click (RightButton, ClickCount=2)
                RaiseMainImagePress(window, MouseButton.Right, 2);

                // Brief wait
                for (int i = 0; i < 3; i++)
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                // Should not fit
                Assert.False(window.ViewModel.Viewer.IsFit, "Right-click should not trigger Fit");
                Assert.Equal(2.0, window.ViewModel.Viewer.Zoom);

                // Positive control: a left double-click from this exact state fits.
                RaiseMainImagePress(window, MouseButton.Left, 2);
                Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.Viewer.IsFit, TimeSpan.FromSeconds(5)),
                    "Positive control failed: a left double-click did not fit");
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    private static async Task AssertDoubleClickFitsFrom200Async(MainWindow window)
    {
        window.ViewModel.Viewer.SetZoom(2.0);
        window.MainImage.UpdateLayout();
        await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Assert.False(window.ViewModel.Viewer.IsFit);

        RaiseMainImagePress(window, MouseButton.Left, 2);

        Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.Viewer.IsFit, TimeSpan.FromSeconds(5)),
            "Positive control failed: a left double-click did not fit");
    }

    /// <summary>
    /// Raises a preview left-button-down on the main image with the given changed button and click count. The click
    /// count is a real property with an internal setter (there is no public way to synthesise a multi-click), so a
    /// failure to set it must fail the test loudly instead of silently degrading to a single click.
    /// </summary>
    private static void RaiseMainImagePress(MainWindow window, MouseButton button, int clickCount)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        };
        var property = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!;
        property.SetValue(args, clickCount);
        Assert.Equal(clickCount, args.ClickCount);
        window.MainImage.RaiseEvent(args);
    }

    private static void WriteTestImages(string folder, params string[] names) => WriteTestImages(folder, 16, names);

    private static void WriteTestImages(string folder, int size, params string[] names)
    {
        var stride = size * 4;
        var pixels = new byte[stride * size];
        Array.Fill(pixels, (byte)0x90);
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.OpenWrite(path))
            {
                encoder.Save(file);
            }
        }
    }

    private static async Task CloseAsync(MainWindow window)
    {
        await StaTestHost.RunAsync(async () =>
        {
            try { window.Close(); } catch { }
        });
    }
}
