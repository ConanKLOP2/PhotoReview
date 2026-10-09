using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// User report "ghosting, ~1-2 in 10 keyboard zooms": the zoom was applied in one dispatcher operation and the anchored
/// scroll only after the render pass, so that render pass COMMITTED a frame at the new size with the OLD scroll offsets
/// (e.g. 100 % from Fit showed the image's top-left corner for one frame before jumping to the centre). Whether the
/// compositor presented that frame depended on vsync timing, hence "sometimes". This test records the layout state of
/// every frame the real <see cref="MainWindow"/> commits (the MediaContext render operation, observed through
/// <see cref="Dispatcher.Hooks"/>) and requires that no frame at the new size shows any offset but the final one.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class KeyboardZoomFrameTests
{
    private static readonly FieldInfo? OperationMethod =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Key[] ZoomKeys = [Key.D1, Key.Add, Key.Add, Key.Subtract];

    private sealed record Frame(double ImageWidth, double HorizontalOffset, double VerticalOffset);

    [Fact]
    public async Task KeyboardZoom_NeverCommitsAFrameAtTheNewSizeWithTheOldScrollOffsets()
    {
        Assert.NotNull(OperationMethod); // the probe needs it; fail loudly rather than pass vacuously
        using var root = new TempRoot("keyboard-zoom-frames");
        using var dataRoot = new DataRootFixture();
        var file = Path.Combine(root.Path, "portrait.jpg");
        WriteJpeg(file, 3000, 4500);
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var presented = new List<string>();
                window = TestAppHost.CreateMainWindow(file, new TestHostHooks { OnPresented = presented.Add });
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1600;
                window.Height = 1000;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.Show();
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(20)), "Image never presented");
                await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(500));

                var shown = window;
                var frames = new List<Frame>();
                void OnCompleted(object? sender, DispatcherHookEventArgs e)
                {
                    if (OperationMethod!.GetValue(e.Operation) is Delegate callback
                        && callback.Method.Name.Contains("RenderMessageHandler", StringComparison.Ordinal))
                        frames.Add(new Frame(shown.MainImage.ActualWidth, shown.ImageScroll.HorizontalOffset, shown.ImageScroll.VerticalOffset));
                }

                var hooks = Dispatcher.CurrentDispatcher.Hooks;
                hooks.OperationCompleted += OnCompleted;
                try
                {
                    // 100 % from Fit (offsets 0,0 -> centre: the worst case), then steps in and out at 100 %+.
                    foreach (var key in ZoomKeys)
                    {
                        frames.Clear();
                        var before = shown.MainImage.ActualWidth;
                        Press(shown, key);
                        await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(600));
                        var final = new Frame(shown.MainImage.ActualWidth, shown.ImageScroll.HorizontalOffset, shown.ImageScroll.VerticalOffset);

                        Assert.NotEqual(before, final.ImageWidth, 0); // the key really zoomed
                        var atNewSize = frames.Where(f => Math.Abs(f.ImageWidth - final.ImageWidth) < 0.5).ToList();
                        Assert.NotEmpty(atNewSize); // the hook really saw the committed frames
                        Assert.All(atNewSize, f =>
                        {
                            Assert.True(Math.Abs(f.HorizontalOffset - final.HorizontalOffset) < 1 && Math.Abs(f.VerticalOffset - final.VerticalOffset) < 1,
                                $"{key}: a frame was committed at the new size {f.ImageWidth:F0} with offsets ({f.HorizontalOffset:F0},{f.VerticalOffset:F0}); final ({final.HorizontalOffset:F0},{final.VerticalOffset:F0})");
                        });
                    }
                }
                finally
                {
                    hooks.OperationCompleted -= OnCompleted;
                }
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    opened.Close();
                    return Task.CompletedTask;
                });
        }
    }

    private static void Press(MainWindow window, Key key)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = PresentationSource.FromVisual(window)
            ?? HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The hosted MainWindow has no PresentationSource to raise key input from.");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static void WriteJpeg(string path, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(x ^ y);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
