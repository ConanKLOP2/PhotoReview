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
/// DF02 — Double-click to Fit gesture tests.
/// These tests verify that double-clicking on the main image applies the Fit operation.
/// All cases expect usage of StaTestHost.WaitForAsync; no Task.Delay.
/// </summary>
[Collection("GlobalState")]
public partial class MainWindowBehaviorTests
{
    /// <summary>
    /// DF02 Case 1: Zoom 200%, double-click main image → Fit converges (Zoom=1, Stretch=Uniform, offset=0).
    /// This is the primary happy path.
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

                var args = CreateLeftDown(MouseButton.Left, 2);

                window.MainImage.RaiseEvent(args);

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
                var args = CreateLeftDown(MouseButton.Left, 2);window.MainImage.RaiseEvent(args);

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
                var args = CreateLeftDown(MouseButton.Left, 1);window.MainImage.RaiseEvent(args);

                // Brief wait
                for (int i = 0; i < 3; i++)
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                // Should still be at Zoom 2.0
                Assert.Equal(2.0, window.ViewModel.Viewer.Zoom);
                Assert.False(window.ViewModel.Viewer.IsFit);
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
                WriteTestImages(fitFolder.Path, 1600, "test.png");
                var hooks = new TestHostHooks { OnPresented = path => presented.Add(path) };
                window = TestAppHost.CreateMainWindow(fitFolder.Path, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                // Zoom to enable pan
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Pan by scroll offset (only possible once the zoomed extent exceeds the viewport)
                Assert.True(
                    await StaTestHost.WaitForAsync(
                        () => { ArrangeHeadless(window); return window.ImageScroll.ScrollableWidth > 100 && window.ImageScroll.ScrollableHeight > 100; },
                        TimeSpan.FromSeconds(5)),
                    $"Zoomed image never became scrollable: {window.ImageScroll.ScrollableWidth}x{window.ImageScroll.ScrollableHeight}");
                window.ImageScroll.ScrollToHorizontalOffset(100);
                window.ImageScroll.ScrollToVerticalOffset(100);
                ArrangeHeadless(window);

                var panOffsetX = window.ImageScroll.HorizontalOffset;
                Assert.True(panOffsetX > 50, "Pan offset not applied");

                // Double-click while panned
                var args = CreateLeftDown(MouseButton.Left, 2);window.MainImage.RaiseEvent(args);

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
                var args = CreateLeftDown(MouseButton.Right, 2);window.MainImage.RaiseEvent(args);

                // Brief wait
                for (int i = 0; i < 3; i++)
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                }

                // Should not fit
                Assert.False(window.ViewModel.Viewer.IsFit, "Right-click should not trigger Fit");
            });
        }
        finally
        {
            if (window is not null)
                await CloseAsync(window);
        }
    }

    /// <summary>
    /// Builds a press event whose ClickCount really is <paramref name="clickCount"/>. WPF exposes ClickCount with
    /// an internal setter, so it is set through reflection; the helper asserts the value stuck, otherwise the
    /// double-click cases would pass vacuously (a single click would be raised instead).
    /// </summary>
    private static MouseButtonEventArgs CreateLeftDown(MouseButton button, int clickCount)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        };
        var setter = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
            .GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("MouseButtonEventArgs.ClickCount has no setter in this WPF version.");
        setter.Invoke(args, new object[] { clickCount });
        Assert.Equal(clickCount, args.ClickCount);
        return args;
    }

    /// <summary>The test window is never shown, so give its content a real 1200x800 viewport.</summary>
    private static void ArrangeHeadless(MainWindow window)
    {
        var layoutRoot = (FrameworkElement)window.Content;
        var size = new Size(1200, 800);
        layoutRoot.Measure(size);
        layoutRoot.Arrange(new Rect(size));
        layoutRoot.UpdateLayout();
    }

    private static void WriteTestImages(string folder, params string[] names)
        => WriteTestImages(folder, 16, names);

    private static void WriteTestImages(string folder, int size, params string[] names)
    {
        int stride = size * 4;
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
