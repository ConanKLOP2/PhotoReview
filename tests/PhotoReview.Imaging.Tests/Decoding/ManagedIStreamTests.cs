using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// <see cref="ManagedIStream"/> is the COM IStream WIC reads through: its Read/Seek must report exact byte counts and
/// positions through the out pointers (WIC trusts them), tolerate null out pointers, and refuse use after Dispose.
/// </summary>
public sealed unsafe class ManagedIStreamTests
{
    // The COM Read/Write take raw pointers (WP-12): the tests pin a managed array for the call.
    private static void Read(ManagedIStream com, byte[] buffer, int cb, nint pcbRead)
    {
        fixed (byte* p = buffer) com.Read(p, cb, pcbRead);
    }

    private static readonly byte[] Data = [10, 11, 12, 13, 14, 15, 16, 17];

    [Fact(DisplayName = "Read reports the actual byte count through pcbRead, including a short read at end of stream")]
    public void Read_ReportsBytesReadThroughPointer_IncludingShortReadAtEof()
    {
        using var stream = new MemoryStream(Data);
        using var com = new ManagedIStream(stream);
        var pcb = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            stream.Position = 6;
            var buffer = new byte[4];
            Read(com, buffer, 4, pcb);
            Assert.Equal(2, Marshal.ReadInt32(pcb));
            Assert.Equal([16, 17, 0, 0], buffer);

            Read(com, buffer, 4, pcb);
            Assert.Equal(0, Marshal.ReadInt32(pcb));
        }
        finally { Marshal.FreeHGlobal(pcb); }
    }

    [Fact(DisplayName = "Read and Seek accept a null out pointer (WIC passes NULL when it ignores the result)")]
    public void ReadAndSeek_NullOutPointer_StillAdvanceThePosition()
    {
        using var stream = new MemoryStream(Data);
        using var com = new ManagedIStream(stream);
        var buffer = new byte[3];

        Read(com, buffer, 3, IntPtr.Zero);
        Assert.Equal(3, stream.Position);
        Assert.Equal([10, 11, 12], buffer);

        com.Seek(1, (int)SeekOrigin.Begin, IntPtr.Zero);
        Assert.Equal(1, stream.Position);
    }

    [Theory(DisplayName = "Seek honours the STREAM_SEEK origin and reports the new absolute position")]
    [InlineData(SeekOrigin.Begin, 5L, 4L, 5L)]
    [InlineData(SeekOrigin.Current, 2L, 4L, 6L)]
    [InlineData(SeekOrigin.Current, -3L, 4L, 1L)]
    [InlineData(SeekOrigin.End, -2L, 4L, 6L)]
    [InlineData(SeekOrigin.End, 0L, 4L, 8L)]
    public void Seek_ReportsNewPosition(SeekOrigin origin, long move, long startPosition, long expected)
    {
        using var stream = new MemoryStream(Data) { Position = startPosition };
        using var com = new ManagedIStream(stream);
        var pos = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            com.Seek(move, (int)origin, pos);
            Assert.Equal(expected, Marshal.ReadInt64(pos));
            Assert.Equal(expected, stream.Position);
        }
        finally { Marshal.FreeHGlobal(pos); }
    }

    [Fact(DisplayName = "Seek before the start surfaces the stream's IOException instead of corrupting the position")]
    public void Seek_BeforeStart_Throws()
    {
        using var stream = new MemoryStream(Data) { Position = 2 };
        using var com = new ManagedIStream(stream);

        Assert.ThrowsAny<IOException>(() => com.Seek(-3, (int)SeekOrigin.Current, IntPtr.Zero));
        Assert.Equal(2, stream.Position);
    }

    [Fact(DisplayName = "Stat reports the stream length as a read-only stream")]
    public void Stat_ReportsLength()
    {
        using var stream = new MemoryStream(Data);
        using var com = new ManagedIStream(stream);

        com.Stat(out StatStgNative stat, 0);

        Assert.Equal(Data.Length, stat.cbSize);
        Assert.Equal(2, stat.type); // STGTY_STREAM
    }

    [Fact(DisplayName = "Every stream operation throws ObjectDisposedException after Dispose")]
    public void AfterDispose_OperationsThrowObjectDisposed()
    {
        using var stream = new MemoryStream(Data);
        var com = new ManagedIStream(stream);
        com.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Read(com, new byte[1], 1, IntPtr.Zero));
        Assert.Throws<ObjectDisposedException>(() => com.Seek(0, (int)SeekOrigin.Begin, IntPtr.Zero));
        Assert.Throws<ObjectDisposedException>(() => com.Stat(out _, 0));
        com.Commit(0); // tolerated: WIC may release/commit after the owner disposed it
    }

    [Fact(DisplayName = "Clone and CopyTo are unsupported")]
    public void CloneAndCopyTo_NotSupported()
    {
        using var stream = new MemoryStream(Data);
        using var com = new ManagedIStream(stream);

        Assert.Throws<NotSupportedException>(() => com.Clone(out _));
        Assert.Throws<NotSupportedException>(() => com.CopyTo(0, 1, IntPtr.Zero, IntPtr.Zero));
    }
}
