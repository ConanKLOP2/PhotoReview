using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R37 (#181): crossfade on a real photo-to-photo navigation in the real <see cref="MainWindow"/>.
/// <see cref="MainWindow.OutgoingImage"/> is the fade layer (see the <c>StartImageFade</c>/<c>OnImageFadeCompleted</c>
/// region of MainWindow.xaml.cs); <see cref="PhotoReview.App.Coordinators.ImageTransitionDecisionTests"/> already
/// proves the pure decision predicate (different file vs. same-file upgrade vs. Compare open), so this file only
/// proves the WIRING: the layer actually becomes visible and fades when <see cref="ImageTransition.Fade"/> is set,
/// releases itself when done, and never lights up for <see cref="ImageTransition.None"/> or a same-file zoom change
/// (no navigation).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ImageCrossfadeIntegrationTests
{
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FadeTimeout = TimeSpan.FromSeconds(3);
    private static readonly Size WindowContent = new(400, 300);

    /// <summary>A two-image folder, real MainWindow, <paramref name="transition"/> applied before the first present.
    /// Laid out at <see cref="WindowContent"/> so <c>MainImage.ActualWidth/Height</c> are non-zero -- StartImageFade
    /// (MainWindow.xaml.cs) returns early otherwise, same requirement as MainWindowZoomDetailTests.</summary>
    private static async Task WithTwoImageWindowAsync(ImageTransition transition, Func<MainWindow, List<string>, Task> body)
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("image-crossfade");
        WriteImage(Path.Combine(folder.Path, "a.png"));
        WriteImage(Path.Combine(folder.Path, "b.png"));
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(folder.Path, new TestHostHooks { OnPresented = presented.Add });
                window.Settings.ImageTransition = transition;
                window.Settings.ImageTransitionMs = AppSettings.MinImageTransitionMs; // fastest allowed, keeps the test quick
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout), "First image never presented");
                await LayoutAsync((FrameworkElement)window.Content);
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
    public async Task NavigatingToADifferentFile_WithFadeEnabled_AnimatesTheOutgoingLayerThenReleasesIt()
    {
        await WithTwoImageWindowAsync(ImageTransition.Fade, async (window, presented) =>
        {
            Assert.Equal(Visibility.Collapsed, window.OutgoingImage.Visibility); // nothing to fade from yet

            var outgoingBitmap = window.MainImage.Source;
            Assert.NotNull(outgoingBitmap);

            // CI-CROSSFADE-ANIMATION-FLAKE (see docs/refactoring/decisions/): a point-in-time poll for
            // "Opacity < 1.0" (StaTestHost.WaitForAsync: 10ms Task.Delay + a Background-priority dispatch)
            // can permanently miss the whole animation on a loaded GitHub Actions windows-latest runner --
            // the fade genuinely starts and fully completes (Opacity reverted to its pre-fade base value of
            // 1.0 by OnImageFadeCompleted once the clock is removed) inside a single gap of that
            // Background-priority poll, however long AppSettings.ImageTransitionMs is; the CI evidence for
            // this file shows the compositor itself ticking fine (CompositionTarget.Rendering ~67 Hz,
            // RenderCapability.Tier=0x00020000). Like the other two crossfade tests' VisibilityWatch below,
            // a value-changed watcher catches ANY transient dip below 1.0, however briefly, so it cannot
            // miss a fade that genuinely ran -- it is driven by the same per-frame property-changed
            // notification that updates the visual, not by this test's own poll cadence.
            using var opacityWatch = WatchForOpacityDrop(window.OutgoingImage);
            // Same reasoning for the layer's visibility: the whole fade can start and finish between two polls, so a
            // point-in-time 'is it Visible right now' check is racy on a loaded runner. Record that it became Visible and
            // what it showed at that moment from the value-changed notification instead.
            using var visibilityWatch = WatchForTransientVisible(window.OutgoingImage);

            // Fire-and-forget, then poll with a bound: never await the navigation task directly -- if it were
            // ever to hang (a real bug, or extreme contention from other processes on a shared machine), an
            // unbounded await would block this test (and the whole run) forever instead of failing cleanly.
            _ = window.ViewModel.NextAsync();
            Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 1, PresentTimeout), "Second image never presented");

            Assert.True(visibilityWatch.EverVisible(), "The outgoing layer never became visible.");
            Assert.Same(outgoingBitmap, visibilityWatch.SourceWhenFirstVisible); // froze the OUTGOING frame, not the new one
            Assert.True(await StaTestHost.WaitForAsync(() => window.OutgoingImage.Visibility == Visibility.Collapsed, FadeTimeout), "The fade never completed (OnImageFadeCompleted).");
            Assert.True(opacityWatch.EverDropped, "The fade animation never started.");
            Assert.Null(window.OutgoingImage.Source); // released once the fade completes
        });
    }

    [Fact]
    public async Task NavigatingToADifferentFile_WithTransitionNone_NeverShowsTheOutgoingLayer()
    {
        await WithTwoImageWindowAsync(ImageTransition.None, async (window, presented) =>
        {
            // A value-changed watcher (not a post-hoc snapshot) so a fast, buggy fade that starts AND completes
            // before this test gets to check can't slip through the "give it 150ms then look" race.
            using var watch = WatchForTransientVisible(window.OutgoingImage);

            _ = window.ViewModel.NextAsync(); // fire-and-forget + bounded poll below, see the other test for why
            Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 1, PresentTimeout), "Second image never presented");
            await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(150)); // let any (incorrect) animation run its course

            Assert.False(watch.EverVisible(), "The outgoing layer became visible even though ImageTransition is None.");
            Assert.Equal(Visibility.Collapsed, window.OutgoingImage.Visibility);
            Assert.Null(window.OutgoingImage.Source);
        });
    }

    [Fact]
    public async Task ZoomingTheSameImage_WithFadeEnabled_NeverStartsAFade()
    {
        await WithTwoImageWindowAsync(ImageTransition.Fade, async (window, _) =>
        {
            using var watch = WatchForTransientVisible(window.OutgoingImage);

            window.ViewModel.Viewer.SetZoom(2.0); // no navigation -- same file, just a zoom change

            await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(150));
            Assert.False(watch.EverVisible(), "The outgoing layer became visible for a same-file zoom change.");
            Assert.Equal(Visibility.Collapsed, window.OutgoingImage.Visibility);
        });
    }

    /// <summary>Watches <paramref name="image"/>'s Visibility for any transient trip through Visible, however
    /// briefly -- a race-proof alternative to "wait a while, then check the final value" for a fade fast
    /// enough to start and fully complete (reverting Visibility to Collapsed) inside the drain window.</summary>
    private static VisibilityWatch WatchForTransientVisible(Image image) => new(image);

    /// <summary>Watches <paramref name="image"/>'s Opacity for any transient dip below 1.0, however briefly --
    /// see CI-CROSSFADE-ANIMATION-FLAKE at the call site for why a point-in-time poll for this cannot be
    /// trusted on a loaded CI runner.</summary>
    private static OpacityWatch WatchForOpacityDrop(Image image) => new(image);

    private sealed class OpacityWatch : IDisposable
    {
        private static readonly DependencyPropertyDescriptor Descriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Image));
        private readonly Image _image;
        private readonly EventHandler _handler;
        private bool _everDropped;

        public OpacityWatch(Image image)
        {
            _image = image;
            _everDropped = image.Opacity < 1.0;
            _handler = (_, _) => { if (_image.Opacity < 1.0) _everDropped = true; };
            Descriptor.AddValueChanged(_image, _handler);
        }

        public bool EverDropped => _everDropped;

        public void Dispose() => Descriptor.RemoveValueChanged(_image, _handler);
    }

    private sealed class VisibilityWatch : IDisposable
    {
        private static readonly DependencyPropertyDescriptor Descriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Image));
        private readonly Image _image;
        private readonly EventHandler _handler;
        private bool _everVisible;
        private System.Windows.Media.ImageSource? _sourceWhenFirstVisible;

        public VisibilityWatch(Image image)
        {
            _image = image;
            _everVisible = image.Visibility == Visibility.Visible;
            _sourceWhenFirstVisible = _everVisible ? image.Source : null;
            _handler = (_, _) =>
            {
                if (_image.Visibility != Visibility.Visible || _everVisible) return;
                _everVisible = true;
                _sourceWhenFirstVisible = _image.Source;
            };
            Descriptor.AddValueChanged(_image, _handler);
        }

        public bool EverVisible() => _everVisible;

        public System.Windows.Media.ImageSource? SourceWhenFirstVisible => _sourceWhenFirstVisible;

        public void Dispose() => Descriptor.RemoveValueChanged(_image, _handler);
    }

    private static async Task LayoutAsync(FrameworkElement layoutRoot)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            layoutRoot.Measure(WindowContent);
            layoutRoot.Arrange(new Rect(WindowContent));
            layoutRoot.UpdateLayout();
            await StaTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
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
