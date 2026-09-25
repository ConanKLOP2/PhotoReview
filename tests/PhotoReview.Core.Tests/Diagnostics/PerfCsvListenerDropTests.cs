using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>CORE-03: with BoundedChannelFullMode.DropWrite, TryWrite reports success for discarded rows; they must still be counted.</summary>
[Collection("GlobalState")]
public sealed class PerfCsvListenerDropTests
{
    private sealed class GatedStream(MemoryStream inner, ManualResetEventSlim gate) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { gate.Wait(TimeSpan.FromSeconds(30)); inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { gate.Wait(TimeSpan.FromSeconds(30)); inner.Write(buffer, offset, count); }
    }

    [Fact(DisplayName = "CORE-03: rows discarded by a full DropWrite channel are counted and reported in the trailer")]
    public void DroppedRowsAreCountedAndReported()
    {
        using var gate = new ManualResetEventSlim(false);
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(new GatedStream(backing, gate), capacity: 4);

        // The writer blocks on its first buffer flush, so the 4-slot channel fills and the rest is discarded.
        const int emitted = 3000;
        for (var i = 0; i < emitted; i++) PhotoReviewPerf.Log.PreloadPaused(85, 512);

        var droppedBeforeRelease = listener.DroppedCount;
        gate.Set();
        listener.Dispose();

        // The writer can consume at most a few dozen rows (one StreamWriter buffer) before blocking; everything else was dropped.
        Assert.True(droppedBeforeRelease >= emitted - 500, $"dropped={droppedBeforeRelease}");
        var text = System.Text.Encoding.UTF8.GetString(backing.ToArray());
        var trailer = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.StartsWith("# dropped=", StringComparison.Ordinal));
        Assert.True(long.Parse(trailer["# dropped=".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture) >= emitted - 500, trailer);
    }
}
