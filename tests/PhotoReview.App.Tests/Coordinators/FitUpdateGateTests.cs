using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>F11 fullscreen jitter: intermediate size changes are suppressed and coalesced into one final Fit update.</summary>
public sealed class FitUpdateGateTests
{
    [Fact]
    public void ShouldApply_NoTransition_IsTrue() => Assert.True(new FitUpdateGate().ShouldApply);

    [Fact]
    public void ShouldApply_AfterBegin_IsFalse()
    {
        var gate = new FitUpdateGate();
        gate.Begin();
        Assert.False(gate.ShouldApply);
    }

    [Fact]
    public void TryEnd_LatestToken_ReopensGateAndRequestsOneUpdate()
    {
        var gate = new FitUpdateGate();
        var token = gate.Begin();

        Assert.True(gate.TryEnd(token));
        Assert.True(gate.ShouldApply);
    }

    [Fact]
    public void TryEnd_SupersededToken_KeepsGateClosedAndRequestsNoUpdate()
    {
        var gate = new FitUpdateGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.False(gate.TryEnd(first));
        Assert.False(gate.ShouldApply);
        Assert.True(gate.TryEnd(second));
        Assert.True(gate.ShouldApply);
    }

    [Fact]
    public void TryEnd_RapidToggleThenSettle_RunsExactlyOneUpdate()
    {
        var gate = new FitUpdateGate();
        var updates = 0;
        var tokens = new[] { gate.Begin(), gate.Begin(), gate.Begin() };
        foreach (var t in tokens)
            if (gate.TryEnd(t)) updates++;

        Assert.Equal(1, updates);
    }
}
