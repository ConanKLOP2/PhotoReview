namespace PhotoReview.App.Services;

/// <summary>
/// Startup UX (perf/startup-first-image): a new window is shown before WPF has rendered anything into it, and that
/// unpainted surface is drawn white by the compositor (the "white flash" when a photo is opened from Explorer). The
/// window is therefore cloaked from the moment its HWND exists (<see cref="Hide"/>, in SourceInitialized, i.e. before
/// it is shown) until its first frame has been rendered (<see cref="Reveal"/>, ContentRendered). Cloaking does not
/// delay anything else: the window is still shown, activated, laid out and rendered as before.
/// <see cref="Reveal"/> is idempotent and also runs on the safety timeout and on close, so the window can never stay
/// invisible. When DWM refuses the cloak nothing changes (the window is shown as before).
/// </summary>
internal sealed class StartupWindowReveal
{
    /// <summary>Test seam: (hwnd, cloaked) -> accepted. Production: DWMWA_CLOAK.</summary>
    internal static Func<IntPtr, bool, bool> SetCloaked { get; set; } = PhotoReview.Platform.Windows.WindowsDwmCloak.TrySetCloaked;

    /// <summary>Longest time the window may stay cloaked if no frame is ever reported (e.g. software rendering stalls).</summary>
    internal static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(3);

    private IntPtr _handle;
    private bool _cloaked;
    private bool _revealed;

    /// <summary>True while the window is cloaked and not yet revealed.</summary>
    public bool IsHidden => _cloaked && !_revealed;

    /// <summary>Cloaks <paramref name="handle"/> unless this gate was already used (a window is cloaked at most once).</summary>
    public void Hide(IntPtr handle)
    {
        if (_revealed || _cloaked || handle == IntPtr.Zero) return;
        _handle = handle;
        _cloaked = SetCloaked(handle, true);
    }

    /// <summary>Uncloaks the window (once). A no-op when it was never cloaked.</summary>
    public void Reveal()
    {
        if (_revealed) return;
        _revealed = true;
        if (_cloaked) SetCloaked(_handle, false);
    }
}
