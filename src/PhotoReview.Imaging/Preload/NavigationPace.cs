using System.Diagnostics;

namespace PhotoReview.Imaging.Preload;

/// <summary>
/// perf(preload): tracks how the user is moving through the catalog -- the travel direction and an
/// EWMA of the interval between single-step navigations -- so preload can (a) prioritize the side the
/// user is heading to and (b) during a key-held burst, start decodes far enough ahead that they finish
/// when the user arrives instead of burning workers on images the user passes before they finish.
/// Thread-safe; every member takes one short lock.
/// </summary>
public sealed class NavigationPace
{
    /// <summary>Weight of the newest inter-key interval in the EWMA.</summary>
    public const double IntervalAlpha = 0.3;

    /// <summary>A gap longer than this ends a run of steps (the EWMA restarts from the next interval).</summary>
    public const double MaxStepIntervalMs = 500;

    /// <summary>Consecutive short intervals needed before a run counts as a burst (a double-tap is not one).</summary>
    public const int MinBurstSamples = 2;

    /// <summary>Upper bound on how many images a burst may skip ahead.</summary>
    public const int MaxLead = 24;

    /// <summary>A burst is over once no key arrived for max(2 x interval, this floor).</summary>
    public const double BurstIdleFloorMs = 150;

    /// <summary>Upper bound on the viewer-decode start delay during a burst.</summary>
    public const double MaxViewerStartDelayMs = 100;

    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private int _lastIndex = -1;
    private long _lastTicks;
    private double _intervalMs;
    private int _samples;
    private int _direction = 1;

    /// <param name="timestamp">Stopwatch-tick clock; tests pass a fake one.</param>
    public NavigationPace(Func<long>? timestamp = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    /// <summary>+1 when the last move went to a higher index (Next), -1 for a lower one (Prev).</summary>
    public int Direction { get { lock (_gate) return _direction; } }

    /// <summary>
    /// Records a navigation to <paramref name="index"/>. Idempotent for the index already recorded,
    /// so the post-present preload kick for the same navigation never counts as a second key.
    /// </summary>
    public void Record(int index)
    {
        if (index < 0) return;
        lock (_gate)
        {
            if (index == _lastIndex) return;
            var now = _timestamp();
            if (_lastIndex >= 0)
            {
                var delta = index - _lastIndex;
                _direction = delta > 0 ? 1 : -1;
                var elapsedMs = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
                // Only single steps held/pressed in quick succession form a burst; a jump (Home, a
                // sibling folder, a file action landing elsewhere) or a pause restarts the estimate.
                if (Math.Abs(delta) == 1 && elapsedMs <= MaxStepIntervalMs)
                {
                    _intervalMs = _samples == 0 ? elapsedMs : _intervalMs + IntervalAlpha * (elapsedMs - _intervalMs);
                    _samples++;
                }
                else
                {
                    _intervalMs = 0;
                    _samples = 0;
                }
            }
            _lastIndex = index;
            _lastTicks = now;
        }
    }

    /// <summary>
    /// How many images ahead of the current one a burst should start preloading: about
    /// keyRate x decodeTime (= decodeMs / interval) when that exceeds 1, else 0. Also 0 once the
    /// burst is over (no key for max(2 x interval, <see cref="BurstIdleFloorMs"/>)).
    /// </summary>
    public int GetLead(double decodeMs)
    {
        lock (_gate)
        {
            var interval = ActiveIntervalMs();
            if (interval <= 0 || decodeMs <= 0) return 0;
            var ratio = decodeMs / interval;
            return ratio <= 1 ? 0 : Math.Min(MaxLead, (int)Math.Ceiling(ratio));
        }
    }

    /// <summary>
    /// During a burst, the viewer's own decode for a not-yet-cached image is superseded by the next key
    /// long before it could finish; waiting slightly longer than one key interval before starting it
    /// means those decodes are dropped instead of started, while the image the burst stops on starts
    /// at most <see cref="MaxViewerStartDelayMs"/> late. Zero outside a burst.
    /// </summary>
    public TimeSpan GetViewerStartDelay(double decodeMs)
    {
        lock (_gate)
        {
            var interval = ActiveIntervalMs();
            if (interval <= 0 || decodeMs <= interval) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(Math.Min(MaxViewerStartDelayMs, interval * 1.5));
        }
    }

    // Caller holds _gate.
    private double ActiveIntervalMs()
    {
        if (_samples < MinBurstSamples || _intervalMs <= 0) return 0;
        var sinceLastMs = (_timestamp() - _lastTicks) * 1000.0 / Stopwatch.Frequency;
        return sinceLastMs > Math.Max(2 * _intervalMs, BurstIdleFloorMs) ? 0 : _intervalMs;
    }
}
