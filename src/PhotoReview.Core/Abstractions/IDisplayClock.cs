using System;

namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Refresh timing of a display, in <c>Stopwatch</c> (QPC) ticks: the latest vertical blank and the refresh period.
/// Later vblanks are <c>LastVBlank + n * RefreshPeriod</c>.
/// </summary>
public readonly record struct DisplayTiming(long LastVBlank, long RefreshPeriod);

/// <summary>Reads the refresh timing of the monitor a window is on; null when unavailable.</summary>
public interface IDisplayClock
{
    /// <summary>
    /// Timing of the monitor that shows <paramref name="window"/> (an HWND). Non-blocking; the first calls after the
    /// window moved to another monitor (or after a pause) can return null until the clock has observed a few vblanks.
    /// </summary>
    DisplayTiming? GetTiming(IntPtr window);
}

/// <summary>
/// Estimates a display's vblank grid from observed vblank timestamps (QPC ticks taken right after a vblank wait
/// returned, so each is the true vblank plus a non-negative wake-up delay, and a busy observer can miss vblanks).
/// Pure; any refresh rate. The period is the observed span divided by the number of refreshes in it; the phase is the
/// earliest projection of the recent samples onto the latest vblank, which discards late wake-ups.
/// </summary>
public sealed class VBlankEstimator
{
    /// <summary>Samples kept (about half a second at 60 Hz).</summary>
    public const int Capacity = 32;

    /// <summary>Samples needed before a timing is reported.</summary>
    public const int MinSamples = 4;

    private readonly long[] _samples = new long[Capacity];
    private int _count;
    private int _next;

    public int Count => _count;

    public void Reset()
    {
        _count = 0;
        _next = 0;
        Current = null;
    }

    /// <summary>Records a vblank observed at <paramref name="timestamp"/> (QPC ticks, increasing).</summary>
    public void Add(long timestamp)
    {
        if (_count > 0 && timestamp <= _samples[(_next - 1 + Capacity) % Capacity]) return;
        _samples[_next] = timestamp;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        DropSamplesBeforeARateChange();
        Current = Compute();
    }

    /// <summary>The estimated grid (recomputed on <see cref="Add"/>), or null with fewer than <see cref="MinSamples"/> samples.</summary>
    public DisplayTiming? Current { get; private set; }

    private DisplayTiming? Compute()
    {
        {
            if (_count < MinSamples) return null;
            var intervals = new long[_count - 1];
            for (var i = 0; i < intervals.Length; i++) intervals[i] = Sample(i + 1) - Sample(i);
            var sorted = (long[])intervals.Clone();
            Array.Sort(sorted);
            // Lower quartile: most intervals are one refresh; a missed vblank gives ~2 refreshes, a late wake-up
            // followed by an on-time one gives slightly less than one, so the lower quartile is a safe base.
            var baseline = (double)sorted[sorted.Length / 4];
            if (baseline <= 0) return null;
            long refreshes = 0;
            var counts = new long[intervals.Length];
            for (var i = 0; i < intervals.Length; i++)
            {
                counts[i] = Math.Max(1, (long)Math.Round(intervals[i] / baseline));
                refreshes += counts[i];
            }
            var period = (double)(Sample(_count - 1) - Sample(0)) / refreshes;
            // Project every sample onto the latest vblank; the earliest projection has the least wake-up delay.
            var latest = double.MaxValue;
            long ahead = 0;
            for (var i = _count - 1; i >= 0; i--)
            {
                latest = Math.Min(latest, Sample(i) + ahead * period);
                if (i > 0) ahead += counts[i - 1];
            }
            return new DisplayTiming((long)Math.Round(latest), (long)Math.Round(period));
        }
    }

    private long Sample(int index) => _samples[(_next - _count + index + Capacity) % Capacity];

    /// <summary>
    /// A refresh-rate change (or a monitor switch without a reset) shows as several consecutive intervals that fit
    /// neither the old period nor a whole multiple of it; keep only the samples after the change.
    /// </summary>
    private void DropSamplesBeforeARateChange()
    {
        const int Recent = 4;
        if (_count < Recent + MinSamples) return;
        var olderSpan = Sample(_count - Recent - 1) - Sample(0);
        var olderIntervals = _count - Recent - 1;
        var older = (double)olderSpan / olderIntervals;
        for (var i = _count - Recent; i < _count; i++)
        {
            var ratio = (Sample(i) - Sample(i - 1)) / older;
            var nearest = Math.Max(1, Math.Round(ratio));
            if (Math.Abs(ratio - nearest) < 0.15 * nearest) return;
        }
        var keep = Recent + 1;
        var kept = new long[keep];
        for (var i = 0; i < keep; i++) kept[i] = Sample(_count - keep + i);
        Reset();
        foreach (var sample in kept)
        {
            _samples[_next] = sample;
            _next = (_next + 1) % Capacity;
            _count++;
        }
    }
}
