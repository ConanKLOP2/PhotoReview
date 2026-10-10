using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Input;

// C-05 (NO-WPF-EXEC-PLAN mục 5): đồng hồ khung hình. Thực thi Win32 ở WP-14 (waitable object của swap chain).

/// <summary>Một nhịp khung. <see cref="Timestamp"/> = Stopwatch ticks.</summary>
public readonly record struct FrameTick(long Timestamp, double RenderingTimeMs, DisplayTiming? Timing);

public sealed class FrameTickEventArgs(FrameTick tick) : EventArgs
{
    public FrameTick Tick { get; } = tick;
}

public interface IFrameClock
{
    /// <summary>
    /// Phát trên UI thread, một lần mỗi khung sắp được trình bày, khi có ít nhất một subscriber hoặc
    /// <see cref="RequestFrame"/> đang chờ.
    /// </summary>
    event EventHandler<FrameTickEventArgs>? Frame;

    void RequestFrame();
}
