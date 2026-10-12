using System.Diagnostics;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>WP21-STALL: nhật ký khung chẩn đoán (PHOTOREVIEW_FRAME_LOG), mặc định tắt; định dạng dòng là hợp đồng với tools/diag/stall-experiment.ps1.</summary>
public sealed class FrameLogTests
{
    [Fact]
    public void Format_IsTheDocumentedLine_InvariantCulture() =>
        Assert.Equal("F gap=250.3 frame=3.46 endDraw=1.20 present=0.99", FrameLog.Format(250.34, 3.456, 1.2, 0.987));

    [Fact]
    public void FromEnvironment_IsNullWhenUnsetOrBlank()
    {
        Assert.Null(FrameLog.FromEnvironment(_ => null));
        Assert.Null(FrameLog.FromEnvironment(_ => "  "));
    }

    [Fact]
    public void FromEnvironment_IsNullWhenThePathCannotBeOpened() =>
        Assert.Null(FrameLog.FromEnvironment(_ => Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "f.log")));

    [Fact]
    public void FromEnvironment_ReadsTheDocumentedVariable()
    {
        string? asked = null;
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var log = FrameLog.FromEnvironment(name =>
            {
                asked = name;
                return path;
            });

            Assert.NotNull(log);
            Assert.Equal("PHOTOREVIEW_FRAME_LOG", asked);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Record_WritesOneLinePerFrame_WithTheGapSincePreviousFrame()
    {
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var f = Stopwatch.Frequency;
            using (var log = new FrameLog(path))
            {
                log.Record(frameStart: 10 * f, frameEnd: 10 * f + (f / 100), endDrawMs: 1.5, presentMs: 0.5); // 10 ms
                log.Record(frameStart: 10 * f + (f / 4), frameEnd: 10 * f + (f / 4) + (f / 50), endDrawMs: 12, presentMs: 1); // +250 ms, 20 ms
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("F gap=0.0 frame=10.00 endDraw=1.50 present=0.50", lines[0]);
            Assert.Equal("F gap=250.0 frame=20.00 endDraw=12.00 present=1.00", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Record_IsFlushedImmediately_WhileTheLogIsStillOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var log = new FrameLog(path);
            log.Record(frameStart: 5, frameEnd: 5 + (Stopwatch.Frequency / 100), endDrawMs: 1, presentMs: 0.5);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Assert.StartsWith("F gap=0.0 frame=10.00", reader.ReadToEnd()); // công cụ đo đọc tệp khi tiến trình còn chạy
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewInstances_AppendToAnExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var first = new FrameLog(path))
            {
                first.Record(1, 2, 0, 0);
            }

            using (var second = new FrameLog(path))
            {
                second.Record(1, 2, 0, 0);
            }

            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Record_AfterDispose_IsANoOp()
    {
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var log = new FrameLog(path);
            log.Dispose();

            log.Record(1, 2, 0, 0);
            log.Dispose();

            Assert.Empty(File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
