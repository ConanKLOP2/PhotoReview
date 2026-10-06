using System.Globalization;
using System.Text;
using PhotoReview.Core.Diagnostics;
using PhotoReview.TestSupport;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>
/// Behaviour of <see cref="PerfCsvListener"/> that the other perf CSV suites leave open: the environment-variable start-up path end to end,
/// the diagnostic-flag header, rows arriving after a writer fault, and disposing a listener that never started or whose stream stays broken.
/// </summary>
[Collection("GlobalState")] // the listener subscribes to the process-wide PhotoReviewPerf EventSource; the tests also set process env vars
public sealed class PerfCsvListenerStartupAndFaultCoverageTests
{
    private const string TraceVar = "PHOTOREVIEW_PERF_TRACE";

    private static PerfCsvListener.Row Row() => PerfCsvListener.BuildRow("Evt", ["a"], [1L]);

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("disk full (simulated)");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full (simulated)");
    }

    private static T WithEnv<T>(string name, string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact(DisplayName = "TryStartFromEnvironment creates the trace directory, names the file perf-<pid>-<timestamp>.csv and records a real event")]
    public void TryStartFromEnvironment_ValidDirectory_WritesAWellFormedTraceFile()
    {
        using var temp = new TempRoot("perf-env");
        var traceDir = Path.Combine(temp.Path, "nested", "trace"); // does not exist yet

        WithEnv(TraceVar, traceDir, () =>
        {
            using (var listener = PerfCsvListener.TryStartFromEnvironment())
            {
                Assert.NotNull(listener);
                PhotoReviewPerf.Log.KeyInput(42, "Right", 12.5);
            } // Dispose drains the channel and writes the trailer
            return 0;
        });

        var file = Assert.Single(Directory.GetFiles(traceDir));
        var name = Path.GetFileName(file);
        Assert.Matches(
            "^perf-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + @"-\d{8}-\d{6}\.csv$", name);
        var lines = File.ReadAllText(file, Encoding.UTF8).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("# commit=", lines[0], StringComparison.Ordinal);
        Assert.Equal("utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text", lines[1]);
        Assert.Contains(lines, l => !l.StartsWith('#') && l.Split(',') is { Length: 11 } f && f[3] == "KeyInput" && f[6] == "12.5");
        Assert.Equal("# dropped=0", lines[^1]);
    }

    [Fact(DisplayName = "The header lists only the diagnostic flags that are set, in a fixed order")]
    public void Header_ListsOnlyTheDiagnosticFlagsThatAreSet()
    {
        var text = WithEnv(DiagOptions.PreloadWorkersVar, "3", () =>
            WithEnv(DiagOptions.DisableDiskCacheVar, "1", () =>
                WithEnv(DiagOptions.PreReadVar, null, () =>
                {
                    var backing = new MemoryStream();
                    PerfCsvListener.StartForTest(backing, capacity: 10).Dispose();
                    return Encoding.UTF8.GetString(backing.ToArray());
                })));

        var header = text.Split('\n')[0];
        Assert.Contains($" diag={DiagOptions.PreloadWorkersVar}=3;{DiagOptions.DisableDiskCacheVar}=1 ", header, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagOptions.PreReadVar, header, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "With no diagnostic flag set the header carries an empty diag field")]
    public void Header_NoDiagnosticFlags_EmptyDiagField()
    {
        var text = WithEnv(DiagOptions.PreloadWorkersVar, null, () =>
            WithEnv(DiagOptions.DisableDiskCacheVar, null, () =>
                WithEnv(DiagOptions.PreReadVar, "", () =>
                {
                    var backing = new MemoryStream();
                    PerfCsvListener.StartForTest(backing, capacity: 10).Dispose();
                    return Encoding.UTF8.GetString(backing.ToArray());
                })));

        Assert.Contains(" diag= ", text.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rows enqueued after the writer faulted are counted as dropped, not queued forever; Dispose survives a still-broken stream")]
    public void EnqueueAfterWriterFault_CountsEachRowAsDropped_AndDisposeStaysQuiet()
    {
        var listener = PerfCsvListener.StartForTest(new FailingStream(), capacity: 1000);
        const int before = 200;
        for (var i = 0; i < before; i++) listener.Enqueue(Row);
        Assert.True(SpinWait.SpinUntil(() => listener.WriterFaulted, TimeSpan.FromSeconds(20)), "the writer never faulted");
        Assert.Equal(before, listener.DroppedCount); // every abandoned row is counted once (W2CM-04)

        for (var i = 0; i < 5; i++) listener.Enqueue(Row);

        Assert.Equal(before + 5, listener.DroppedCount);
        Assert.Null(Record.Exception(listener.Dispose)); // the trailer cannot be written to the broken stream: best effort, no throw
    }

    [Fact(DisplayName = "A listener that was never started drops events silently and disposes cleanly")]
    public void NeverStartedListener_IgnoresEnqueueAndDisposes()
    {
        var listener = new PerfCsvListener();

        listener.Enqueue(Row);

        Assert.Equal(0, listener.DroppedCount); // nothing was queued, so nothing counts as lost
        Assert.False(listener.WriterFaulted);
        Assert.Null(Record.Exception(listener.Dispose));
        Assert.Null(Record.Exception(listener.Dispose));
    }
}
