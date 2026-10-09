using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Q-TOUCHPAD-REFRESH: <see cref="ViewerState.EffectiveScalingQuality"/> (what the main image binds to).</summary>
public sealed class ViewerStateRefreshScalingTests
{
    [Theory]
    [InlineData(ScalingQuality.HighQuality, false, ScalingQuality.HighQuality)]
    [InlineData(ScalingQuality.Linear, false, ScalingQuality.Linear)]
    [InlineData(ScalingQuality.Linear, true, ScalingQuality.HighQuality)]
    [InlineData(ScalingQuality.HighQuality, true, ScalingQuality.HighQuality)]
    public void EffectiveScalingQuality_FollowsTheSettingUnlessARefreshForcedHighQuality(ScalingQuality setting, bool forced, ScalingQuality expected)
    {
        var viewer = new ViewerState { ScalingQuality = setting, ForceHighQualityScaling = forced };

        Assert.Equal(expected, viewer.EffectiveScalingQuality);
    }

    [Fact]
    public void EffectiveScalingQuality_IsNotifiedWhenEitherInputChanges()
    {
        var viewer = new ViewerState();
        var notified = 0;
        viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewerState.EffectiveScalingQuality)) notified++; };

        viewer.ScalingQuality = ScalingQuality.Linear;
        viewer.ForceHighQualityScaling = true;

        Assert.Equal(2, notified);
    }
}
