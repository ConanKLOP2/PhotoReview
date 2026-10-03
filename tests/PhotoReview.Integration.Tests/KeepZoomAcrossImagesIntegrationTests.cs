using System.IO;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R36 (#178): <see cref="PhotoReview.Core.Settings.AppSettings.KeepZoomAcrossImages"/> on the real
/// <see cref="MainWindow"/>. <c>ViewerStateTests.ApplyInitialViewMode_KeepZoomAcrossImages_IsANoOpAndReturnsFalse</c>
/// (PhotoReview.App.Tests) already proves the pure no-op at the <c>ViewerState</c> level; this file proves the
/// setting actually reaches a real photo-to-photo navigation end to end: the zoom set on image A survives
/// navigating to image B when the setting is on, and resets to Fit (the default <c>InitialViewMode</c>) when off.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class KeepZoomAcrossImagesIntegrationTests
{
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(10);

    private static async Task WithTwoImageWindowAsync(bool keepZoomAcrossImages, Func<MainWindow, List<string>, Task> body)
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("keep-zoom-across-images");
        WriteImage(Path.Combine(folder.Path, "a.png"));
        WriteImage(Path.Combine(folder.Path, "b.png"));
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(folder.Path, new TestHostHooks { OnPresented = presented.Add });
                window.Settings.KeepZoomAcrossImages = keepZoomAcrossImages;
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout), "First image never presented");
                await body(window, presented);
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }
    }

    [Fact]
    public async Task Enabled_PreservesZoomAndLeavesFit_AfterNavigatingToTheNextImage()
    {
        await WithTwoImageWindowAsync(keepZoomAcrossImages: true, async (window, presented) =>
        {
            window.ViewModel.Viewer.SetZoom(2.0);
            Assert.False(window.ViewModel.Viewer.IsFit);

            // Fire-and-forget, then poll with a bound: never await the navigation task directly here -- if it
            // were ever to hang (a real bug, or extreme contention from other processes on a shared machine),
            // an unbounded await would block this test (and the whole run) forever instead of failing cleanly.
            _ = window.ViewModel.NextAsync();
            Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 1, PresentTimeout), "Second image never presented");

            Assert.False(window.ViewModel.Viewer.IsFit, "KeepZoomAcrossImages=true must not reset the view on navigation.");
            Assert.Equal(2.0, window.ViewModel.Viewer.Zoom, 6);
        });
    }

    [Fact]
    public async Task Disabled_ResetsToFit_AfterNavigatingToTheNextImage()
    {
        await WithTwoImageWindowAsync(keepZoomAcrossImages: false, async (window, presented) =>
        {
            window.ViewModel.Viewer.SetZoom(2.0);
            Assert.False(window.ViewModel.Viewer.IsFit);

            _ = window.ViewModel.NextAsync(); // fire-and-forget + bounded poll below, see the other test for why
            Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 1, PresentTimeout), "Second image never presented");

            Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.Viewer.IsFit, TimeSpan.FromSeconds(5)),
                "KeepZoomAcrossImages=false must reset to Fit (the default InitialViewMode) on navigation.");
        });
    }

    private static void WriteImage(string path)
    {
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
