using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>WP-21: chọn bộ lọc cho khung (rủi ro HQC 27-45 ms/khung ở WP-15). Chỉ đổi khi: HQC + đang chuyển động + thu nhỏ nặng.</summary>
[Trait("Category", "HotPath")]
public sealed class ScalingPolicyTests
{
    private const ImageInterpolation Hq = ImageInterpolation.HighQualityCubic;

    [Fact]
    public void Resting_KeepsTheRequestedFilter_EvenForAHeavyDownscale()
    {
        Assert.Equal(Hq, ScalingPolicy.Choose(Hq, 6000, 4000, 1000, 667, inMotion: false));
    }

    [Fact]
    public void InMotion_HeavyDownscale_FallsBackToLinear()
    {
        Assert.Equal(ImageInterpolation.Linear, ScalingPolicy.Choose(Hq, 6000, 4000, 1000, 667, inMotion: true));
    }

    [Fact]
    public void InMotion_LightDownscaleOrUpscale_KeepsHq()
    {
        Assert.Equal(Hq, ScalingPolicy.Choose(Hq, 2000, 1500, 1000, 750, inMotion: true));   // x4 diện tích: đúng ngưỡng, không "nặng"
        Assert.Equal(Hq, ScalingPolicy.Choose(Hq, 1000, 750, 3000, 2250, inMotion: true));   // phóng to
    }

    [Fact]
    public void ThresholdIsStrictlyGreaterThanFourTimesTheArea()
    {
        Assert.False(ScalingPolicy.IsHeavyDownscale(2000, 1000, 1000, 500));  // đúng 4x
        Assert.True(ScalingPolicy.IsHeavyDownscale(2001, 1000, 1000, 500));
    }

    [Theory]
    [InlineData(ImageInterpolation.Linear)]
    [InlineData(ImageInterpolation.NearestNeighbor)]
    public void NonCubicRequests_AreNeverChanged(ImageInterpolation requested)
    {
        Assert.Equal(requested, ScalingPolicy.Choose(requested, 6000, 4000, 100, 66, inMotion: true));
    }

    [Theory]
    [InlineData(0, 100, 10, 10)]
    [InlineData(100, 0, 10, 10)]
    [InlineData(100, 100, 0, 10)]
    [InlineData(100, 100, 10, 0)]
    [InlineData(100, 100, double.NaN, 10)]
    public void DegenerateSizes_AreNotHeavy(int sw, int sh, double dw, double dh)
    {
        Assert.False(ScalingPolicy.IsHeavyDownscale(sw, sh, dw, dh));
        Assert.Equal(Hq, ScalingPolicy.Choose(Hq, sw, sh, dw, dh, inMotion: true));
    }
}
