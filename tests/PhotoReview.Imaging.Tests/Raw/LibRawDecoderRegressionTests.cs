using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderBufferPinTests
{
    private static readonly string SamplePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus/Canon - EOS 350D - RAW (3_2).CR2"));

    [Fact]
    public void Decode_WithRequestBytes_KeepsBufferPinnedThroughUnpackAndProcess()
    {
        if (!File.Exists(SamplePath)) return;
        using var manager = new PinTrackingMemoryManager(File.ReadAllBytes(SamplePath));
        var pinCounts = new List<(string Stage, int Pins)>();
        var decoder = new LibRawDecoder(stage => pinCounts.Add((stage, manager.ActivePins)));

        var image = decoder.Decode(new DecodeRequest(SamplePath, DecodeBox.Unbounded, bytes: manager.Memory));

        Assert.True(image.PixelWidth > 0);
        Assert.Equal(["opened", "unpacked", "processed"], pinCounts.Select(entry => entry.Stage));
        // libraw_open_buffer keeps a pointer into the buffer; it must stay pinned until the handle is closed.
        Assert.All(pinCounts, entry => Assert.Equal(1, entry.Pins));
        Assert.Equal(0, manager.ActivePins);
        Assert.Equal(1, manager.TotalPins);
    }

    private sealed unsafe class PinTrackingMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _buffer;
        private readonly int _length;
        private int _activePins;
        private int _totalPins;

        internal PinTrackingMemoryManager(byte[] content)
        {
            _length = content.Length;
            _buffer = (byte*)NativeMemory.Alloc((nuint)_length);
            content.CopyTo(new Span<byte>(_buffer, _length));
        }

        internal int ActivePins => Volatile.Read(ref _activePins);
        internal int TotalPins => Volatile.Read(ref _totalPins);

        public override Span<byte> GetSpan() => new(_buffer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            Interlocked.Increment(ref _activePins);
            Interlocked.Increment(ref _totalPins);
            return new MemoryHandle(_buffer + elementIndex, default, this);
        }

        public override void Unpin() => Interlocked.Decrement(ref _activePins);

        protected override void Dispose(bool disposing) => NativeMemory.Free(_buffer);
    }
}
