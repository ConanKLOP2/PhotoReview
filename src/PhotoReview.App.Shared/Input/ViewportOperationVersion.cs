namespace PhotoReview.App.Input;

/// <summary>
/// AR13: the viewport-operation counter shared by zoom-at-point (<see cref="PointerInputController"/>) and Fit.
/// Each operation takes a new version before its first render yield; when it resumes it only applies its scroll
/// if no later operation (of either kind) has started since. UI thread only.
/// </summary>
internal sealed class ViewportOperationVersion
{
    public long Current { get; private set; }

    /// <summary>Starts a new operation, superseding any pending one; returns its version.</summary>
    public long Next() => ++Current;
}
