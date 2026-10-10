using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16: bất biến của <see cref="ViewportLayoutEngine"/> trên input ngẫu nhiên (seed cố định -> tái lập được): offset luôn trong
/// [0, Max], thanh cuộn Auto hiện đúng khi nội dung tràn viewport của nó, viewport = client trừ thanh đang hiện, ảnh nằm trong ô,
/// Fit giữ tỉ lệ và chạm đúng một cạnh của khung.
/// </summary>
public sealed class ViewportLayoutEnginePropertyTests
{
    private const int Iterations = 20_000;

    private static IEnumerable<ViewportInput> RandomInputs(int seed)
    {
        var random = new Random(seed);
        double Length(double max) => random.Next(10) switch
        {
            0 => 0,
            1 => Math.Round(random.NextDouble() * max), // số nguyên: hay rơi đúng biên
            _ => random.NextDouble() * max,
        };
        var policies = Enum.GetValues<ScrollBarPolicy>();
        for (var i = 0; i < Iterations; i++)
        {
            var cw = Length(4000);
            var ch = Length(2500);
            var policy = policies[random.Next(policies.Length)];
            var thickness = random.Next(4) == 0 ? 0 : 10 + (random.NextDouble() * 10);
            var bitmapW = Length(8000);
            var bitmapH = Length(8000);
            if (random.Next(2) == 0)
            {
                // Fit: MaxImage = client (ResetFit), đôi khi chưa đo (vô hạn).
                var bounded = random.Next(5) != 0;
                yield return new ViewportInput(cw, ch, thickness, policy, ViewerStretchMode.Uniform, double.NaN, double.NaN,
                    bounded ? cw : double.PositiveInfinity, bounded ? ch : double.PositiveInfinity, bitmapW, bitmapH);
            }
            else
            {
                // Zoom: kích thước tường minh, đôi khi đúng bằng client hoặc client - thanh (biên của lượt đo 3).
                double Explicit(double client) => random.Next(6) switch
                {
                    0 => client,
                    1 => Math.Max(0, client - thickness),
                    2 => double.NaN,
                    _ => Length(12000),
                };
                yield return new ViewportInput(cw, ch, thickness, policy, ViewerStretchMode.None, Explicit(cw), Explicit(ch),
                    double.PositiveInfinity, double.PositiveInfinity, bitmapW, bitmapH);
            }
        }
    }

    [Fact]
    public void ClampedOffsets_AreAlwaysWithinZeroAndTheMaximum_AndClampingIsIdempotent()
    {
        var random = new Random(16);
        foreach (var input in RandomInputs(1))
        {
            var layout = ViewportLayoutEngine.Compute(input);
            Assert.True(layout.MaxHorizontalOffset >= 0 && layout.MaxVerticalOffset >= 0);
            var requestH = (random.NextDouble() * 3 * (layout.MaxHorizontalOffset + 10)) - layout.MaxHorizontalOffset - 10;
            var requestV = (random.NextDouble() * 3 * (layout.MaxVerticalOffset + 10)) - layout.MaxVerticalOffset - 10;
            var (h, v) = ViewportLayoutEngine.ClampOffset(layout, requestH, requestV);
            Assert.InRange(h, 0, layout.MaxHorizontalOffset);
            Assert.InRange(v, 0, layout.MaxVerticalOffset);
            Assert.Equal((h, v), ViewportLayoutEngine.ClampOffset(layout, h, v));
            if (requestH >= 0 && requestH <= layout.MaxHorizontalOffset) Assert.Equal(requestH, h);
            if (requestV >= 0 && requestV <= layout.MaxVerticalOffset) Assert.Equal(requestV, v);
        }
    }

    [Fact]
    public void AutoBars_AreVisibleExactlyWhenTheContentOverflowsTheirViewport_WhichLosesTheBarsThickness()
    {
        foreach (var input in RandomInputs(2).Where(i => i.ScrollBars == ScrollBarPolicy.Auto))
        {
            var layout = ViewportLayoutEngine.Compute(input);
            Assert.Equal(ViewportLayoutMath.GreaterThan(layout.ExtentWidth, layout.ViewportWidth), layout.HorizontalBarVisible);
            Assert.Equal(ViewportLayoutMath.GreaterThan(layout.ExtentHeight, layout.ViewportHeight), layout.VerticalBarVisible);
            var thickness = ViewportLayoutMath.SanitizeLength(input.ScrollBarThickness);
            Assert.Equal(Math.Max(0, input.ClientWidth - (layout.VerticalBarVisible ? thickness : 0)), layout.ViewportWidth, 9);
            Assert.Equal(Math.Max(0, input.ClientHeight - (layout.HorizontalBarVisible ? thickness : 0)), layout.ViewportHeight, 9);
            Assert.Equal(Math.Max(0, layout.ExtentWidth - layout.ViewportWidth), layout.MaxHorizontalOffset, 9);
            Assert.Equal(Math.Max(0, layout.ExtentHeight - layout.ViewportHeight), layout.MaxVerticalOffset, 9);
        }
    }

    [Fact]
    public void OverlayAndHiddenBars_NeverTakeSpace()
    {
        foreach (var input in RandomInputs(3).Where(i => i.ScrollBars != ScrollBarPolicy.Auto))
        {
            var layout = ViewportLayoutEngine.Compute(input);
            Assert.Equal(input.ClientWidth, layout.ViewportWidth);
            Assert.Equal(input.ClientHeight, layout.ViewportHeight);
            var overlay = input.ScrollBars == ScrollBarPolicy.Overlay;
            Assert.Equal(overlay && ViewportLayoutMath.GreaterThan(layout.ExtentWidth, layout.ViewportWidth), layout.HorizontalBarVisible);
            Assert.Equal(overlay && ViewportLayoutMath.GreaterThan(layout.ExtentHeight, layout.ViewportHeight), layout.VerticalBarVisible);
        }
    }

    [Fact]
    public void TheImage_StaysInsideItsSlot_AndIsCentredWhenSmaller()
    {
        foreach (var input in RandomInputs(4))
        {
            var layout = ViewportLayoutEngine.Compute(input);
            var slotW = Math.Max(layout.ExtentWidth, layout.ViewportWidth);
            var slotH = Math.Max(layout.ExtentHeight, layout.ViewportHeight);
            var r = layout.ImageRect;
            Assert.True(r.X >= 0 && r.Y >= 0, $"{input} -> {layout}");
            Assert.True(r.Right <= slotW + 1e-6 && r.Bottom <= slotH + 1e-6, $"{input} -> {layout}");
            Assert.Equal((slotW - r.Width) / 2, r.X, 6);
            Assert.Equal((slotH - r.Height) / 2, r.Y, 6);
        }
    }

    [Fact]
    public void BoundedFit_KeepsTheAspectRatio_TouchesOneSideOfTheBox_AndNeverScrolls()
    {
        foreach (var input in RandomInputs(5).Where(i => i.Stretch == ViewerStretchMode.Uniform
                     && double.IsFinite(i.MaxImageWidth) && i.ClientWidth > 0 && i.ClientHeight > 0 && i.BitmapWidth > 0 && i.BitmapHeight > 0))
        {
            var layout = ViewportLayoutEngine.Compute(input);
            var r = layout.ImageRect;
            Assert.False(layout.HorizontalBarVisible || layout.VerticalBarVisible);
            Assert.Equal(0, layout.MaxHorizontalOffset);
            Assert.Equal(0, layout.MaxVerticalOffset);
            Assert.True(r.Width <= input.ClientWidth * (1 + 1e-12) && r.Height <= input.ClientHeight * (1 + 1e-12));
            var touches = Math.Abs(r.Width - input.ClientWidth) <= 1e-9 * input.ClientWidth
                || Math.Abs(r.Height - input.ClientHeight) <= 1e-9 * input.ClientHeight;
            Assert.True(touches, $"{input} -> {layout}");
            Assert.Equal(input.BitmapWidth / input.BitmapHeight, r.Width / r.Height, 6);
        }
    }
}
