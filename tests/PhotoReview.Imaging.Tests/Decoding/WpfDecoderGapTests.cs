using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Mutation-gap tests for <see cref="WpfBitmapImageDecoder"/>, <see cref="ExifOrientation"/> and <see cref="WpfImageAdapter"/>.</summary>
public sealed class WpfDecoderGapTests : IDisposable
{
    private readonly TempRoot _root = new("wpf-gap");

    public void Dispose() => _root.Dispose();

    // ---- ReadOrientationOrDefault ----

    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(COMException))]
    public void ReadOrientationOrDefault_MetadataFault_IsOrientationOne(Type faultType)
    {
        var result = WpfBitmapImageDecoder.ReadOrientationOrDefault(() => throw (Exception)Activator.CreateInstance(faultType)!);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ReadOrientationOrDefault_ValueIsPassedThrough() =>
        Assert.Equal(6, WpfBitmapImageDecoder.ReadOrientationOrDefault(() => 6));

    [Fact]
    public void ReadOrientationOrDefault_OtherFault_Propagates() =>
        Assert.Throws<IOException>(() => WpfBitmapImageDecoder.ReadOrientationOrDefault(() => throw new IOException("disk")));

    // ---- decode streams ----

    private sealed class FaultStream(Stream inner, Func<Exception> fault) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw fault();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // IMG-R04: the wrapper owns the file stream it was given; leaking it keeps a handle on the fixture file open.
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Reader(Func<string, Stream> open) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) => open(path);
    }

    [Fact(DisplayName = "A source that keeps reporting E_INVALIDARG (with and without the colour profile) is invalid data, not a caller bug")]
    public void Decode_PersistentInvalidArgument_IsInvalidData()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("a.jpg"), 64, 48);
        var decoder = new WpfBitmapImageDecoder(new Reader(p => new FaultStream(File.OpenRead(p), () => new ArgumentException("E_INVALIDARG"))));

        var ex = Record.Exception(() => decoder.Decode(new DecodeRequest(path, 0)));

        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact(DisplayName = "Without orientation and without a downscale the header is not pre-read: no EXIF summary, no original size")]
    public void Decode_NoOrientationNoDownscale_SkipsTheHeaderPreRead()
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("d.jpg"), 64, 48, orientation: 6);
        var decoder = new WpfBitmapImageDecoder();

        var decoded = decoder.Decode(new DecodeRequest(path, 0, ApplyOrientation: false));

        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Null(decoded.Exif);
    }

    [Fact(DisplayName = "The header pre-read (and with it the EXIF summary) only runs when orientation or a downscale needs it")]
    public void Decode_ExifFixture_SummaryOnlyFromTheHeaderPreRead()
    {
        var path = _root.Combine("exif.jpg");
        File.WriteAllBytes(path, PhotoReview.Imaging.Tests.Metadata.ExifTestData.EncodeJpegWithExif());
        var decoder = new WpfBitmapImageDecoder();

        var withHeader = decoder.Decode(new DecodeRequest(path, 0, ApplyOrientation: true));
        var without = decoder.Decode(new DecodeRequest(path, 0, ApplyOrientation: false));

        Assert.NotNull(withHeader.Exif);
        Assert.Null(without.Exif);
    }

    // ---- ExifOrientation.Read range ----

    /// <summary>Rewrites the value of the EXIF orientation entry (tag 0x0112, SHORT, count 1) in place, in either byte order.</summary>
    private static void PatchOrientation(string path, ushort value)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 10 <= bytes.Length; i++)
        {
            if (bytes[i] == 0x01 && bytes[i + 1] == 0x12 && bytes[i + 2] == 0 && bytes[i + 3] == 3 && bytes[i + 7] == 1)
            {
                bytes[i + 8] = (byte)(value >> 8); bytes[i + 9] = (byte)value;
                File.WriteAllBytes(path, bytes);
                return;
            }
            if (bytes[i] == 0x12 && bytes[i + 1] == 0x01 && bytes[i + 2] == 3 && bytes[i + 3] == 0 && bytes[i + 4] == 1)
            {
                bytes[i + 8] = (byte)value; bytes[i + 9] = (byte)(value >> 8);
                File.WriteAllBytes(path, bytes);
                return;
            }
        }
        throw new InvalidOperationException("orientation entry not found");
    }

    [Theory]
    [InlineData((ushort)9)]
    [InlineData((ushort)0)]
    [InlineData((ushort)200)]
    public void Read_OutOfRangeOrientationTag_IsOne(ushort tag)
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("o" + tag + ".jpg"), 32, 24, 6);
        PatchOrientation(path, tag);
        using var stream = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

        Assert.Equal(1, ExifOrientation.Read(decoder.Frames[0].Metadata as System.Windows.Media.Imaging.BitmapMetadata));
    }
}
