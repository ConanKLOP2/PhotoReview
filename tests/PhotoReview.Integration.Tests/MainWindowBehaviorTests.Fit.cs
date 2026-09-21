using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// DF02 — Double-click to Fit gesture tests.
/// These tests verify that double-clicking on the main image applies the Fit operation.
/// All cases expect usage of StaTestHost.WaitForAsync; no Task.Delay.
/// </summary>
[Collection("STA Test Collection")]
public partial class MainWindowBehaviorTests
{
    private const string FitTestFolder = @"C:\temp\photoreview-fit-test";

    static MainWindowBehaviorTests()
    {
        // Cleanup old folder
        if (Directory.Exists(FitTestFolder))
        {
            try { Directory.Delete(FitTestFolder, true); } catch { }
        }
        Directory.CreateDirectory(FitTestFolder);
    }

    /// <summary>
    /// DF02 Case 1: Zoom 200%, double-click main image → Fit converges (Zoom=1, Stretch=Uniform, offset=0).
    /// This is the primary happy path; must FAIL on baseline (no double-click handler yet).
    /// </summary>
    [Fact]
    public async Task DoubleClickFit_From200Percent_ConvergesTo1x()
    {
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(FitTestFolder, "square.png");
                var hooks = new MainWindowTestHooks
                {
                    OnPresented = path => presented.Add(path),
                };
                window = new MainWindow(FitTestFolder, hooks);

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

                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                // Try to set ClickCount=2 via reflection for testing
                var clickCountField = typeof(MouseButtonEventArgs).GetField("_clickCount",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (clickCountField != null)
                {
                    clickCountField.SetValue(args, 2);
                }

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
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(FitTestFolder, "test.png");
                var hooks = new MainWindowTestHooks { OnPresented = path => presented.Add(path) };
                window = new MainWindow(FitTestFolder, hooks);

                Assert.True(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0 && window.ViewModel.Viewer.IsFit,
                        TimeSpan.FromSeconds(10)),
                    "Image not presented in Fit mode");

                var beforeZoom = window.ViewModel.Viewer.Zoom;
                var beforeStretch = window.ViewModel.Viewer.Stretch;
                var beforeWidth = window.MainImage.ActualWidth;
                var beforeHeight = window.MainImage.ActualHeight;

                // Double-click
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                var clickCountField = typeof(MouseButtonEventArgs).GetField("_clickCount",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (clickCountField != null) clickCountField.SetValue(args, 2);

                window.MainImage.RaiseEvent(args);

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
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(FitTestFolder, "test.png");
                var hooks = new MainWindowTestHooks { OnPresented = path => presented.Add(path) };
                window = new MainWindow(FitTestFolder, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                // Zoom
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Single-click (ClickCount=1)
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                var clickCountField = typeof(MouseButtonEventArgs).GetField("_clickCount",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (clickCountField != null) clickCountField.SetValue(args, 1);

                window.MainImage.RaiseEvent(args);

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
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(FitTestFolder, "test.png");
                var hooks = new MainWindowTestHooks { OnPresented = path => presented.Add(path) };
                window = new MainWindow(FitTestFolder, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                // Zoom to enable pan
                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Pan by scroll offset
                window.ImageScroll.ScrollToHorizontalOffset(100);
                window.ImageScroll.ScrollToVerticalOffset(100);

                var panOffsetX = window.ImageScroll.HorizontalOffset;
                Assert.True(panOffsetX > 50, "Pan offset not applied");

                // Double-click while panned
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                var clickCountField = typeof(MouseButtonEventArgs).GetField("_clickCount",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (clickCountField != null) clickCountField.SetValue(args, 2);

                window.MainImage.RaiseEvent(args);

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
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                WriteTestImages(FitTestFolder, "test.png");
                var hooks = new MainWindowTestHooks { OnPresented = path => presented.Add(path) };
                window = new MainWindow(FitTestFolder, hooks);

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)));

                window.ViewModel.Viewer.SetZoom(2.0);
                window.MainImage.UpdateLayout();
                await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Right-click (RightButton, ClickCount=2)
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                var clickCountField = typeof(MouseButtonEventArgs).GetField("_clickCount",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (clickCountField != null) clickCountField.SetValue(args, 2);

                window.MainImage.RaiseEvent(args);

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

    private static void WriteTestImages(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            // Create empty placeholder file for test; production will skip or load with placeholder
            File.WriteAllBytes(path, []);
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
