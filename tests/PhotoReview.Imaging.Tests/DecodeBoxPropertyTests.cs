namespace PhotoReview.Imaging.Tests;

/// <summary>Property test of DecodeBox.Fit/FitStored against a decimal-arithmetic reference over ordinary and extreme (int.MaxValue) sizes.</summary>
[Trait("Category", "HotPath")]
public sealed class DecodeBoxPropertyTests
{
    private static int RandomSize(Random rng) => rng.Next(7) switch
    {
        0 => rng.Next(1, 4),
        1 => rng.Next(1, 300),
        2 => rng.Next(1, 8_000),
        3 => rng.Next(30_000, 70_000),
        4 => int.MaxValue - rng.Next(0, 3),
        5 => rng.Next(1, 200_000),
        _ => rng.Next(1, 100),
    };

    private static int RandomBoxSide(Random rng) => rng.Next(6) switch
    {
        0 => 0,
        1 => rng.Next(1, 10),
        2 => rng.Next(1, 200),
        3 => rng.Next(100, 4_100),
        4 => int.MaxValue - rng.Next(0, 3),
        _ => -rng.Next(0, 5),
    };

    [Fact(DisplayName = "Fit never upscales, never exceeds a constrained axis, keeps aspect to within one pixel, and matches the decimal reference")]
    public void Fit_SatisfiesGeometricInvariants()
    {
        var rng = new Random(2026);
        for (var i = 0; i < 200_000; i++)
        {
            int srcW = RandomSize(rng), srcH = RandomSize(rng);
            var box = new DecodeBox(RandomBoxSide(rng), RandomBoxSide(rng));
            var context = $"src={srcW}x{srcH} box={box.Width}x{box.Height}";

            var (w, h) = box.Fit(srcW, srcH);

            Assert.True(w >= 1 && h >= 1 && w <= srcW && h <= srcH, "out of [1, source]: " + context + $" -> {w}x{h}");
            if (box.Width > 0) Assert.True(w <= box.Width || w == srcW, "exceeds box width: " + context + $" -> {w}x{h}");
            if (box.Height > 0) Assert.True(h <= box.Height || h == srcH, "exceeds box height: " + context + $" -> {w}x{h}");

            var widthExceeds = box.Width > 0 && srcW > box.Width;
            var heightExceeds = box.Height > 0 && srcH > box.Height;
            if (!widthExceeds && !heightExceeds)
            {
                Assert.True((w, h) == (srcW, srcH), "source already fits so it must be unchanged: " + context);
                continue;
            }

            // Reference: scale by the tighter of the two ratios; the constrained side is exact, the other is the floor (min 1).
            var scaleW = box.Width > 0 ? (decimal)box.Width / srcW : decimal.MaxValue;
            var scaleH = box.Height > 0 ? (decimal)box.Height / srcH : decimal.MaxValue;
            if (scaleW <= scaleH)
            {
                Assert.True(w == box.Width, "width should be the constraining side: " + context + $" -> {w}x{h}");
                Assert.True(h == Math.Max(1, (int)Math.Floor((decimal)srcH * box.Width / srcW)), "wrong derived height: " + context + $" -> {w}x{h}");
            }
            else
            {
                Assert.True(h == box.Height, "height should be the constraining side: " + context + $" -> {w}x{h}");
                Assert.True(w == Math.Max(1, (int)Math.Floor((decimal)srcW * box.Height / srcH)), "wrong derived width: " + context + $" -> {w}x{h}");
            }
        }
    }

    [Fact(DisplayName = "FitStored(transposed) is Fit on the rotated size, swapped back; unrotated FitStored is Fit")]
    public void FitStored_IsFitOnTheDisplayedSize()
    {
        var rng = new Random(77);
        for (var i = 0; i < 50_000; i++)
        {
            int srcW = RandomSize(rng), srcH = RandomSize(rng);
            var box = new DecodeBox(RandomBoxSide(rng), RandomBoxSide(rng));

            Assert.Equal(box.Fit(srcW, srcH), box.FitStored(srcW, srcH, transposed: false));
            var (dw, dh) = box.Fit(srcH, srcW);
            Assert.Equal((dh, dw), box.FitStored(srcW, srcH, transposed: true));
        }
    }
}
