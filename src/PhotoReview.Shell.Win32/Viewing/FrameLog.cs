using System.Diagnostics;
using System.Globalization;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP21-STALL: nhật ký từng khung (chẩn đoán, mặc định TẮT): đặt <c>PHOTOREVIEW_FRAME_LOG=&lt;tệp&gt;</c> thì mỗi khung được ghi một dòng
/// <c>F gap=&lt;ms từ khung trước&gt; frame=&lt;tổng ms&gt; endDraw=&lt;ms&gt; present=&lt;ms&gt;</c> (nối tiếp vào tệp). Dùng bởi
/// <c>tools/diag/stall-experiment.ps1</c> để đếm tần suất khung bị chặn; ghi lỗi (đĩa đầy, khoá) thì tự tắt, không ảnh hưởng vẽ.
/// </summary>
internal sealed class FrameLog : IDisposable
{
    internal const string Variable = "PHOTOREVIEW_FRAME_LOG";

    private StreamWriter? _writer;
    private long _lastFrame;

    internal FrameLog(string path)
    {
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    /// <summary>Mở theo biến môi trường; null khi không đặt hoặc không mở được tệp.</summary>
    internal static FrameLog? FromEnvironment(Func<string, string?>? get = null)
    {
        var path = (get ?? Environment.GetEnvironmentVariable)(Variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new FrameLog(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string Format(double gapMs, double frameMs, double endDrawMs, double presentMs) =>
        string.Create(CultureInfo.InvariantCulture, $"F gap={gapMs:F1} frame={frameMs:F2} endDraw={endDrawMs:F2} present={presentMs:F2}");

    /// <summary>Ghi một khung: <paramref name="frameStart"/>/<paramref name="frameEnd"/> là Stopwatch ticks.</summary>
    internal void Record(long frameStart, long frameEnd, double endDrawMs, double presentMs)
    {
        if (_writer is null)
        {
            return;
        }

        var gapMs = _lastFrame == 0 ? 0 : FrameStats.ToMs(frameStart - _lastFrame);
        _lastFrame = frameStart;
        try
        {
            _writer.WriteLine(Format(gapMs, FrameStats.ToMs(frameEnd - frameStart), endDrawMs, presentMs));
        }
        catch (IOException)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
