using System.Diagnostics;
using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP-15: số đo renderer (Category=Manual - báo số, không assert thời gian). Chạy:
/// <c>dotnet test tests/PhotoReview.Shell.Tests -c Release --filter "FullyQualifiedName~RendererPerfTests" --logger "console;verbosity=detailed"</c>.
/// Ảnh 24 MP (6000x4000 Pbgra32 = 96 MB), viewport 1920x1080; WARP và GPU thật (nếu D3D11 hardware tạo được).
/// "submit" = BeginDraw..EndDrawAndPresent (CPU); "done" = thêm chờ GPU xong (đọc 1 pixel qua texture staging).
/// </summary>
[Trait("Category", "Manual")]
public sealed class RendererPerfTests(ITestOutputHelper output)
{
    private const int ImageWidth = 6000;
    private const int ImageHeight = 4000;
    private const int ViewWidth = 1920;
    private const int ViewHeight = 1080;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Measure24MpUploadAndFrames(bool hardware)
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(ViewWidth, ViewHeight, 1, useHardware: hardware);
        string device = surface.IsWarp ? "WARP" : "GPU";
        Line($"== device {device} (requested hardware={hardware}), max bitmap {surface.MaxBitmapSize}");
        if (hardware && surface.IsWarp)
        {
            Line($"no hardware D3D11 device on this machine: skipped");
            Assert.True(surface.IsWarp);
            return;
        }

        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(surface, dispatcher) { BudgetBytes = long.MaxValue };
        using PixelBuffer image = RenderTestImages.CreateSmooth(ImageWidth, ImageHeight);
        using PixelBuffer fullHd = RenderTestImages.CreateSmooth(ViewWidth, ViewHeight);

        // Lượt bỏ (JIT, khởi tạo driver).
        cache.GetOrUpload(image);
        surface.WaitForGpu();
        cache.Clear();

        var upload24 = new List<double>();
        for (int i = 0; i < 9; i++)
        {
            long start = Stopwatch.GetTimestamp();
            cache.GetOrUpload(image);
            surface.WaitForGpu();
            upload24.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            cache.Clear();
        }

        Summary("upload 24MP (GetOrUpload + GPU done)", upload24.Skip(1).ToList());

        var uploadFhd = new List<double>();
        for (int i = 0; i < 41; i++)
        {
            long start = Stopwatch.GetTimestamp();
            cache.GetOrUpload(fullHd);
            surface.WaitForGpu();
            uploadFhd.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            cache.Clear();
        }

        Summary("upload 1920x1080 (GetOrUpload + GPU done)", uploadFhd.Skip(1).ToList());

        // Prefetch theo dải: thời gian từng việc nền (UI thread bị chiếm tối đa bấy nhiêu mỗi lần).
        cache.Prefetch(image);
        var stripes = new List<double>();
        while (true)
        {
            long start = Stopwatch.GetTimestamp();
            if (!dispatcher.RunOne())
            {
                break;
            }

            stripes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        surface.WaitForGpu();
        Summary($"prefetch stripe ({GpuImageCache.DefaultStripeBytes / 1024} KB) x{stripes.Count}", stripes);
        IGpuImage gpu = cache.GetOrUpload(image);

        MeasureFrames(surface, null, "baseline clear-only (sync overhead)", 110, _ => (default, null), ImageInterpolation.Linear);
        MeasureFrames(surface, gpu, "pan 100% 1920x1080 HQC", 210, frame =>
            (new RectD(0, 0, ViewWidth, ViewHeight), new RectD(frame * 7 % 4000, frame * 3 % 2900, ViewWidth, ViewHeight)),
            ImageInterpolation.HighQualityCubic);
        MeasureFrames(surface, gpu, "pan 200% zoom HQC", 110, frame =>
            (new RectD(0, 0, ViewWidth, ViewHeight), new RectD(frame * 5 % 5000, frame * 3 % 3400, ViewWidth / 2.0, ViewHeight / 2.0)),
            ImageInterpolation.HighQualityCubic);
        MeasureFrames(surface, gpu, "fit 24MP->1620x1080 HQC", 60, _ =>
            (new RectD(150, 0, 1620, 1080), (RectD?)null), ImageInterpolation.HighQualityCubic);
        MeasureFrames(surface, gpu, "fit 24MP->1620x1080 Linear", 60, _ =>
            (new RectD(150, 0, 1620, 1080), (RectD?)null), ImageInterpolation.Linear);

        Assert.True(upload24.Count > 0);
    }

    private void MeasureFrames(D2DRenderSurface surface, IGpuImage? image, string name, int frames,
        Func<int, (RectD Destination, RectD? Source)> layout, ImageInterpolation interpolation)
    {
        var submit = new List<double>();
        var done = new List<double>();
        for (int frame = 0; frame < frames; frame++)
        {
            (RectD destination, RectD? source) = layout(frame);
            long start = Stopwatch.GetTimestamp();
            IDrawContext dc = surface.BeginDraw();
            dc.Clear(RenderColors.DarkCanvas);
            if (image is not null)
            {
                dc.DrawImage(image, destination, source, interpolation);
            }

            surface.EndDrawAndPresent();
            double submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            surface.WaitForGpu();
            double completed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame >= 10)
            {
                submit.Add(submitted);
                done.Add(completed);
            }
        }

        Summary(name + " submit", submit);
        Summary(name + " done", done);
    }

    private void Summary(string name, List<double> values)
    {
        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)Math.Ceiling(q * values.Count) - 1)];
        Line($"{name}: n={values.Count} P50={P(0.5):F2} P95={P(0.95):F2} max={values[^1]:F2} ms");
    }

    private void Line(FormattableString text) => output.WriteLine(FormattableString.Invariant(text));
}
