using System.Globalization;
using System.Text;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>W2CM-04: idle flush, writer-fault accounting and Dispose timeout of <see cref="PerfCsvListener"/>.</summary>
[Collection("GlobalState")] // the listener subscribes to the process-wide PhotoReviewPerf EventSource
public sealed class PerfCsvListenerFaultTests
{
    private static PerfCsvListener.Row Row() => PerfCsvListener.BuildRow("Evt", ["a"], [1L]);

    private sealed class ProbeStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly ManualResetEventSlim? _blockFirstWrite;
        private int _blocked;
        private long _length;

        public ProbeStream(ManualResetEventSlim? blockFirstWrite = null) => _blockFirstWrite = blockFirstWrite;

        public volatile bool Fail;
        public ManualResetEventSlim Entered { get; } = new(false);
        public long Written => Interlocked.Read(ref _length);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { if (Fail) throw new IOException("disk full (simulated)"); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail) throw new IOException("disk full (simulated)");
            if (_blockFirstWrite is not null && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.Set();
                _blockFirstWrite.Wait(TimeSpan.FromSeconds(30));
            }

            _inner.Write(buffer, offset, count);
            Interlocked.Add(ref _length, count);
        }

        public string Text() => Encoding.UTF8.GetString(_inner.ToArray());
    }

    private static long Trailer(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(l => l.StartsWith("# dropped=", StringComparison.Ordinal));
        return long.Parse(line["# dropped=".Length..].Trim(), CultureInfo.InvariantCulture);
    }

    [Fact(DisplayName = "W2CM-04: rows are flushed after an idle interval without waiting for Dispose")]
    public void IdleWriter_FlushesTheTailOfABurst()
    {
        var stream = new ProbeStream();
        var listener = PerfCsvListener.StartForTest(stream, capacity: 100, idleFlush: TimeSpan.FromMilliseconds(50));
        try
        {
            listener.Enqueue(Row);

            Assert.True(SpinWait.SpinUntil(() => stream.Written > 0, TimeSpan.FromSeconds(20)), "the burst tail was never flushed while idle");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact(DisplayName = "W2CM-04: rows abandoned after a writer fault are counted in the dropped trailer")]
    public void WriterFault_CountsEveryAbandonedRow()
    {
        var stream = new ProbeStream { Fail = true };
        var listener = PerfCsvListener.StartForTest(stream, capacity: 1000);
        const int emitted = 200;
        for (var i = 0; i < emitted; i++) listener.Enqueue(Row);

        Assert.True(SpinWait.SpinUntil(() => listener.WriterFaulted, TimeSpan.FromSeconds(20)), "the writer never faulted");
        stream.Fail = false;
        listener.Dispose();

        Assert.Equal(emitted, Trailer(stream.Text()));
    }

    [Fact(DisplayName = "W2CM-04: a Dispose timeout does not write the trailer while the writer task is mid-write")]
    public async Task DisposeTimeout_DefersTheTrailerUntilTheWriterTaskEnds()
    {
        using var gate = new ManualResetEventSlim(false);
        var stream = new ProbeStream(gate);
        var listener = PerfCsvListener.StartForTest(stream, capacity: 1000, disposeWait: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 200; i++) listener.Enqueue(Row);
        Assert.True(stream.Entered.Wait(TimeSpan.FromSeconds(20)), "the writer never reached the stream");

        listener.Dispose(); // times out: the writer task is blocked inside its first stream write
        var duringWrite = stream.Written;

        gate.Set();
        await listener.FinishTask.WaitAsync(TimeSpan.FromSeconds(20));
        var text = stream.Text();

        Assert.Equal(0, duringWrite);
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("# dropped=", StringComparison.Ordinal)));
        Assert.StartsWith("# dropped=", text.TrimEnd().Split('\n').Last(), StringComparison.Ordinal);
    }
}
