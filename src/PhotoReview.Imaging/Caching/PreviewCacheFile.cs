using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// perf(cache) v5 preview disk-cache entry: a single file (no separate ".meta" companion) whose
/// fixed 24-byte header carries everything <see cref="PreviewImageService"/> used to store in a
/// companion file (source orientation, decode backend, pixel dimensions, original source
/// dimensions) plus an explicit format version, followed immediately by the encoded payload
/// (JPEG bytes, to end of file).
/// </summary>
/// <remarks>
/// Format chosen by measurement (see <c>PreviewCacheFormatBenchmarkTests</c>, Manual category):
/// on a synthetic 2190x1460 photo-like image, JPEG q95 4:4:4 produced ~0.5-1 MB files decoding in
/// well under raw Bgr32's ~12.8 MB per file, at PSNR in the mid-30s dB (visually lossless for a
/// downscaled preview) -- the 4 GB disk-cache cap holds an order of magnitude more previews than
/// either the old 32-bit PNG format or a raw pixel dump. Header layout (all integers little-endian):
/// <code>
/// offset 0  (4 bytes) magic "PRVC"
/// offset 4  (1 byte)  format version (<see cref="CurrentVersion"/>; a mismatch makes the whole
///                     file a cache miss -- see <see cref="Read"/> -- so a version bump never
///                     needs to parse or migrate an older layout)
/// offset 5  (1 byte)  DecoderBackend that actually produced the pixels (may differ from the
///                     backend requested by the cache key when the source decode fell back --
///                     see PreviewImageService's disk-cache persistence)
/// offset 6  (1 byte)  EXIF orientation already baked into the pixels (1-8)
/// offset 7  (1 byte)  1 if the payload carries an alpha channel, else 0 (JPEG never does; this
///                     flag exists so a future payload format can add real alpha support without
///                     another version bump)
/// offset 8  (4 bytes) pixel width
/// offset 12 (4 bytes) pixel height
/// offset 16 (4 bytes) original (full source, post-orientation) pixel width -- perf: lets a later
///                     "original dimensions" lookup for this source be served from this disk-cache
///                     entry without a decoder ReadInfo call/extra file open (see
///                     PreviewImageService.GetOriginalDimensionsAsync)
/// offset 20 (4 bytes) original (full source, post-orientation) pixel height
/// offset 24 (2 bytes) v7+: length N of the EXIF block (0 = none) -- see <see cref="CurrentVersion"/>
/// offset 26 (N bytes) v7+: EXIF summary (Metadata.ExifSummaryCodec)
/// then ...            payload bytes (currently always a JPEG-encoded frame) to end of file
///                     (v6: the payload starts right at offset 24)
/// </code>
/// </remarks>
public static class PreviewCacheFile
{
    /// <summary>
    /// preview-v6: bumped from v5 so entries written by builds that flattened alpha (transparent PNG/WebP baked to
    /// opaque black, R2-A-02) are treated as stale and re-decoded once. v5 added the original source dimensions.
    /// </summary>
    /// <remarks>
    /// preview-v7 (photo information line): after the fixed header comes a 2-byte little-endian length N (0 = no EXIF)
    /// and N bytes of <see cref="Metadata.ExifSummaryCodec"/> data, then the payload. v6 entries (same header, payload
    /// straight after it) are still READ, as "no EXIF": re-decoding them from source only to learn EXIF would cost one
    /// source read per cached image (AGENTS.md priority 1). They are replaced by v7 entries as the cache turns over,
    /// or at once after "Clear cache". Malformed EXIF bytes in a v7 entry also just mean "no EXIF", never a rejected entry.
    /// </remarks>
    public const int CurrentVersion = 7;

    /// <summary>Previous layout, still readable (no EXIF block) -- see <see cref="CurrentVersion"/>.</summary>
    internal const int NoExifVersion = 6;

    /// <summary>JPEG quality chosen by measurement -- see the format decision in the type doc.</summary>
    public const int DefaultJpegQuality = 95;

    private const int HeaderSize = 24;
    private static ReadOnlySpan<byte> MagicBytes => "PRVC"u8;

    /// <summary>Decoded contents of a v5 preview cache entry, ready to hand to <see cref="WpfDecodedImage"/>.</summary>
    // Internal, not public: architecture rule K-1 forbids public types in PhotoReview.Imaging.Caching
    // from exposing System.Windows.Media.* on their public surface (see
    // ImagingPublicSurfaceTests.Imaging_CachingAndPreload_PublicMembers_DoNotExpose_SystemWindowsMediaTypes).
    // PreviewImageService (same assembly) uses this directly; a caller outside the assembly goes
    // through the IDecodedImage-based WriteAtomicallyAsync/ReadAsDecodedImage overloads below,
    // exactly like DiskCacheStore's public IDecodedImage overload vs. its internal BitmapSource one.
    internal readonly record struct ReadResult(BitmapSource Bitmap, DecoderBackend ActualBackend, int Orientation, long FileBytes, int OriginalWidth, int OriginalHeight,
        ExifSummary? Exif = null);

    /// <summary>Public, framework-agnostic entry point: encodes an already-decoded preview.</summary>
    public static Task WriteAtomicallyAsync(IDecodedImage image, string cachePath, int jpegQuality = DefaultJpegQuality, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PlatformImage is not BitmapSource bitmap)
            throw new ArgumentException("PlatformImage must be a BitmapSource for the preview cache.", nameof(image));
        return WriteAtomicallyAsync(bitmap, image.ActualBackend, image.Orientation, image.OriginalWidth, image.OriginalHeight, cachePath, jpegQuality, exif: image.Exif, cancellationToken: cancellationToken);
    }

    /// <summary>Public, framework-agnostic entry point: reads a v5 entry back as an <see cref="IDecodedImage"/>.</summary>
    public static IDecodedImage ReadAsDecodedImage(string cachePath)
    {
        var result = Read(cachePath);
        return new WpfDecodedImage(result.Bitmap, downscaled: true, orientation: result.Orientation, actualBackend: result.ActualBackend,
            originalWidth: result.OriginalWidth, originalHeight: result.OriginalHeight, exif: result.Exif);
    }

    /// <summary>
    /// Atomically encodes <paramref name="bitmap"/> as a v4 cache entry (header + JPEG payload) via a
    /// temporary file and rename. Unlike <see cref="DiskCacheStore.WriteAtomicallyAsync(System.Windows.Media.Imaging.BitmapSource,string,System.Threading.CancellationToken)"/>,
    /// this never opens the file with <c>FileOptions.WriteThrough</c> or calls <c>Flush(true)</c>:
    /// this is a disposable cache, not a durability-critical journal, so the atomic
    /// temp-file-then-rename is the only guarantee that matters (a crash never leaves a
    /// half-written file at <paramref name="cachePath"/>).
    /// </summary>
    internal static async Task WriteAtomicallyAsync(
        BitmapSource bitmap,
        DecoderBackend actualBackend,
        int orientation,
        int originalWidth,
        int originalHeight,
        string cachePath,
        int jpegQuality = DefaultJpegQuality,
        bool opacityVerified = false,
        ExifSummary? exif = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        // IMG-01: JPEG cannot carry alpha; refuse so a future caller cannot silently flatten transparency.
        // Q-R7: alpha-capable formats are accepted only when every pixel is actually opaque.
        if (!opacityVerified && !IsFullyOpaque(bitmap)) throw new ArgumentException("Bitmaps with transparent pixels cannot be stored in the JPEG preview cache.", nameof(bitmap));
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "EXIF orientation must be 1-8.");

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // A caller that doesn't know the original (pre-downscale) source size yet reports 0/0
            // here; fall back to this entry's own pixel dimensions rather than persisting a
            // header that claims "no original size known" (0 would round-trip as "unknown" and
            // force a real ReadInfo later, defeating the point of storing it at all).
            var headerOriginalWidth = originalWidth > 0 ? originalWidth : bitmap.PixelWidth;
            var headerOriginalHeight = originalHeight > 0 ? originalHeight : bitmap.PixelHeight;
            var header = BuildHeader(actualBackend, orientation, bitmap.PixelWidth, bitmap.PixelHeight, headerOriginalWidth, headerOriginalHeight);
            // v7: EXIF block (2-byte length, 0 = none, then the encoded summary) between header and payload.
            var exifBytes = ExifSummaryCodec.Encode(exif);
            var exifLength = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(exifLength, (ushort)exifBytes.Length);
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                stream.Write(header);
                stream.Write(exifLength);
                stream.Write(exifBytes);
                var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            DiskCacheStore.TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// Reads and fully decodes a v4 cache entry in one file open (no separate metadata file to
    /// read). Throws <see cref="InvalidDataException"/> for a bad magic, an unsupported/mismatched
    /// version, or any other structurally invalid header -- callers treat that exactly like a
    /// corrupt payload (delete the entry, fall back to decoding the source).
    /// </summary>
    internal static ReadResult Read(string cachePath)
    {
        using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual(MagicBytes))
            throw new InvalidDataException("Preview cache entry has an invalid header magic.");

        var version = header[4];
        if (version != CurrentVersion && version != NoExifVersion)
            throw new InvalidDataException($"Preview cache entry is version {version.ToString(System.Globalization.CultureInfo.InvariantCulture)}, expected {CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

        var backendValue = (DecoderBackend)header[5];
        if (!Enum.IsDefined(backendValue))
            throw new InvalidDataException("Preview cache entry has an invalid decoder backend byte.");

        var orientation = header[6];
        if (orientation is < 1 or > 8)
            throw new InvalidDataException("Preview cache entry has an invalid orientation byte.");

        var hasAlpha = header[7] != 0;
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Preview cache entry has invalid pixel dimensions.");

        var originalWidth = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        var originalHeight = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
        if (originalWidth <= 0 || originalHeight <= 0)
            throw new InvalidDataException("Preview cache entry has invalid original dimensions.");

        ExifSummary? exif = null;
        var exifBlockSize = 0;
        if (version == CurrentVersion)
        {
            if (stream.Length - HeaderSize < 2)
                throw new InvalidDataException("Preview cache entry has no payload.");
            Span<byte> exifLength = stackalloc byte[2];
            stream.ReadExactly(exifLength);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(exifLength);
            if (length > ExifSummaryCodec.MaxEncodedLength || length > stream.Length - HeaderSize - 2)
                throw new InvalidDataException("Preview cache entry has an oversized EXIF block.");
            if (length > 0)
            {
                Span<byte> exifBytes = stackalloc byte[length];
                stream.ReadExactly(exifBytes);
                exif = ExifSummaryCodec.Decode(exifBytes); // malformed = no EXIF; the pixels are still good
            }
            exifBlockSize = 2 + length;
        }

        var fileBytes = stream.Length;
        var payloadLength = fileBytes - HeaderSize - exifBlockSize;
        if (payloadLength <= 0)
            throw new InvalidDataException("Preview cache entry has no payload.");

        // BitmapImage always decodes its StreamSource from that stream's absolute beginning
        // (it seeks back to 0 internally, e.g. to read ColorContexts during FinalizeCreation) --
        // handing it the still-open FileStream positioned right after the header would make it
        // decode the header bytes themselves as if they were the JPEG. Copying just the payload
        // (still a single file open/read) into a fresh, independently-zero-based stream sidesteps
        // that without a second file open.
        if (payloadLength > int.MaxValue)
            throw new InvalidDataException("Preview cache entry payload is implausibly large.");
        using var payloadStream = new MemoryStream((int)payloadLength);
        stream.CopyTo(payloadStream);
        // The entry has no length or checksum, and WPF decodes a truncated JPEG into a partly grey picture instead of failing,
        // so a cut-off entry would be served (and kept in RAM) as a valid preview. Every payload the encoder writes ends with
        // the EOI marker; a payload without it was cut short (or is not a JPEG at all).
        var payload = payloadStream.GetBuffer().AsSpan(0, (int)payloadLength);
        if (payload.Length < 4 || payload[^2] != 0xFF || payload[^1] != 0xD9)
            throw new InvalidDataException("Preview cache entry payload is truncated (no JPEG end-of-image marker).");
        payloadStream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = payloadStream;
        bitmap.EndInit();

        if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            throw new InvalidDataException("Preview cache entry payload dimensions do not match its header.");

        // Render-native output (D-item 3): the disk-cache read path used to hand back whatever
        // format BitmapImage produced (Bgra32 for a PNG decode); converting once here means the
        // renderer never has to format-convert this bitmap on every present.
        var targetFormat = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        BitmapSource native = bitmap.Format == targetFormat ? bitmap : new FormatConvertedBitmap(bitmap, targetFormat, null, 0);
        native.Freeze();

        return new ReadResult(native, backendValue, orientation, fileBytes, originalWidth, originalHeight, exif);
    }

    /// <summary>
    /// True when <paramref name="bmp"/> can carry transparency (alpha pixel format, or an indexed
    /// format whose palette has a non-opaque color). Such previews must not go through the JPEG cache.
    /// </summary>
    /// <summary>Framework-agnostic form of <see cref="HasAlpha(BitmapSource)"/> for a decoded preview.</summary>
    public static bool HasAlpha(IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.PlatformImage is BitmapSource bitmap && HasAlpha(bitmap);
    }

    internal static bool HasAlpha(BitmapSource bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        var format = bmp.Format;
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64
            || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
            return true;
        if (format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8)
            return bmp.Palette?.Colors.Any(c => c.A < 255) == true;
        return false;
    }

    /// <summary>
    /// Q-R7: true when <paramref name="bmp"/> can be written to the JPEG cache without losing
    /// transparency. Formats that cannot carry alpha are opaque without a scan; Bgra32/Pbgra32 are
    /// scanned (band by band through a small pooled buffer, vectorised, early exit on the first
    /// A&lt;255 pixel). For fully opaque pixels premultiplied equals straight colour, so writing
    /// them is colour-safe. Other alpha formats (16-bit/float) are conservatively treated as not opaque.
    /// </summary>
    internal static bool IsFullyOpaque(BitmapSource bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        if (!HasAlpha(bmp)) return true;
        var format = bmp.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32) return false;

        var width = bmp.PixelWidth;
        var height = bmp.PixelHeight;
        if (width <= 0 || height <= 0) return true;
        var stride = width * 4;
        var rowsPerBand = Math.Clamp(65536 / stride, 1, height);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(rowsPerBand * stride);
        try
        {
            for (var y = 0; y < height; y += rowsPerBand)
            {
                var rows = Math.Min(rowsPerBand, height - y);
                bmp.CopyPixels(new System.Windows.Int32Rect(0, y, width, rows), buffer, stride, 0);
                if (!AllAlphaOpaque(buffer.AsSpan(0, rows * stride))) return false;
            }
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>True when every 4th byte (alpha of BGRA) of <paramref name="pixels"/> is 255.</summary>
    internal static bool AllAlphaOpaque(ReadOnlySpan<byte> pixels)
    {
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels);
        const uint AlphaMask = 0xFF000000u; // little-endian: byte 3 is the top byte
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && px.Length >= System.Numerics.Vector<uint>.Count)
        {
            var mask = new System.Numerics.Vector<uint>(AlphaMask);
            var last = px.Length - System.Numerics.Vector<uint>.Count;
            for (; i <= last; i += System.Numerics.Vector<uint>.Count)
            {
                var v = new System.Numerics.Vector<uint>(px.Slice(i));
                if (!System.Numerics.Vector.EqualsAll(v & mask, mask)) return false;
            }
        }
        for (; i < px.Length; i++)
            if ((px[i] & AlphaMask) != AlphaMask) return false;
        return true;
    }

    private static byte[] BuildHeader(DecoderBackend actualBackend, int orientation, int width, int height, int originalWidth, int originalHeight)
    {
        var header = new byte[HeaderSize];
        MagicBytes.CopyTo(header);
        header[4] = CurrentVersion;
        header[5] = (byte)actualBackend;
        header[6] = (byte)orientation;
        header[7] = 0; // JPEG payload never carries alpha -- see the type doc.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), originalWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), originalHeight);
        return header;
    }
}
