using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoReview.App.Services;

/// <summary>
/// Startup UX (perf/startup-first-image): a new window is shown before WPF has rendered anything into it, and that
/// unpainted surface is drawn white by the compositor (the "white flash" when a photo is opened from Explorer). The
/// window is therefore cloaked from the moment its HWND exists (<see cref="Hide"/>, in SourceInitialized, i.e. before
/// it is shown) until its first frame has been rendered: the second render tick after the window was shown
/// (<see cref="NoteShown"/>), the first tick's frame being done by then. ContentRendered (<see cref="Reveal"/>) is only
/// the fallback: WPF posts it at Input priority, so at startup it waits behind the folder load and the first image.
/// Cloaking does not delay anything else: the window is still shown, activated, laid out and rendered as before.
/// <see cref="Reveal"/> is idempotent and also runs on the safety timeout; <see cref="Stop"/> (window closed) drops the
/// timer and the static render-event subscription. When DWM refuses the cloak nothing changes.
/// UI thread only.
/// </summary>
internal sealed class StartupWindowReveal
{
    /// <summary>Test seam: (hwnd, cloaked) -> accepted. Production: DWMWA_CLOAK.</summary>
    internal static Func<IntPtr, bool, bool> SetCloaked { get; set; } = PhotoReview.Platform.Windows.WindowsDwmCloak.TrySetCloaked;

    /// <summary>Longest time the window may stay cloaked if no frame is ever reported (e.g. rendering stalls).</summary>
    internal static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Render ticks after the window was shown before it is revealed: the first tick's frame is rendered by the second.</summary>
    internal const int TicksBeforeReveal = 2;

    private readonly Action<EventHandler> _subscribeRendering;
    private readonly Action<EventHandler> _unsubscribeRendering;
    private readonly Action? _onRevealed;
    private readonly EventHandler _onRendering;
    private DispatcherTimer? _timer;
    private IntPtr _handle;
    private bool _cloaked;
    private bool _revealed;
    private bool _shown;
    private int _ticksSinceShown;

    /// <param name="subscribeRendering">Test seam; default <c>CompositionTarget.Rendering +=</c>.</param>
    /// <param name="unsubscribeRendering">Test seam; default <c>CompositionTarget.Rendering -=</c>.</param>
    /// <param name="onRevealed">Called once when a cloaked window is uncloaked.</param>
    public StartupWindowReveal(Action<EventHandler>? subscribeRendering = null, Action<EventHandler>? unsubscribeRendering = null, Action? onRevealed = null)
    {
        _subscribeRendering = subscribeRendering ?? (h => CompositionTarget.Rendering += h);
        _unsubscribeRendering = unsubscribeRendering ?? (h => CompositionTarget.Rendering -= h);
        _onRevealed = onRevealed;
        _onRendering = OnRendering;
    }

    /// <summary>True while the window is cloaked and not yet revealed.</summary>
    public bool IsHidden => _cloaked && !_revealed;

    /// <summary>Cloaks <paramref name="handle"/> unless this gate was already used (a window is cloaked at most once).</summary>
    public void Hide(IntPtr handle)
    {
        if (_revealed || _cloaked || handle == IntPtr.Zero) return;
        _handle = handle;
        _cloaked = SetCloaked(handle, true);
        if (!_cloaked) return;
        _timer = new DispatcherTimer { Interval = SafetyTimeout };
        _timer.Tick += (_, _) => Reveal();
        _timer.Start();
        _subscribeRendering(_onRendering);
    }

    /// <summary>The window has just been shown (SWP_SHOWWINDOW): render ticks from now on count towards the reveal.</summary>
    public void NoteShown() => _shown = true;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_shown) return;
        if (++_ticksSinceShown >= TicksBeforeReveal) Reveal();
    }

    /// <summary>Uncloaks the window (once). A no-op when it was never cloaked.</summary>
    public void Reveal()
    {
        Stop();
        if (_revealed) return;
        _revealed = true;
        if (!_cloaked) return;
        SetCloaked(_handle, false);
        _onRevealed?.Invoke();
    }

    /// <summary>Drops the safety timer and the static render-event subscription (they would keep a closed window alive).</summary>
    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        if (_cloaked) _unsubscribeRendering(_onRendering);
    }
}
