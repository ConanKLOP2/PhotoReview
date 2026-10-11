using System.Diagnostics;
using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-17: số đo chữ DirectWrite + overlay (Category=Manual - báo số, không assert thời gian). Chạy:
/// <c>dotnet test tests/PhotoReview.Shell.Tests -c Release --filter "FullyQualifiedName~TextOverlayPerfTests" --logger "console;verbosity=detailed"</c>.
/// "submit" = BeginDraw..EndDrawAndPresent (CPU); "done" = thêm chờ GPU xong. Cảnh = 5 panel của G-OVL (1920x1080, DPI 1,5).
/// </summary>
[Trait("Category", "Manual")]
public sealed class TextOverlayPerfTests(ITestOutputHelper output)
{
    private static readonly TextStyle Style = new("Segoe UI", 12);

    [Fact]
    public void MeasureLayoutBuildAndCacheHit()
    {
        using var renderer = new DWriteTextRenderer("vi-VN", 4096);
        for (int i = 0; i < 50; i++)
        {
            renderer.CreateLayout("warm-up " + i, Style, 300, TextTrimming.CharacterEllipsis).Dispose();
        }

        var miss = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            long t = Stopwatch.GetTimestamp();
            renderer.CreateLayout("Ảnh số " + i + " – Trường Đại học Bách khoa Hà Nội", Style, 300, TextTrimming.CharacterEllipsis).Dispose();
            miss.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        var hit = new List<double>();
        for (int i = 0; i < 2000; i++)
        {
            long t = Stopwatch.GetTimestamp();
            renderer.CreateLayout("Ảnh số 7 – Trường Đại học Bách khoa Hà Nội", Style, 300, TextTrimming.CharacterEllipsis).Dispose();
            hit.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        Summary("CreateLayout cache MISS (build, ellipsis)", miss, "ms");
        Summary("CreateLayout cache HIT", hit, "ms");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureOverlayFrame(bool hardware)
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(1920, 1080, 1.5, useHardware: hardware);
        string device = surface.IsWarp ? "WARP" : "GPU";
        output.WriteLine($"== device {device} (requested hardware={hardware})");
        if (hardware && surface.IsWarp)
        {
            output.WriteLine("no hardware D3D11 device: skipped");
            return;
        }

        using var renderer = new DWriteTextRenderer("vi-VN", 256);
        using OverlayHost host = OverlayGoldenTests.BuildScene(12);
        var context = new OverlayLayoutContext(new SizeD(1920 / 1.5, 1080 / 1.5), 1.5, renderer);
        host.Arrange(context);

        var steadyArrange = new List<double>();
        var submit = new List<double>();
        var done = new List<double>();
        for (int frame = 0; frame < 160; frame++)
        {
            long start = Stopwatch.GetTimestamp();
            host.Arrange(context);
            double arrange = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            IDrawContext dc = surface.BeginDraw();
            dc.Clear(new ColorF(0.06f, 0.06f, 0.06f, 1f));
            host.Render(dc);
            surface.EndDrawAndPresent();
            double submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            surface.WaitForGpu();
            double completed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame >= 20)
            {
                steadyArrange.Add(arrange);
                submit.Add(submitted);
                done.Add(completed);
            }
        }

        Summary(device + " overlay Arrange (layouts cached)", steadyArrange, "ms");
        Summary(device + " overlay Arrange+Render submit (CPU)", submit, "ms");
        Summary(device + " overlay Arrange+Render done (GPU)", done, "ms");

        // Chữ đổi mỗi khung (đồng hồ/zoom %): layout miss mỗi khung.
        var changing = new List<double>();
        var zoom = (TextElement)host.Find("ZoomIndicatorText")!;
        for (int frame = 0; frame < 160; frame++)
        {
            zoom.Text = (50 + frame).ToString(CultureInfo.InvariantCulture) + " %";
            long start = Stopwatch.GetTimestamp();
            host.Arrange(context);
            IDrawContext dc = surface.BeginDraw();
            dc.Clear(new ColorF(0.06f, 0.06f, 0.06f, 1f));
            host.Render(dc);
            surface.EndDrawAndPresent();
            double submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            surface.WaitForGpu();
            if (frame >= 20)
            {
                changing.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        Summary(device + " overlay frame, zoom text changes each frame, done", changing, "ms");
    }

    private void Summary(string name, List<double> values, string unit)
    {
        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)Math.Ceiling(q * values.Count) - 1)];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: n={values.Count} P50={P(0.5):F3} P95={P(0.95):F3} max={values[^1]:F3} {unit}"));
    }
}
