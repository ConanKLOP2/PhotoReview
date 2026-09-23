using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// feat(zoom) (option A for #43) on the real <see cref="MainWindow"/>: leaving Fit sizes the image
/// from the ORIGINAL pixels (100 % = 1 source px per device px) while the preview is still shown,
/// then swaps in the on-demand full-resolution decode without changing the element size, the
/// scroll extent or the scroll position.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Slow")]
public sealed class MainWindowZoomDetailTests(ITestOutputHelper output)
{
    private const string DisableDiskCacheVariable = "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE";
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(20);
    private static readonly Size WindowContent = new(1200, 800);

    [Fact]
    public async Task ZoomTo100_SwapsPreviewForOriginal_WithoutLayoutOrScrollChange()
    {
        using var root = new TempRoot("zoom-detail");
        using var dataRoot = new DataRootFixture();
        using var noDiskCache = new EnvironmentScope(DisableDiskCacheVariable, "1"); // never write the user's real cache dir
        var path = WriteJpeg(Path.Combine(root.Dir("images"), "big.jpg"), 4000, 3000);
        MainWindow? window = null;
        var presented = new List<string>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = OpenWindow(path, presented.Add, DecoderBackend.Wpf);
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout), "The image was never presented.");
                var layoutRoot = (FrameworkElement)window.Content;
                await LayoutAsync(layoutRoot);

                var viewer = window.ViewModel.Viewer;
                var zoomDetail = window.ViewModel.Presenter.ZoomDetail;
                var preview = Assert.IsAssignableFrom<BitmapSource>(window.MainImage.Source);
                Assert.True(preview.PixelWidth < 4000, $"Expected a downscaled preview, got {preview.PixelWidth}px.");

                window.SetZoom(1.0);
                await LayoutAsync(layoutRoot);

                // Preview still shown, already at the original's size (not the preview's).
                var expectedWidth = 4000 / viewer.DpiScale;
                var expectedHeight = 3000 / viewer.DpiScale;
                Assert.Equal(expectedWidth, window.MainImage.ActualWidth, 1);
                Assert.Equal(expectedHeight, window.MainImage.ActualHeight, 1);

                window.ImageScroll.ScrollToHorizontalOffset(700);
                window.ImageScroll.ScrollToVerticalOffset(500);
                await LayoutAsync(layoutRoot);
                var before = Snapshot(window);
                Assert.True(before.HorizontalOffset > 0 && before.VerticalOffset > 0, $"Scroll offsets were not applied: {before}");

                Assert.True(await StaTestHost.WaitForAsync(() => zoomDetail.IsShowingOriginal, PresentTimeout), "The original never replaced the preview.");
                await LayoutAsync(layoutRoot);

                var original = Assert.IsAssignableFrom<BitmapSource>(window.MainImage.Source);
                Assert.Equal(4000, original.PixelWidth);
                Assert.Equal(3000, original.PixelHeight);
                Assert.Equal(before, Snapshot(window));

                // Back to Fit: the preview returns (the original is only kept for a later zoom).
                window.ViewModel.Viewer.ResetFit(WindowContent.Width, WindowContent.Height);
                await LayoutAsync(layoutRoot);
                Assert.Same(preview, window.MainImage.Source);
            });
        }
        finally
        {
            if (window is not null) await StaTestHost.RunAsync(() => { window.Close(); return Task.CompletedTask; });
        }
    }

    /// <summary>
    /// Measurement for the PR (not a gate): time from the zoom command to the sharp 24 MP image on
    /// screen, per navigation, on a synthetic 6000x4000 JPEG with the default decoder backend.
    /// </summary>
    [Theory]
    [Trait("Category", "Manual")]
    [InlineData(DecoderBackend.Wpf)]
    [InlineData(DecoderBackend.WicDirect)]
    [InlineData(DecoderBackend.TurboJpeg)]
    public async Task Measure_ZoomCommandToSharpImage_24MP(DecoderBackend backend)
    {
        using var root = new TempRoot("zoom-detail-measure");
        using var dataRoot = new DataRootFixture();
        using var noDiskCache = new EnvironmentScope(DisableDiskCacheVariable, "1");
        var folder = root.Dir("images");
        var paths = MeasureNames.Select(n => WriteJpeg(Path.Combine(folder, n), 6000, 4000, noise: true)).ToArray();
        output.WriteLine($"{backend}: synthetic 6000x4000 JPEG, {new FileInfo(paths[0]).Length / 1024.0 / 1024.0:0.0} MB");
        MainWindow? window = null;
        var presented = new List<string>();
        var samples = new List<double>();

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = OpenWindow(paths[0], presented.Add, backend);
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout));
                var zoomDetail = window.ViewModel.Presenter.ZoomDetail;
                var shownAt = 0L;
                zoomDetail.OriginalShown += _ => shownAt = Stopwatch.GetTimestamp();

                for (var i = 0; i < 6; i++)
                {
                    if (i > 0)
                    {
                        var count = presented.Count;
                        await (i % 2 == 1 ? window.ViewModel.NextAsync() : window.ViewModel.PreviousAsync());
                        Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > count, PresentTimeout));
                    }
                    shownAt = 0;
                    var start = Stopwatch.GetTimestamp();
                    window.SetZoom(1.0);
                    Assert.True(await StaTestHost.WaitForAsync(() => shownAt != 0, PresentTimeout));
                    samples.Add((shownAt - start) * 1000.0 / Stopwatch.Frequency);
                    window.ViewModel.Viewer.ResetFit(WindowContent.Width, WindowContent.Height);
                }
            });
        }
        finally
        {
            if (window is not null) await StaTestHost.RunAsync(() => { window.Close(); return Task.CompletedTask; });
        }

        output.WriteLine(string.Join(" ", samples.Select(s => s.ToString("0", System.Globalization.CultureInfo.InvariantCulture))) + " ms");
        var sorted = samples.Order().ToArray();
        output.WriteLine($"zoom->sharp 24MP: median {sorted[sorted.Length / 2]:0} ms, min {sorted[0]:0} ms, max {sorted[^1]:0} ms (n={sorted.Length})");
    }

    private static readonly string[] MeasureNames = ["a.jpg", "b.jpg"];

    /// <summary>
    /// The production graph reads the user's real config.json; pin the settings this test depends on
    /// (in memory only, never saved) before the first image is opened.
    /// </summary>
    private static MainWindow OpenWindow(string path, Action<string> onPresented, DecoderBackend backend)
    {
        var window = TestAppHost.CreateMainWindow(null, new TestHostHooks { OnPresented = onPresented, DisablePreload = true });
        window.Settings.LoadingMode = LoadingMode.Preview;
        window.Settings.InitialViewMode = InitialViewMode.Fit;
        window.Settings.DecoderBackend = backend;
        _ = window.ViewModel.OpenPathAsync(path);
        return window;
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

    private static LayoutSnapshot Snapshot(MainWindow window) => new(
        Math.Round(window.MainImage.ActualWidth, 3),
        Math.Round(window.MainImage.ActualHeight, 3),
        Math.Round(window.ImageScroll.ExtentWidth, 3),
        Math.Round(window.ImageScroll.ExtentHeight, 3),
        Math.Round(window.ImageScroll.HorizontalOffset, 3),
        Math.Round(window.ImageScroll.VerticalOffset, 3));

    private readonly record struct LayoutSnapshot(
        double ImageWidth, double ImageHeight, double ExtentWidth, double ExtentHeight, double HorizontalOffset, double VerticalOffset);

    /// <param name="noise">Adds deterministic per-pixel noise so the file compresses (and decodes)
    /// like a real photo (~10+ MB at 24 MP) instead of a smooth gradient.</param>
    private static string WriteJpeg(string path, int width, int height, bool noise = false)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        uint seed = 2463534242;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 3;
                var n = 0;
                if (noise)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    n = (int)(seed % 61) - 30;
                }
                pixels[i] = (byte)Math.Clamp(x * 255 / width + n, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(y * 255 / height - n, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((((x / 64) + (y / 64)) % 2 == 0 ? 40 : 220) + n, 0, 255);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
