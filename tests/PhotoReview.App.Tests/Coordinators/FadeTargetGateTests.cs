using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.Coordinators;

public sealed class FadeTargetGateTests
{
    [Fact(DisplayName = "The first target always starts a fade; repeating it (every mouse move) does not")]
    public void TryChange_SameTargetRepeated_OnlyFirstStarts()
    {
        var gate = new FadeTargetGate();

        Assert.True(gate.TryChange(1.0));
        Assert.False(gate.TryChange(1.0));
        Assert.False(gate.TryChange(1.0));
    }

    [Fact(DisplayName = "A changed target (hide, show, or a new opacity setting) starts a new fade")]
    public void TryChange_DifferentTarget_Starts()
    {
        var gate = new FadeTargetGate();
        Assert.True(gate.TryChange(1.0));

        Assert.True(gate.TryChange(0.0));
        Assert.False(gate.TryChange(0.0));
        Assert.True(gate.TryChange(0.6));
        Assert.True(gate.TryChange(0.0));
    }
}
