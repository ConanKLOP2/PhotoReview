using PhotoReview.Imaging;

namespace PhotoReview.Imaging.Tests;

public sealed class AdaptivePreviewPolicyTests
{
    [Fact(DisplayName = "Decode width clamped between MinimumDecodeWidth and MaximumDecodeWidth")]
    public void CalculateTargetDecodeWidth_Clamped()
    {
        // Very small request clamped to 1200
        var small = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(500, 1.0, 1.0);
        Assert.Equal(AdaptivePreviewPolicy.MinimumDecodeWidth, small);

        // Very large request clamped to 4000
        var large = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(5000, 2.0, 1.5);
        Assert.Equal(AdaptivePreviewPolicy.MaximumDecodeWidth, large);

        // Normal request: 1920 * 1.25 * 1.15 = 2760
        var normal = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, 1.25, 1.15);
        Assert.Equal(2760, normal);
    }

    [Fact(DisplayName = "Invalid non-positive or non-finite inputs throw ArgumentOutOfRangeException")]
    public void CalculateTargetDecodeWidth_ThrowsOnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(-10));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, 1.0, -1));
    }
}
