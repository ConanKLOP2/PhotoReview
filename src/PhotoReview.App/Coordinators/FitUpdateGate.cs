namespace PhotoReview.App.Coordinators;

/// <summary>
/// Coalesces viewport-driven Fit updates around a window transition (F11 fullscreen). While a transition is open,
/// the intermediate SizeChanged events must not recompute the viewport/decode box (that made Fit zoom big, then small);
/// the owner runs one update after layout settles. A newer <see cref="Begin"/> supersedes an older one, so a rapid
/// F11 F11 yields a single final update. UI thread only.
/// </summary>
internal sealed class FitUpdateGate
{
    private int _generation;

    /// <summary>True when no transition is open and a size change may update Fit immediately.</summary>
    public bool ShouldApply { get; private set; } = true;

    /// <summary>Opens a transition; returns the token to pass to <see cref="TryEnd"/>.</summary>
    public int Begin()
    {
        ShouldApply = false;
        return ++_generation;
    }

    /// <summary>
    /// Closes the transition that issued <paramref name="token"/>. Returns true when the caller must now run the single
    /// coalesced update; false when a newer transition superseded this one (its own end will run the update).
    /// </summary>
    public bool TryEnd(int token)
    {
        if (token != _generation) return false;
        ShouldApply = true;
        return true;
    }
}
