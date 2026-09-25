using System;

namespace PhotoReview.App.Input;

/// <summary>
/// feat/mouse-zoom: release-velocity estimate for a drag-pan. Keeps the last <see cref="Capacity"/> pointer
/// samples in a fixed ring buffer (no allocation per mouse move) and measures the velocity over the last
/// <see cref="WindowMs"/> before release.
/// </summary>
/// <remarks>
/// The release point is added as a final sample at the release time, so a pointer that rested before the
/// button went up (no MouseMove while resting) yields a proportionally smaller velocity and, after a pause
/// longer than the window, none at all.
/// </remarks>
internal sealed class PanVelocityTracker
{
    public const int Capacity = 32;

    /// <summary>Velocity window before release, in ms.</summary>
    public const double WindowMs = 80;

    /// <summary>A span shorter than this (ms) is too short to measure a velocity from.</summary>
    public const double MinSpanMs = 8;

    private readonly double[] _time = new double[Capacity];
    private readonly double[] _x = new double[Capacity];
    private readonly double[] _y = new double[Capacity];
    private int _count;
    private int _next;

    public int Count => _count;

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Records the pointer at (<paramref name="x"/>, <paramref name="y"/>) DIP at <paramref name="timeMs"/>.</summary>
    public void Add(double timeMs, double x, double y)
    {
        _time[_next] = timeMs;
        _x[_next] = x;
        _y[_next] = y;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Pointer velocity (DIP/ms) over the samples within <see cref="WindowMs"/> of the newest one; zero when
    /// fewer than two samples fall in the window or they span less than <see cref="MinSpanMs"/>.
    /// </summary>
    public (double X, double Y) GetVelocity()
    {
        if (_count < 2) return (0, 0);
        var newest = (_next - 1 + Capacity) % Capacity;
        var newestTime = _time[newest];
        var oldest = newest;
        for (var i = 1; i < _count; i++)
        {
            var index = (newest - i + Capacity) % Capacity;
            if (newestTime - _time[index] > WindowMs) break;
            oldest = index;
        }
        var span = newestTime - _time[oldest];
        if (oldest == newest || span < MinSpanMs) return (0, 0);
        return ((_x[newest] - _x[oldest]) / span, (_y[newest] - _y[oldest]) / span);
    }
}

/// <summary>Scroll state of the viewer on one axis pair, as the kinetic step reads it.</summary>
internal readonly record struct ScrollBounds(double ExtentWidth, double ExtentHeight, double ViewportWidth, double ViewportHeight)
{
    public double MaxHorizontal => Math.Max(0, ExtentWidth - ViewportWidth);
    public double MaxVertical => Math.Max(0, ExtentHeight - ViewportHeight);
}

/// <summary>
/// feat/mouse-zoom: inertial scrolling after a drag-pan is released. Pure and time-driven: the caller passes
/// the elapsed time of each frame, so tests drive it without a clock. Velocities are SCROLL-OFFSET velocities
/// in DIP/ms (the opposite sign of the pointer's, since dragging right scrolls left).
/// </summary>
/// <remarks>
/// Friction is exponential: v(t) = v0 * e^(-t / <see cref="TimeConstantMs"/>). Each step integrates it exactly
/// (distance = v * tau * (1 - e^(-dt/tau))), so the glide covers the same distance at 30, 60 or 144 Hz; the
/// total glide distance is v0 * tau. An axis that reaches an edge of the image stops (its velocity becomes 0);
/// the glide ends when both axes are below <see cref="StopVelocity"/>.
/// </remarks>
internal struct KineticScroller
{
    /// <summary>Friction time constant (ms): velocity falls to 1/e in this time.</summary>
    public const double TimeConstantMs = 325;

    /// <summary>Release speeds below this (DIP/ms = 1000 DIP/s) do not start a glide.</summary>
    public const double StartVelocity = 0.1;

    /// <summary>The glide stops when the speed on both axes falls below this (DIP/ms).</summary>
    public const double StopVelocity = 0.02;

    /// <summary>Release speed cap (DIP/ms), against a stray sample making the image fly off.</summary>
    public const double MaxVelocity = 8;

    /// <summary>A frame gap longer than this (ms, e.g. the window was busy) is integrated as this much.</summary>
    public const double MaxFrameMs = 100;

    public double VelocityX { get; private set; }
    public double VelocityY { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>
    /// Starts a glide from a POINTER release velocity (DIP/ms); returns false (and stays idle) when the release
    /// was too slow to glide.
    /// </summary>
    public bool Start(double pointerVelocityX, double pointerVelocityY)
    {
        var vx = Math.Clamp(-Finite(pointerVelocityX), -MaxVelocity, MaxVelocity);
        var vy = Math.Clamp(-Finite(pointerVelocityY), -MaxVelocity, MaxVelocity);
        if (Math.Sqrt(vx * vx + vy * vy) < StartVelocity)
        {
            Stop();
            return false;
        }
        VelocityX = vx;
        VelocityY = vy;
        IsActive = true;
        return true;
    }

    public void Stop()
    {
        VelocityX = 0;
        VelocityY = 0;
        IsActive = false;
    }

    /// <summary>
    /// Advances the glide by <paramref name="elapsedMs"/> from the current offsets and returns the new, clamped
    /// offsets. Becomes inactive when both axes have stopped (edge or friction).
    /// </summary>
    public (double Horizontal, double Vertical) Step(double elapsedMs, double horizontalOffset, double verticalOffset, ScrollBounds bounds)
    {
        if (!IsActive) return (horizontalOffset, verticalOffset);
        var dt = Math.Clamp(Finite(elapsedMs), 0, MaxFrameMs);
        var decay = Math.Exp(-dt / TimeConstantMs);
        var travel = TimeConstantMs * (1 - decay);

        var horizontal = Advance(horizontalOffset, VelocityX, travel, bounds.MaxHorizontal, out var hitX);
        var vertical = Advance(verticalOffset, VelocityY, travel, bounds.MaxVertical, out var hitY);
        VelocityX = hitX ? 0 : VelocityX * decay;
        VelocityY = hitY ? 0 : VelocityY * decay;
        if (Math.Abs(VelocityX) < StopVelocity) VelocityX = 0;
        if (Math.Abs(VelocityY) < StopVelocity) VelocityY = 0;
        if (VelocityX == 0 && VelocityY == 0) IsActive = false;
        return (horizontal, vertical);
    }

    private static double Advance(double offset, double velocity, double travel, double max, out bool hitEdge)
    {
        var target = Finite(offset) + velocity * travel;
        var clamped = Math.Clamp(target, 0, max);
        // Stopped at an edge (also when already there and still pushing into it: the target is then past it).
        hitEdge = velocity != 0 && clamped != target;
        return clamped;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
