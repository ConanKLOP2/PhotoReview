using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App.Converters;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using Xunit.Abstractions;

namespace PhotoReview.App.Tests.Viewport;

/// <summary>
/// WP-16: <see cref="ViewportLayoutEngine"/> so với layout WPF THẬT của cặp <c>ImageScroll</c>/<c>MainImage</c> - cùng
/// <c>Themes/DarkScrollBars.xaml</c> (thanh cuộn 10 DIP, lưới 2x2), cùng <see cref="ViewerStretchModeConverter"/>, cùng các
/// thuộc tính mà MainWindow.xaml bind từ <see cref="ViewerState"/> (Stretch, MaxWidth/MaxHeight, Width/Height, Source). Mỗi ca
/// dựng một ScrollViewer tách rời trên luồng STA (không cửa sổ, không desktop), Measure/Arrange ở kích thước client rồi đọc
/// ViewportWidth/Height, ExtentWidth/Height, ComputedScrollBarVisibility, ScrollableWidth/Height, TranslatePoint và
/// ActualWidth/Height của ảnh - đúng các trường của <see cref="ViewportLayout"/>. Đây là đối chứng tại chỗ trong khi chờ golden
/// G-VIEW (WP-10, ghi từ MainWindow thật); nó không thay golden.
/// Lưới: 7 client x 4 DPI x 5 ảnh x (Fit, FitWidth, FitHeight, 16 mức zoom 25..400 %) + ca biên của thanh cuộn thứ hai
/// + bitmap DPI khác 96 (C-07 mục 10). Sai số chấp nhận 0,5 DIP (C-08); thực đo được in ra.
/// </summary>
[Trait("Category", "UI")]
public sealed class ViewportEngineWpfParityTests(ITestOutputHelper output)
{
    private const double Tolerance = 0.5;
    private const double ScrollBarThickness = 10; // Themes/DarkScrollBars.xaml: ScrollBar Width/Height = 10

    private static readonly (double W, double H)[] Clients =
        [(800, 600), (1280, 720), (1920, 1080), (1366, 728), (3840, 2160), (300, 200), (15, 15)];

    private static readonly double[] DpiScales = [1.0, 1.25, 1.5, 2.0];

    private static readonly (int W, int H)[] Images = [(6000, 4000), (4000, 6000), (800, 600), (12000, 1000), (1000, 12000)];

    private enum Mode { Fit, FitWidth, FitHeight, Zoom }

    private sealed record Case(string Name, ViewportInput Input, double BitmapDpi = 96);

    [Fact]
    public void Grid_EngineMatchesWpfLayout_WithinHalfADip()
    {
        var cases = BuildGrid().ToList();
        Assert.True(cases.Count >= 600, $"lưới quá nhỏ: {cases.Count}");
        var worst = RunAll(cases);
        output.WriteLine($"{cases.Count} ca, lệch lớn nhất {worst.Deviation:G6} DIP ở {worst.Name}");
    }

    [Fact]
    public void SecondBarCascade_AndExactFitBoundaries_MatchWpf()
    {
        // Ca biên của MeasureOverride (lượt 3) và DoubleUtil.GreaterThan: extent bằng đúng client / client - thanh cuộn.
        var cases = new List<Case>
        {
            Explicit("cascade-h-after-v", 1000, 600, 995, 2000),      // v hiện -> viewport 990 -> h hiện
            Explicit("cascade-v-after-h", 1000, 600, 2000, 595),      // h hiện -> viewport 590 -> v hiện
            Explicit("no-cascade-h", 1000, 600, 990, 2000),           // 990 > 990 sai: chỉ v
            Explicit("no-cascade-v", 1000, 600, 2000, 590),
            Explicit("exact-client", 1000, 600, 1000, 600),           // bằng đúng: không thanh nào
            Explicit("exact-client-plus-tiny", 1000, 600, 1000 + 1e-13, 600), // trong DoubleUtil.AreClose: không thanh
            Explicit("just-over-client", 1000, 600, 1000.001, 600),
            Explicit("both-over", 1000, 600, 1001, 601),
            Explicit("smaller-centred", 1000, 600, 400, 300),
            Explicit("client-thinner-than-bar", 6, 6, 50, 50),
            Explicit("zero-size-element", 1000, 600, 0, 0),
            new("fit-infinite-bound", Input(1000, 600, ViewerStretchMode.Uniform, double.NaN, double.NaN, double.PositiveInfinity, double.PositiveInfinity, 1500, 900)),
            new("fit-width-bound-only", Input(1000, 600, ViewerStretchMode.Uniform, double.NaN, double.NaN, 800, double.PositiveInfinity, 1500, 900)),
            new("fill-auto-size", Input(1000, 600, ViewerStretchMode.None, double.NaN, double.NaN, double.PositiveInfinity, double.PositiveInfinity, 400, 300)),
            new("fill-auto-size-large", Input(1000, 600, ViewerStretchMode.None, double.NaN, double.NaN, double.PositiveInfinity, double.PositiveInfinity, 1400, 300)),
            new("fit-no-bitmap", Input(1000, 600, ViewerStretchMode.Uniform, double.NaN, double.NaN, 1000, 600, 0, 0)),
            new("zoom-no-bitmap", Input(1000, 600, ViewerStretchMode.None, 2000, 1500, double.PositiveInfinity, double.PositiveInfinity, 0, 0)),
            // Bitmap DPI 72/300: BitmapSource.Width = pixel x 96 / DPI; chỉ tỉ lệ ảnh hưởng Fit.
            new("fit-bitmap-72dpi", Input(1280, 720, ViewerStretchMode.Uniform, double.NaN, double.NaN, 1280, 720, 600 * 96 / 72.0, 400 * 96 / 72.0), BitmapDpi: 72),
            new("fit-bitmap-300dpi", Input(1280, 720, ViewerStretchMode.Uniform, double.NaN, double.NaN, 1280, 720, 600 * 96 / 300.0, 400 * 96 / 300.0), BitmapDpi: 300),
        };
        var worst = RunAll(cases);
        output.WriteLine($"{cases.Count} ca biên, lệch lớn nhất {worst.Deviation:G6} DIP ở {worst.Name}");
        Assert.InRange(worst.Deviation, 0, Tolerance);
    }

    [Fact]
    public void Offsets_AreClampedLikeScrollViewer()
    {
        StaUi.Run(() =>
        {
            var resources = LoadTheme();
            foreach (var input in new[]
            {
                Input(1000, 600, ViewerStretchMode.None, 3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 3000, 2000),
                Input(1000, 600, ViewerStretchMode.None, 995, 2000, double.PositiveInfinity, double.PositiveInfinity, 995, 2000),
                Input(1000, 600, ViewerStretchMode.None, 400, 300, double.PositiveInfinity, double.PositiveInfinity, 400, 300),
            })
            {
                foreach (var (h, v) in new[] { (1e9, 1e9), (-50.0, 37.25), (123.5, -1.0), (0.0, 0.0) })
                {
                    var (scroll, _) = Layout(resources, input, 96);
                    scroll.ScrollToHorizontalOffset(h);
                    scroll.ScrollToVerticalOffset(v);
                    scroll.UpdateLayout();
                    var layout = ViewportLayoutEngine.Compute(input);
                    var (eh, ev) = ViewportLayoutEngine.ClampOffset(layout, h, v);
                    Assert.True(Math.Abs(scroll.HorizontalOffset - eh) <= 1e-9, $"H {h}: WPF {scroll.HorizontalOffset} engine {eh}");
                    Assert.True(Math.Abs(scroll.VerticalOffset - ev) <= 1e-9, $"V {v}: WPF {scroll.VerticalOffset} engine {ev}");
                }
            }
        });
    }

    [Fact]
    public void RequestedOffset_IsRememberedAcrossAShrinkAndRegrowOfTheViewport_LikeViewportState()
    {
        // ScrollContentPresenter giữ offset được yêu cầu và chỉ kẹp offset hiệu lực: ViewportState mô hình hoá điều này.
        StaUi.Run(() =>
        {
            var resources = LoadTheme();
            var content = Input(1000, 600, ViewerStretchMode.None, 3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 3000, 2000);
            var (scroll, root) = Layout(resources, content, 96);
            var state = new ViewportState();
            state.SetInput(content);
            state.UpdateLayout();

            scroll.ScrollToHorizontalOffset(1900);
            scroll.UpdateLayout();
            state.ScrollTo(1900, 0);
            state.UpdateLayout();
            Assert.Equal(scroll.HorizontalOffset, state.HorizontalOffset, 9);

            foreach (var width in ShrinkThenRegrow)
            {
                root.Width = width;
                scroll.Width = width;
                root.Measure(new Size(width, 600));
                root.Arrange(new Rect(0, 0, width, 600));
                root.UpdateLayout();
                state.SetInput(content with { ClientWidth = width });
                state.UpdateLayout();
                Assert.Equal(scroll.HorizontalOffset, state.HorizontalOffset, 9);
            }
            Assert.Equal(1900, state.HorizontalOffset, 9); // 1500: kẹp 3000 - 1490 = 1510; 1000 lại: trở về 1900
        });
    }

    private static readonly double[] ShrinkThenRegrow = [1500.0, 1000.0];

    private static (string Name, double Deviation) RunAll(IReadOnlyList<Case> cases)
    {
        var failures = new List<string>();
        (string Name, double Deviation) worst = ("-", 0);
        StaUi.Run(() =>
        {
            var resources = LoadTheme();
            foreach (var c in cases)
            {
                var (scroll, _) = Layout(resources, c.Input, c.BitmapDpi);
                var wpf = Read(scroll);
                var engine = ViewportLayoutEngine.Compute(c.Input);
                var deviation = Deviation(wpf, engine);
                if (deviation > worst.Deviation) worst = (c.Name, deviation);
                if (deviation > Tolerance || wpf.HorizontalBarVisible != engine.HorizontalBarVisible || wpf.VerticalBarVisible != engine.VerticalBarVisible)
                    failures.Add($"{c.Name}: WPF {wpf} / engine {engine}");
            }
        });
        Assert.True(failures.Count == 0, $"{failures.Count}/{cases.Count} ca lệch:\n" + string.Join("\n", failures.Take(25)));
        return worst;
    }

    private static double Deviation(ViewportLayout a, ViewportLayout b) => new[]
    {
        a.ViewportWidth - b.ViewportWidth, a.ViewportHeight - b.ViewportHeight,
        a.ExtentWidth - b.ExtentWidth, a.ExtentHeight - b.ExtentHeight,
        a.ImageRect.X - b.ImageRect.X, a.ImageRect.Y - b.ImageRect.Y,
        a.ImageRect.Width - b.ImageRect.Width, a.ImageRect.Height - b.ImageRect.Height,
        a.MaxHorizontalOffset - b.MaxHorizontalOffset, a.MaxVerticalOffset - b.MaxVerticalOffset,
    }.Select(Math.Abs).Max();

    private static IEnumerable<Case> BuildGrid()
    {
        var zooms = Enumerable.Range(1, 16).Select(i => i * 0.25).ToArray();
        foreach (var (cw, ch) in Clients)
            foreach (var dpi in DpiScales)
                foreach (var (iw, ih) in Images)
                {
                    yield return FromViewer($"{cw}x{ch}@{dpi} {iw}x{ih} Fit", cw, ch, dpi, iw, ih, Mode.Fit, 0);
                    yield return FromViewer($"{cw}x{ch}@{dpi} {iw}x{ih} FitWidth", cw, ch, dpi, iw, ih, Mode.FitWidth, 0);
                    yield return FromViewer($"{cw}x{ch}@{dpi} {iw}x{ih} FitHeight", cw, ch, dpi, iw, ih, Mode.FitHeight, 0);
                    foreach (var z in zooms)
                        yield return FromViewer($"{cw}x{ch}@{dpi} {iw}x{ih} {z * 100:F0}%", cw, ch, dpi, iw, ih, Mode.Zoom, z);
                }
    }

    /// <summary>Input dựng từ <see cref="ViewerState"/> thật, như MainWindow bind (bitmap = bản preview 1/4 cạnh, 96 DPI).</summary>
    private static Case FromViewer(string name, double cw, double ch, double dpi, int iw, int ih, Mode mode, double zoom)
    {
        var viewer = new ViewerState { DpiScale = dpi };
        viewer.SetSourceSize(iw, ih, newImage: true);
        switch (mode)
        {
            case Mode.Fit:
                viewer.ResetFit(cw, ch);
                break;
            case Mode.FitWidth:
                viewer.UpdateViewport(cw, ch, force: true);
                viewer.ZoomToFitWidth();
                break;
            case Mode.FitHeight:
                viewer.UpdateViewport(cw, ch, force: true);
                viewer.ZoomToFitHeight();
                break;
            default:
                viewer.SetZoom(zoom);
                break;
        }
        var input = Input(cw, ch, viewer.Stretch, viewer.ImageWidth, viewer.ImageHeight, viewer.MaxImageWidth, viewer.MaxImageHeight,
            Math.Max(1, iw / 4), Math.Max(1, ih / 4));
        return new Case(name, input);
    }

    private static Case Explicit(string name, double cw, double ch, double w, double h) =>
        new(name, Input(cw, ch, ViewerStretchMode.None, w, h, double.PositiveInfinity, double.PositiveInfinity, Math.Max(1, w), Math.Max(1, h)));

    private static ViewportInput Input(double cw, double ch, ViewerStretchMode stretch, double w, double h, double maxW, double maxH,
        double bitmapW, double bitmapH) =>
        new(cw, ch, ScrollBarThickness, ScrollBarPolicy.Auto, stretch, w, h, maxW, maxH, bitmapW, bitmapH);

    private static ViewportLayout Read(ScrollViewer scroll)
    {
        var image = (Image)scroll.Content;
        var origin = image.TranslatePoint(new Point(0, 0), scroll);
        return new ViewportLayout(
            scroll.ViewportWidth, scroll.ViewportHeight,
            scroll.ExtentWidth, scroll.ExtentHeight,
            scroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
            new PhotoReview.App.Input.RectD(origin.X + scroll.HorizontalOffset, origin.Y + scroll.VerticalOffset, image.ActualWidth, image.ActualHeight),
            scroll.ScrollableWidth, scroll.ScrollableHeight);
    }

    /// <summary>
    /// MainWindow.xaml: Border > ScrollViewer(Auto/Auto, Stretch) > Image. Bitmap có kích thước DIP = input.BitmapWidth/Height
    /// ở <paramref name="bitmapDpi"/> (pixel = DIP x DPI / 96, làm tròn - ca grid chọn số chia hết).
    /// </summary>
    private static (ScrollViewer Scroll, Border Root) Layout(ResourceDictionary theme, ViewportInput input, double bitmapDpi)
    {
        var image = new Image
        {
            Stretch = (Stretch)new ViewerStretchModeConverter().Convert(input.Stretch, typeof(Stretch), null!, System.Globalization.CultureInfo.InvariantCulture),
            MaxWidth = input.MaxImageWidth,
            MaxHeight = input.MaxImageHeight,
            Width = input.ImageWidth,
            Height = input.ImageHeight,
        };
        if (input.BitmapWidth > 0 && input.BitmapHeight > 0)
        {
            var pw = (int)Math.Round(input.BitmapWidth * bitmapDpi / 96);
            var ph = (int)Math.Round(input.BitmapHeight * bitmapDpi / 96);
            image.Source = BitmapSource.Create(pw, ph, bitmapDpi, bitmapDpi, PixelFormats.Gray8, null, new byte[pw * ph], pw);
        }
        var scroll = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = input.ClientWidth,
            Height = input.ClientHeight,
            Content = image,
        };
        var root = new Border { Width = input.ClientWidth, Height = input.ClientHeight, Child = scroll };
        root.Resources.MergedDictionaries.Add(theme);
        root.Measure(new Size(input.ClientWidth, input.ClientHeight));
        root.Arrange(new Rect(0, 0, input.ClientWidth, input.ClientHeight));
        root.UpdateLayout();
        return (scroll, root);
    }

    // ---- nạp Themes/DarkScrollBars.xaml thật (cùng cách DarkScrollBarRenderingTests) ----

    private static ResourceDictionary LoadTheme()
    {
        if (Application.Current is null)
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            catch (InvalidOperationException)
            {
                // Một test khác vừa tạo Application trước: dùng chung là đủ (xem DarkScrollBarRenderingTests).
            }
        }
        RedirectPackUriResourceAssembly();
        return new ResourceDictionary { Source = new Uri("/PhotoReview.App;component/Themes/DarkScrollBars.xaml", UriKind.Relative) };
    }

    /// <summary>Bản sao của DarkScrollBarRenderingTests.RedirectPackUriResourceAssembly (private ở đó; xem lý do trong file ấy).</summary>
    private static void RedirectPackUriResourceAssembly()
    {
        var app = typeof(MainWindow).Assembly;
        try
        {
            Application.ResourceAssembly = app;
            return;
        }
        catch (InvalidOperationException)
        {
            // Đã bị chốt bởi test trước trên luồng khác: ép qua reflection bên dưới.
        }

        const BindingFlags AnyStatic = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        typeof(Application).GetField("_resourceAssembly", AnyStatic)?.SetValue(null, app);
        var baseUriHelper = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MS.Internal.BaseUriHelper", throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (baseUriHelper is not null)
        {
            var property = baseUriHelper.GetProperty("ResourceAssembly", AnyStatic);
            if (property?.SetMethod is not null) property.SetValue(null, app);
            else baseUriHelper.GetField("_resourceAssembly", AnyStatic)?.SetValue(null, app);
        }
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MS.Internal.AppModel.ResourceContainer", throwOnError: false))
            .FirstOrDefault(t => t is not null)
            ?.GetField("_resourceManagerWrapper", AnyStatic)?.SetValue(null, null);
    }
}
