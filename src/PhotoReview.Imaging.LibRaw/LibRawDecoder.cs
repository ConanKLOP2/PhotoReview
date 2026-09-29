using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Full RAW decoder backed by the pinned LibRaw C API.</summary>
public sealed class LibRawDecoder : ICancellableImageDecoder
{
    private const int MaxThumbnailBytes = 32 << 20;
    private static readonly LibRawNativeMethods.ProgressCallback CancellationCallback = CheckCancellation;
    private static readonly SemaphoreSlim s_fullDecodeGate = new(1, 1);
    private readonly Action<string>? _stageObserver;

    /// <summary>Free slots of the single-slot gate that serialises full-resolution decodes (test seam).</summary>
    internal static int FullDecodeSlotsAvailable => s_fullDecodeGate.CurrentCount;

    public LibRawDecoder() { }

    /// <summary>Test seam: <paramref name="stageObserver"/> is told "opened", "unpacked" and "processed" as each native stage completes.</summary>
    internal LibRawDecoder(Action<string>? stageObserver) => _stageObserver = stageObserver;

    /// <summary>Extracts LibRaw's embedded JPEG thumbnail without demosaicing the sensor image.</summary>
    public static byte[] ReadJpegThumbnail(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var cancellationState = new CancellationState(cancellationToken);
        using var raw = CreateHandle();
        LibRawNativeMethods.LibRawSetProgressHandler(raw, CancellationCallback, cancellationState.Pointer);
        CheckResult(LibRawNativeMethods.LibRawOpenWFile(raw, path), "open RAW file", cancellationToken);
        CheckResult(LibRawNativeMethods.LibRawUnpackThumb(raw), "unpack embedded thumbnail", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var thumbnailPointer = LibRawNativeMethods.LibRawDcrawMakeMemThumb(raw, out var thumbnailError);
        if (thumbnailPointer == IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDecodeException(thumbnailError, "create embedded JPEG thumbnail");
        }

        using var thumbnail = new SafeLibRawImageHandle(thumbnailPointer);
        var header = Marshal.PtrToStructure<ProcessedImageHeader>(thumbnail.DangerousGetHandle());
        // LibRaw's memory wrapper leaves width/height as zero for JPEG thumbnails; validate the encoded
        // payload and let the existing JPEG decoder verify dimensions and structure.
        if (header.Type != LibRawNativeMethods.ImageJpeg || header.DataSize < 4 || header.DataSize > MaxThumbnailBytes)
            throw new InvalidDataException($"LibRaw returned an unsupported or oversized thumbnail (type={header.Type}, " +
                $"width={header.Width}, height={header.Height}, bytes={header.DataSize}).");

        var bytes = new byte[checked((int)header.DataSize)];
        Marshal.Copy(IntPtr.Add(thumbnail.DangerousGetHandle(), ProcessedImageHeader.DataOffset), bytes, 0, bytes.Length);
        var jpeg = TrimToEndOfImage(bytes);

        cancellationToken.ThrowIfCancellationRequested();
        return jpeg;
    }

    /// <summary>
    /// Validates the SOI marker and trims trailing fill (0x00 padding, extra 0xFF) after the final EOI marker.
    /// Camera thumbnails are often zero-padded to a fixed slot size. Data without an SOI or without an EOI once the
    /// fill is skipped is rejected as an incomplete stream.
    /// </summary>
    internal static byte[] TrimToEndOfImage(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new InvalidDataException("LibRaw thumbnail is not a complete JPEG stream.");
        var end = bytes.Length;
        while (end > 2 && bytes[end - 1] is 0x00 or 0xFF) end--;
        if (end < 4 || bytes[end - 1] != 0xD9 || bytes[end - 2] != 0xFF)
            throw new InvalidDataException("LibRaw thumbnail is not a complete JPEG stream.");
        return end == bytes.Length ? bytes : bytes[..end];
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = CreateHandle();
        CheckResult(LibRawNativeMethods.LibRawOpenWFile(handle, path), "open RAW file");
        CheckResult(LibRawNativeMethods.LibRawAdjustSizesInfoOnly(handle), "read RAW dimensions");
        // adjust_sizes_info_only applies the container flip to iwidth/iheight (a portrait file reports height > width),
        // exactly the size Decode returns, so the orientation is already folded in: report it as 1.
        // Pinned by ReadInfo_PortraitRewrittenCorpusFile_MatchesDecodedOrientedDimensions.
        var width = LibRawNativeMethods.LibRawGetIWidth(handle);
        var height = LibRawNativeMethods.LibRawGetIHeight(handle);
        if (width <= 0 || height <= 0) throw new InvalidDataException("LibRaw returned invalid image dimensions.");
        return new ImageInfo(width, height, 1);
    }

    public IDecodedImage Decode(DecodeRequest request) => Decode(request, CancellationToken.None);

    public IDecodedImage Decode(DecodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        if (!request.ApplyOrientation)
            throw new NotSupportedException("LibRaw always applies the RAW container's orientation; an un-oriented full decode is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();

        // A full-resolution decode holds LibRaw's working image, its 8-bit RGB output (3 B/px) and the managed BGRA copy
        // (4 B/px) at once, which is several hundred MB for a 60-100 MP sensor. Serialise those so parallel callers
        // (e.g. LibRaw as the general backend) cannot exhaust memory together; bounded decodes are not gated.
        var gated = !request.IsDownscaleRequested;
        if (gated) s_fullDecodeGate.Wait(cancellationToken);
        try
        {
            return DecodeCore(request, cancellationToken);
        }
        catch (OutOfMemoryException ex)
        {
            throw new InvalidOperationException("Not enough memory to decode this RAW image.", ex);
        }
        finally
        {
            if (gated) s_fullDecodeGate.Release();
        }
    }

    private WpfDecodedImage DecodeCore(DecodeRequest request, CancellationToken cancellationToken)
    {
        using var cancellationState = new CancellationState(cancellationToken);
        // libraw_open_buffer stores a pointer into the caller's buffer (no copy) that unpack/process read later, so the
        // pin must outlive the LibRaw handle: declared before `raw` so it is released after libraw_close.
        using var bufferPin = request.Bytes is { } bytes ? bytes.Pin() : default;
        using var raw = CreateHandle();
        LibRawNativeMethods.LibRawSetProgressHandler(raw, CancellationCallback, cancellationState.Pointer);
        OpenSource(raw, request, bufferPin, cancellationToken);
        _stageObserver?.Invoke("opened");

        LibRawNativeMethods.LibRawSetOutputColor(raw, 1); // sRGB
        LibRawNativeMethods.LibRawSetOutputBps(raw, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(raw, 1);
        CheckResult(LibRawNativeMethods.LibRawUnpack(raw), "unpack RAW data", cancellationToken);
        _stageObserver?.Invoke("unpacked");
        cancellationToken.ThrowIfCancellationRequested();
        CheckResult(LibRawNativeMethods.LibRawDcrawProcess(raw), "process RAW data", cancellationToken);
        _stageObserver?.Invoke("processed");
        cancellationToken.ThrowIfCancellationRequested();

        var imagePointer = LibRawNativeMethods.LibRawDcrawMakeMemImage(raw, out var imageError);
        if (imagePointer == IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDecodeException(imageError, "create decoded image");
        }
        using var image = new SafeLibRawImageHandle(imagePointer);

        var header = Marshal.PtrToStructure<ProcessedImageHeader>(image.DangerousGetHandle());
        if (header.Type != LibRawNativeMethods.ImageBitmap || header.Width == 0 || header.Height == 0 || header.Colors != 3 || header.Bits != 8)
            throw new InvalidDataException("LibRaw returned an unsupported processed image format.");

        var rgbLength = RgbBgraResampler.ValidateSourceLength(header.Width, header.Height, header.DataSize);
        var (targetWidth, targetHeight) = request.Box.Fit(header.Width, header.Height);
        var pixels = new byte[RgbBgraResampler.ValidateTargetLength(targetWidth, targetHeight)];
        unsafe
        {
            var source = new ReadOnlySpan<byte>((byte*)IntPtr.Add(image.DangerousGetHandle(), ProcessedImageHeader.DataOffset), rgbLength);
            RgbBgraResampler.Resize(source, header.Width, header.Height, pixels, targetWidth, targetHeight, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Bgr32, null, pixels, targetWidth * 4);
        var downscaled = targetWidth < header.Width || targetHeight < header.Height;
        return new WpfDecodedImage(bitmap, downscaled, actualBackend: DecoderBackend.LibRaw,
            originalWidth: header.Width, originalHeight: header.Height);
    }

    private static SafeLibRawHandle CreateHandle()
    {
        var pointer = LibRawNativeMethods.LibRawInit(0);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("LibRaw could not initialize its decoder.");
        return new SafeLibRawHandle(pointer);
    }

    private static unsafe void OpenSource(SafeLibRawHandle handle, DecodeRequest request, MemoryHandle bufferPin, CancellationToken cancellationToken)
    {
        if (request.Bytes is not { } bytes)
        {
            CheckResult(LibRawNativeMethods.LibRawOpenWFile(handle, request.Path), "open RAW file", cancellationToken);
            return;
        }

        CheckResult(LibRawNativeMethods.LibRawOpenBuffer(handle, (IntPtr)bufferPin.Pointer, checked((nuint)bytes.Length)), "open RAW buffer", cancellationToken);
    }

    private static int CheckCancellation(IntPtr data, int stage, int iteration, int expected)
    {
        try
        {
            return GCHandle.FromIntPtr(data).Target is CancellationState state && state.Token.IsCancellationRequested ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void CheckResult(int errorCode, string operation, CancellationToken cancellationToken = default)
    {
        if (errorCode != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDecodeException(errorCode, operation);
        }
    }

    private static InvalidDataException CreateDecodeException(int errorCode, string operation) =>
        new($"LibRaw could not {operation}: {LibRawNativeMethods.FormatError(errorCode)}");

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessedImageHeader
    {
        internal int Type;
        internal ushort Height;
        internal ushort Width;
        internal ushort Colors;
        internal ushort Bits;
        internal uint DataSize;
        internal const int DataOffset = 16;
    }

    private sealed class CancellationState : IDisposable
    {
        private readonly GCHandle _handle;

        internal CancellationState(CancellationToken token)
        {
            Token = token;
            _handle = GCHandle.Alloc(this);
        }

        internal CancellationToken Token { get; }
        internal IntPtr Pointer => GCHandle.ToIntPtr(_handle);
        public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
    }
}
