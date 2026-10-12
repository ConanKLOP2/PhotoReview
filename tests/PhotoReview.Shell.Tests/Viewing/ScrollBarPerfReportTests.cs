using System.Diagnostics;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Rendering;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-26: số đo chi phí của thanh cuộn (chỉ in ra, không khẳng định thời gian - CI không ổn định). Chạy tay:
/// <c>dotnet test --filter "Category=Manual&amp;FullyQualifiedName~ScrollBarPerfReport"</c>.
/// </summary>
[Trait("Category", "Manual")]
public sealed class ScrollBarPerfReportTests(ITestOutputHelper output)
{
    private const int Iterations = 20_000;

    [Fact]
    public void Report_HitTest_DragMove_AndDrawCost()
    {
        using var rig = new ScrollBarRig();
        var recorder = new FillRecorder();

        // Làm nóng.
        for (var i = 0; i < 2_000; i++)
        {
            rig.Bars.HitTest(new PhotoReview.App.Input.PointD(995, 50 + (i % 400)));
            rig.Bars.Draw(recorder);
        }

        var hit = Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
        {
            rig.Bars.HitTest(new PhotoReview.App.Input.PointD(995, 50 + (i % 400)));
        }

        var hitUs = Elapsed(hit) / Iterations * 1000;

        var draw = Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
        {
            rig.Bars.Draw(recorder);
        }

        var drawUs = Elapsed(draw) / Iterations * 1000;

        rig.Down(995, 50);
        var move = Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
        {
            rig.Window.Dispatch(PhotoReview.Shell.Interop.WindowMessages.WmMouseMove, 0x0001, ScrollBarRig.Pack(995, 50 + (i % 400)));
        }

        var moveUs = Elapsed(move) / Iterations * 1000;
        rig.Up(995, 50);

        using var surface = D2DRenderSurface.CreateOffscreenSurface(1200, 800, 1);
        var frames = 200;
        for (var i = 0; i < 20; i++)
        {
            using var warm = RenderTestImages.Render(surface, dc => rig.Bars.Draw(dc));
        }

        var d2d = Stopwatch.GetTimestamp();
        for (var i = 0; i < frames; i++)
        {
            using var frame = RenderTestImages.Render(surface, dc => rig.Bars.Draw(dc));
        }

        var d2dMs = Elapsed(d2d) / frames;

        output.WriteLine($"HitTest: {hitUs:F2} us/lan; Draw (ghi lenh): {drawUs:F2} us/lan; WM_MOUSEMOVE khi keo (ScrollTo + layout): {moveUs:F2} us/lan");
        output.WriteLine($"Ve that (WARP offscreen 1200x800, gom ca doc lai pixel): {d2dMs:F2} ms/khung");
    }

    private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
}
