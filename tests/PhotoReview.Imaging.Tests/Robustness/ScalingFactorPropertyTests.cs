using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// TurboJpegDecoder.SelectScalingFactor picks the DCT scale whose output still covers the target box. Checked against a
/// brute-force reference over random and extreme (near int.MaxValue) sizes: exactly the smallest covering factor, no
/// overflow, and never a factor that would leave the intermediate smaller than the target.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ScalingFactorPropertyTests
{
    private static readonly (int Num, int Denom)[] Factors = [(1, 8), (1, 4), (3, 8), (1, 2), (5, 8), (3, 4), (7, 8), (1, 1)];

    private static (long W, long H) Scaled(long width, long height, (int Num, int Denom) f) =>
        ((width * f.Num + f.Denom - 1) / f.Denom, (height * f.Num + f.Denom - 1) / f.Denom);

    [Fact(DisplayName = "SelectScalingFactor returns the smallest factor whose scaled size covers the target, for random and extreme sizes")]
    public void SelectScalingFactor_IsSmallestCoveringFactor()
    {
        var rng = new Random(88);
        int RandomSize() => rng.Next(6) switch
        {
            0 => rng.Next(1, 4),
            1 => rng.Next(1, 200),
            2 => rng.Next(1, 8_000),
            3 => rng.Next(60_000, 65_536),
            4 => int.MaxValue - rng.Next(0, 3),
            _ => rng.Next(1, 100_000),
        };

        for (var i = 0; i < 100_000; i++)
        {
            int origW = RandomSize(), origH = RandomSize();
            // A target never exceeds the source (FitStored never upscales), but the selector must also be safe when it does.
            var targetW = rng.Next(4) == 0 ? RandomSize() : (int)rng.NextInt64(1, (long)origW + 1);
            var targetH = rng.Next(4) == 0 ? RandomSize() : (int)rng.NextInt64(1, (long)origH + 1);

            var chosen = TurboJpegDecoder.SelectScalingFactor(origW, origH, targetW, targetH);

            var expected = Factors.FirstOrDefault(f =>
            {
                var (w, h) = Scaled(origW, origH, f);
                return w >= targetW && h >= targetH;
            });
            var expectedFactor = expected == default ? (1, 1) : expected;
            Assert.True((chosen.Num, chosen.Denom) == expectedFactor,
                $"{origW}x{origH} -> {targetW}x{targetH}: chose {chosen}, expected {expectedFactor.Item1}/{expectedFactor.Item2}");
        }
    }
}
