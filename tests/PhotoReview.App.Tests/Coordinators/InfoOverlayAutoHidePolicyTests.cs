using PhotoReview.App.Coordinators;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Q-R34: when the info overlays over the photo may fade out, and that a hidden overlay is click-through.</summary>
public sealed class InfoOverlayAutoHidePolicyTests
{
    private static InfoOverlayAutoHidePolicy.Outcome Eval(bool enabled = true, bool folder = true, bool attention = false, bool compare = false, bool active = true, bool idle = true) =>
        InfoOverlayAutoHidePolicy.Evaluate(enabled, folder, attention, compare, active, idle);

    [Fact]
    public void Enabled_IdleElapsed_NothingForcesVisibility_HidesAndIsClickThrough()
    {
        var outcome = Eval();
        Assert.Equal(0.0, outcome.Opacity);
        Assert.False(outcome.IsHitTestVisible);
    }

    [Fact]
    public void Enabled_IdleNotElapsed_StaysVisibleAndHitTestable()
    {
        var outcome = Eval(idle: false);
        Assert.Equal(1.0, outcome.Opacity);
        Assert.True(outcome.IsHitTestVisible);
    }

    [Fact]
    public void Disabled_NeverHides_EvenWhenIdle()
    {
        var outcome = Eval(enabled: false);
        Assert.Equal(1.0, outcome.Opacity);
        Assert.True(outcome.IsHitTestVisible);
    }

    [Theory]
    [InlineData(false, false, false, true)]  // no folder open
    [InlineData(true, true, false, true)]    // loading / error / warning / event message showing
    [InlineData(true, false, true, true)]    // Compare open
    [InlineData(true, false, false, false)]  // window inactive (dialog, Settings, other app)
    public void IdleElapsed_ButAStayVisibleRuleApplies_StaysVisible(bool folder, bool attention, bool compare, bool active)
    {
        var outcome = Eval(folder: folder, attention: attention, compare: compare, active: active, idle: true);
        Assert.Equal(1.0, outcome.Opacity);
        Assert.True(outcome.IsHitTestVisible);
        Assert.True(InfoOverlayAutoHidePolicy.MustStayVisible(true, folder, attention, compare, active));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Matrix_HiddenOnlyWhenEnabledAndIdleAndNoStayVisibleRule(bool enabled, bool idle)
    {
        var hidden = Eval(enabled: enabled, idle: idle).Opacity == 0.0;
        Assert.Equal(enabled && idle, hidden);
    }

    [Fact]
    public void DelayMs_UsesOnlyTheInfoDelay_IndependentOfTheToolbarDelay()
    {
        var settings = new AppSettings { ToolbarAutoHideDelayMs = 9000, InfoOverlayAutoHideDelayMs = 3000 };
        Assert.Equal(3000, InfoOverlayAutoHidePolicy.DelayMs(settings));
        settings.ToolbarAutoHideDelayMs = 1;
        Assert.Equal(3000, InfoOverlayAutoHidePolicy.DelayMs(settings));
        settings.InfoOverlayAutoHideDelayMs = 4200;
        Assert.Equal(4200, InfoOverlayAutoHidePolicy.DelayMs(settings));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99999, 10000)]
    public void DelayMs_IsClampedToTheAllowedRange(int stored, int expected)
    {
        Assert.Equal(expected, InfoOverlayAutoHidePolicy.DelayMs(new AppSettings { InfoOverlayAutoHideDelayMs = stored }));
    }

    [Fact]
    public void MustStayVisible_OnlyTheDefaultStateIsEligibleToHide()
    {
        Assert.False(InfoOverlayAutoHidePolicy.MustStayVisible(true, true, false, false, true));
    }
}
