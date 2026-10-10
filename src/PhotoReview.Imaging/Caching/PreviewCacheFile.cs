using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Pixels;

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

    /// <summary>
    /// Decoded contents of a preview cache entry. <see cref="Pixels"/> is a fresh buffer the caller owns: hand it to
    /// <see cref="IPlatformImageCodec.FromPixels"/> (which takes ownership) or dispose it.
    /// </summary>
    // WP-04: pixels, not a BitmapSource -- PhotoReview.Imaging.Caching no longer uses any WPF type (the WPF bitmap is made by
    // the injected IPlatformImageCodec, see PreviewImageService / ReadAsDecodedImage).
    internal readonly record struct ReadResult(PixelBuffer Pixels, DecoderBackend ActualBackend, int Orientation, long FileBytes, int OriginalWidth, int OriginalHeight,
        ExifSummary? Exif = null);

    /// <summary>
    /// Encodes an already-decoded preview, taking its pixels through the WPF bridge codec (WP-04 temporary default: the WPF
    /// app's previews are BitmapSources; WP-06 removes this overload when the codec becomes a required dependency).
    /// </summary>
    public static Task WriteAtomicallyAsync(IDecodedImage image, string cachePath, CancellationToken cancellationToken = default)
        => WriteAtomicallyAsync(image, WpfCacheImageCodec.Instance, cachePath, cancellationToken);

    /// <summary>Encodes an already-decoded preview whose platform image <paramref name="codec"/> understands (C-02).</summary>
    public static async Task WriteAtomicallyAsync(IDecodedImage image, IPlatformImageCodec codec, string cachePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(codec);
        using var lease = codec.ToPixels(image.PlatformImage);
        await WriteAtomicallyAsync(lease.Pixels, image.ActualBackend, image.Orientation, image.OriginalWidth, image.OriginalHeight, cachePath,
            exif: image.Exif, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an entry back as an <see cref="IDecodedImage"/> through the WPF bridge codec (see the write overload).</summary>
    public static IDecodedImage ReadAsDecodedImage(string cachePath) => ReadAsDecodedImage(cachePath, WpfCacheImageCodec.Instance);

    /// <summary>Reads an entry back as an <see cref="IDecodedImage"/> whose platform image comes from <paramref name="codec"/>.</summary>
    public static IDecodedImage ReadAsDecodedImage(string cachePath, IPlatformImageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return ToDecodedImage(Read(cachePath), codec);
    }

    /// <summary>
    /// Wraps a <see cref="Read"/> result as a downscaled preview: the codec takes ownership of the pixels (and on failure they
    /// are released here, never leaked).
    /// </summary>
    internal static IDecodedImage ToDecodedImage(ReadResult entry, IPlatformImageCodec codec)
    {
        var pixels = entry.Pixels;
        var (width, height) = (pixels.Width, pixels.Height);
        object platformImage;
        try
        {
            platformImage = codec.FromPixels(pixels);
        }
        catch
        {
            pixels.Dispose(); // safe even if the codec already disposed it
            throw;
        }
        return new DecodedImage(platformImage, width, height, estimatedBytes: (long)width * height * 4, downscaled: true,
            orientation: entry.Orientation, actualBackend: entry.ActualBackend, originalWidth: entry.OriginalWidth,
            originalHeight: entry.OriginalHeight, exif: entry.Exif);
    }

    /// <summary>
    /// Atomically encodes <paramref name="pixels"/> as a current-version (<see cref="CurrentVersion"/>) cache entry (header + JPEG payload) via
    /// <see cref="AtomicCacheFile.WriteAsync"/> (temp file, write, atomic rename -- see that type for
    /// the durability rationale). The alpha/orientation validation below runs before the write. The JPEG comes from WIC
    /// (<see cref="WicImageEncoder"/>) with exactly the parameters WPF's JpegBitmapEncoder used, so entries are byte-identical to
    /// the pre-WP-04 ones and both builds read each other's caches. <paramref name="pixels"/> is only read (not disposed).
    /// </summary>
    internal static async Task WriteAtomicallyAsync(
        PixelBuffer pixels,
        DecoderBackend actualBackend,
        int orientation,
        int originalWidth,
        int originalHeight,
        string cachePath,
        bool opacityVerified = false,
        ExifSummary? exif = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        // IMG-01: JPEG cannot carry alpha; refuse so a future caller cannot silently flatten transparency.
        // Q-R7: alpha-capable buffers are accepted only when every pixel is actually opaque.
        if (!opacityVerified && !PixelOps.IsFullyOpaque(pixels)) throw new ArgumentException("Bitmaps with transparent pixels cannot be stored in the JPEG preview cache.", nameof(pixels));
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "EXIF orientation must be 1-8.");

        // A caller that doesn't know the original (pre-downscale) source size yet reports 0/0
        // here; fall back to this entry's own pixel dimensions rather than persisting a
        // header that claims "no original size known" (0 would round-trip as "unknown" and
        // force a real ReadInfo later, defeating the point of storing it at all).
        var headerOriginalWidth = originalWidth > 0 ? originalWidth : pixels.Width;
        var headerOriginalHeight = originalHeight > 0 ? originalHeight : pixels.Height;
        var header = BuildHeader(actualBackend, orientation, pixels.Width, pixels.Height, headerOriginalWidth, headerOriginalHeight);
        // v7: EXIF block (2-byte length, 0 = none, then the encoded summary) between header and payload.
        var exifBytes = ExifSummaryCodec.Encode(exif);
        var exifLength = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(exifLength, (ushort)exifBytes.Length);

        await AtomicCacheFile.WriteAsync(cachePath, stream =>
        {
            stream.Write(header);
            stream.Write(exifLength);
            stream.Write(exifBytes);
            WicImageEncoder.EncodeJpeg(pixels, stream, DefaultJpegQuality);
        }, log: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and fully decodes a cache entry in one file open (no separate metadata file to
    /// read). Throws <see cref="InvalidDataException"/> for a bad magic, an unsupported/mismatched
    /// version, any other structurally invalid header, or a payload that does not decode to the header's size -- callers
    /// treat that exactly like a corrupt payload (delete the entry, fall back to decoding the source).
    /// </summary>
    internal static ReadResult Read(string cachePath)
    {
        using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
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
        if (payloadLength > int.MaxValue)
            throw new InvalidDataException("Preview cache entry payload is implausibly large.");

        // WP-04: the payload is read once into a pooled buffer and WIC decodes it straight from memory (IWICStream over the
        // buffer) -- no MemoryStream copy as BitmapImage needed (it always decoded from its stream's absolute beginning, so the
        // header could not stay in front of it), and still a single file open/read.
        var payloadBuffer = ArrayPool<byte>.Shared.Rent((int)payloadLength);
        try
        {
            var payload = payloadBuffer.AsSpan(0, (int)payloadLength);
            stream.ReadExactly(payload);
            // The entry has no length or checksum, and the JPEG decoder turns a truncated JPEG into a partly grey picture instead
            // of failing, so a cut-off entry would be served (and kept in RAM) as a valid preview. Every payload the encoder writes
            // ends with the EOI marker; a payload without it was cut short (or is not a JPEG at all).
            if (payload.Length < 4 || payload[^2] != 0xFF || payload[^1] != 0xD9)
                throw new InvalidDataException("Preview cache entry payload is truncated (no JPEG end-of-image marker).");

            // Render-native output (D-item 3): Bgr32 (or Pbgra32 for a future alpha payload), the formats the renderer presents
            // without converting -- the same two the WPF read path produced.
            PixelBuffer pixels;
            try
            {
                pixels = WicCacheImageReader.Decode(payload, hasAlpha ? PixelLayout.Pbgra32 : PixelLayout.Bgr32, DecodeBox.Unbounded,
                    width, height, out _);
            }
            catch (COMException ex)
            {
                throw new InvalidDataException("Preview cache entry payload could not be decoded.", ex);
            }

            return new ReadResult(pixels, backendValue, orientation, fileBytes, originalWidth, originalHeight, exif);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
    }

    /// <summary>
    /// True when <paramref name="image"/> can carry transparency (an alpha-capable pixel format, or a palette with a
    /// non-opaque colour) -- such previews go through the JPEG cache only once every pixel is verified opaque (Q-R7).
    /// </summary>
    public static bool HasAlpha(IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return WpfCacheImageCodec.CanCarryAlpha(image.PlatformImage);
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
