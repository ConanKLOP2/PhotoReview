using System.Reflection;
using System.Text;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>Payload-to-column mapping, drop accounting, flush cadence and header of <see cref="PerfCsvListener"/> (mutation-testing gaps).</summary>
[Collection("GlobalState")] // listens to the process-wide PhotoReviewPerf EventSource / reads PHOTOREVIEW_DIAG_* variables
public sealed class PerfCsvListenerMappingTests
{
    private static PerfCsvListener.Row Build(string[]? names, object?[]? values) =>
        PerfCsvListener.BuildRow("Evt", names, values);

    [Fact(DisplayName = "nav and gen fill the nav column once; pathId fills the pathId column once; the rest goes to a..d / text in order")]
    public void BuildRow_MapsNamedAndPositionalPayload()
    {
        var row = Build(
            ["first", "nav", "pathId", "second", "third", "name", "fourth"],
            [1L, 42L, "abc123", 2.5, true, "hello", 7]);

        Assert.Equal("Evt", row.EventName);
        Assert.Equal("42", row.Nav);
        Assert.Equal("abc123", row.PathId);
        Assert.Equal("1", row.A);
        Assert.Equal("2.5", row.B);
        Assert.Equal("1", row.C);       // bool -> 1
        Assert.Equal("7", row.D);
        Assert.Equal("hello", row.Text);
    }

    [Fact(DisplayName = "gen is accepted as the nav column when nav is absent")]
    public void BuildRow_GenFillsNav()
    {
        var row = Build(["gen", "phase"], [9L, "scanned"]);

        Assert.Equal("9", row.Nav);
        Assert.Equal("scanned", row.Text);
        Assert.Equal("", row.A);
    }

    [Fact(DisplayName = "A second nav/gen or pathId does not overwrite the first; it is treated as an ordinary value")]
    public void BuildRow_SecondNavOrPathId_IsOrdinaryValue()
    {
        var row = Build(["nav", "gen", "pathId", "pathId"], [10L, 20L, "p1", "p2"]);

        Assert.Equal("10", row.Nav);
        Assert.Equal("p1", row.PathId);
        Assert.Equal("20", row.A);
        Assert.Equal("p2", row.Text);
    }

    [Fact(DisplayName = "Fields that are not named nav/gen/pathId never land in the nav or pathId column")]
    public void BuildRow_OtherNamesNeverFillNavOrPathId()
    {
        var row = Build(["slot", "label", "ms"], [3, "x", 1.5]);

        Assert.Equal("", row.Nav);
        Assert.Equal("", row.PathId);
        Assert.Equal("3", row.A);
        Assert.Equal("1.5", row.B);
        Assert.Equal("x", row.Text);
    }

    [Fact(DisplayName = "Only the first four numeric values get a column; extra ones are ignored without error")]
    public void BuildRow_MoreThanFourNumerics_AreCappedAtFour()
    {
        var row = Build(["a", "b", "c", "d", "e", "f"], [1, 2, 3, 4, 5, 6]);

        Assert.Equal(["1", "2", "3", "4"], new[] { row.A, row.B, row.C, row.D });
        Assert.Equal("", row.Text);
    }

    [Fact(DisplayName = "Null values are skipped, strings are kept verbatim and joined with ';' in order")]
    public void BuildRow_TextValues_SkipNullsAndJoin()
    {
        var row = Build(["t1", "t2", "t3", "t4"], ["alpha", null, "beta", "gamma"]);

        Assert.Equal("alpha;beta;gamma", row.Text);
        Assert.Equal("", row.A);
    }

    [Fact(DisplayName = "Names and values of different length are paired up to the shorter list")]
    public void BuildRow_MismatchedLengths_UseTheShorterList()
    {
        var moreNames = Build(["a", "b", "c"], [1, 2]);
        var moreValues = Build(["a", "b"], [1, 2, 3]);

        Assert.Equal(("1", "2", ""), (moreNames.A, moreNames.B, moreNames.C));
        Assert.Equal(("1", "2", ""), (moreValues.A, moreValues.B, moreValues.C));
    }

    [Fact(DisplayName = "A missing names list or values list yields an empty row instead of throwing")]
    public void BuildRow_NullLists_YieldEmptyRow()
    {
        var noNames = Build(null, [1, 2]);
        var noValues = Build(["a"], null);

        Assert.Equal(("", "", "", ""), (noNames.Nav, noNames.A, noNames.B, noNames.Text));
        Assert.Equal(("", "", "", ""), (noValues.Nav, noValues.A, noValues.B, noValues.Text));
    }

    [Fact(DisplayName = "Doubles round-trip with 17 digits, other formattables use the invariant culture, bool is 0/1")]
    public void BuildRow_FormatsValuesInvariantly()
    {
        var row = Build(["a", "b", "c"], [0.1, false, 1234567890123L]);

        Assert.Equal("0.10000000000000001", row.A);
        Assert.Equal("0", row.B);
        Assert.Equal("1234567890123", row.C);
    }

    [Fact(DisplayName = "A payload that cannot be formatted is counted as dropped, never thrown into event delivery")]
    public void Enqueue_BuilderThrows_IsCountedAsDropped()
    {
        var listener = PerfCsvListener.StartForTest(new MemoryStream(), capacity: 10);
        try
        {
            var ex = Record.Exception(() => listener.Enqueue(() => throw new InvalidOperationException("bad payload")));

            Assert.Null(ex);
            Assert.Equal(1, listener.DroppedCount);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact(DisplayName = "A row arriving after the channel was completed is counted as dropped and not written")]
    public void Enqueue_AfterDispose_IsCountedAsDropped()
    {
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(backing, capacity: 10);
        listener.Dispose();
        var length = backing.ToArray().Length;

        listener.Enqueue(() => Build(["a"], [1]));

        Assert.Equal(1, listener.DroppedCount);
        Assert.Equal(length, backing.ToArray().Length);
    }

    [Fact(DisplayName = "A row queued before the writer finishes is written and not counted as dropped")]
    public void Enqueue_Normal_IsWrittenNotDropped()
    {
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(backing, capacity: 10);

        listener.Enqueue(() => Build(["a"], [1]));
        listener.Dispose();

        Assert.Equal(0, listener.DroppedCount);
        var text = Encoding.UTF8.GetString(backing.ToArray());
        Assert.Contains(",Evt,,,1,,,,\n", text, StringComparison.Ordinal);
        Assert.Contains("# dropped=0", text, StringComparison.Ordinal);
    }

    // A bounded timed wait on a never-signalled event: the moment being waited out (Flush starting to poll, the 1 s flush
    // interval elapsing) has no observable signal.
    private static void Pause(int milliseconds)
    {
        using var never = new ManualResetEventSlim(false);
        never.Wait(milliseconds);
    }

    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private int _flushes;
        public int Flushes => Volatile.Read(ref _flushes);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { Interlocked.Increment(ref _flushes); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    [Fact(DisplayName = "Within the first second the writer does not flush after every row (only on dispose)")]
    public void Writer_DoesNotFlushPerRow()
    {
        var stream = new CountingStream();
        var listener = PerfCsvListener.StartForTest(stream, capacity: 1000);

        for (var i = 0; i < 50; i++) listener.Enqueue(() => Build(["a"], [1]));
        listener.Dispose();

        Assert.True(stream.Flushes < 10, $"flushes={stream.Flushes}");
    }

    [Fact(DisplayName = "A row written more than a second after the last flush triggers a flush while the listener is still running")]
    public void Writer_FlushesAfterOneSecond()
    {
        var stream = new CountingStream();
        var listener = PerfCsvListener.StartForTest(stream, capacity: 1000);
        try
        {
            Pause(1200);
            var before = stream.Flushes;

            listener.Enqueue(() => Build(["a"], [1]));

            Assert.True(SpinWait.SpinUntil(() => stream.Flushes > before, TimeSpan.FromSeconds(10)), "no periodic flush after 1 s");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact(DisplayName = "The header names the entry-assembly version and the PHOTOREVIEW_DIAG_* variables in effect")]
    public void Header_ContainsVersionAndDiagFlags()
    {
        var previous = Environment.GetEnvironmentVariable(DiagOptions.PreReadVar);
        var backing = new MemoryStream();
        try
        {
            Environment.SetEnvironmentVariable(DiagOptions.PreReadVar, "1");
            PerfCsvListener.StartForTest(backing, capacity: 10).Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DiagOptions.PreReadVar, previous);
        }

        var expectedVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var header = Encoding.UTF8.GetString(backing.ToArray()).Split('\n')[0];
        Assert.StartsWith($"# commit={expectedVersion} diag={DiagOptions.PreReadVar}=1 qpcFrequency=", header, StringComparison.Ordinal);
    }
}
