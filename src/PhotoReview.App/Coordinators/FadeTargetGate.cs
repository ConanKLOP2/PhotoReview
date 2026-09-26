namespace PhotoReview.App.Coordinators;

/// <summary>
/// Remembers the opacity an element was last faded to, so a caller that re-evaluates its state on every mouse move
/// starts a new animation only when the target really changes (no allocation or animation-clock restart per event).
/// </summary>
public sealed class FadeTargetGate
{
    private double? _target;

    /// <summary>True when <paramref name="target"/> differs from the last one accepted (the first call always is); records it.</summary>
    public bool TryChange(double target)
    {
        if (_target == target) return false;
        _target = target;
        return true;
    }
}
