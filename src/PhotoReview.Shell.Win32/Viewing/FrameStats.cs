using System.Diagnostics;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-21: thời gian của các khung đã vẽ (vòng tròn 2048 khung gần nhất): tổng thời gian vẽ + Present, và riêng
/// <c>EndDrawAndPresent</c> (nơi <c>Present(1)</c> có thể chặn UI thread - xem NOWPF-WP20/WP21). Rẻ (hai
/// <c>Stopwatch.GetTimestamp</c> mỗi khung): luôn bật để probe/test đo được độ chặn thật. Chỉ dùng trên UI thread.
/// </summary>
internal sealed class FrameStats
{
    internal const int Capacity = 2048;

    private readonly double[] _frameMs = new double[Capacity];
    private readonly double[] _presentMs = new double[Capacity];
    private int _next;
    private int _count;

    /// <summary>Tổng số khung đã ghi (không giới hạn bởi <see cref="Capacity"/>).</summary>
    internal long Total { get; private set; }

    /// <summary>Số khung đang giữ (tối đa <see cref="Capacity"/>).</summary>
    internal int Count => _count;

    /// <summary>Khung <c>Present</c> mất &gt;= <see cref="BlockedThresholdMs"/> (UI thread bị chặn).</summary>
    internal long BlockedPresents { get; private set; }

    internal const double BlockedThresholdMs = 8.0;

    internal static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Khung chậm (Present &gt;= ngưỡng) gần nhất, kèm bóc tách: dữ liệu cho báo cáo hiệu năng.</summary>
    internal List<(double FrameMs, double DrawMs, double EndDrawMs, double PresentMs, string Note)> Slow { get; } = [];

    internal void RecordSlow(double frameMs, double drawMs, double endDrawMs, double presentMs, string note)
    {
        if (Slow.Count < 64 && (presentMs >= BlockedThresholdMs || endDrawMs >= BlockedThresholdMs))
        {
            Slow.Add((frameMs, drawMs, endDrawMs, presentMs, note));
        }
    }

    internal void Record(long frameTicks, long presentTicks)
    {
        var frame = ToMs(frameTicks);
        var present = ToMs(presentTicks);
        _frameMs[_next] = frame;
        _presentMs[_next] = present;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }

        Total++;
        if (present >= BlockedThresholdMs)
        {
            BlockedPresents++;
        }
    }

    internal void Reset()
    {
        _next = 0;
        _count = 0;
        Total = 0;
        BlockedPresents = 0;
    }

    /// <summary>Phân vị <paramref name="percentile"/> (0..100) của thời gian cả khung (ms); 0 khi chưa có khung.</summary>
    internal double FrameMs(double percentile) => Percentile(_frameMs, percentile);

    /// <summary>Phân vị của thời gian <c>EndDrawAndPresent</c> (ms).</summary>
    internal double PresentMs(double percentile) => Percentile(_presentMs, percentile);

    internal double MaxPresentMs()
    {
        var max = 0.0;
        for (var i = 0; i < _count; i++)
        {
            max = Math.Max(max, _presentMs[i]);
        }

        return max;
    }

    private double Percentile(double[] source, double percentile)
    {
        if (_count == 0)
        {
            return 0;
        }

        var copy = new double[_count];
        Array.Copy(source, copy, _count);
        Array.Sort(copy);
        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * _count) - 1;
        return copy[Math.Clamp(rank, 0, _count - 1)];
    }
}
