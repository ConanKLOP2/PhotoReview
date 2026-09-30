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
    /// <summary>Viewer decodes jump ahead of queued preloads; at most this many preload decodes may wait (extra ones are skipped with <see cref="DecoderBusyException"/>).</summary>
    internal const int MaxQueuedPreloadDecodes = 2;
    private static readonly FullDecodeGate s_fullDecodeGate = new(MaxQueuedPreloadDecodes);
    private readonly Action<string>? _stageObserver;
    private readonly bool _configureAfterOpen;

    /// <summary>Free slots of the single-slot gate that serialises LibRaw decodes (test seam).</summary>
    internal static int FullDecodeSlotsAvailable => s_fullDecodeGate.SlotsAvailable;

    /// <summary>Test seam: decodes currently waiting for the full-decode slot (both lanes).</summary>
    internal static int FullDecodeQueuedWaiters => s_fullDecodeGate.QueuedViewers + s_fullDecodeGate.QueuedPreloads;

    public LibRawDecoder() { }

    /// <summary>Test seam: <paramref name="stageObserver"/> is told "opened", "unpacked" and "processed" as each native stage completes.</summary>
    internal LibRawDecoder(Action<string>? stageObserver) => _stageObserver = stageObserver;

    /// <summary>
    /// Test seam reproducing the pre-fix order: <paramref name="configureAfterOpen"/> applies the output settings and
    /// use_camera_wb after libraw_open (as before), so a test can measure what the open-time order changes. Also reports "configured".
    /// </summary>
    internal LibRawDecoder(Action<string>? stageObserver, bool configureAfterOpen)
    {
        _stageObserver = stageObserver;
        _configureAfterOpen = configureAfterOpen;
    }

    /// <summary>
    /// Extracts LibRaw's embedded JPEG thumbnail without demosaicing the sensor image. Deliberately NOT behind the full-decode gate:
    /// no demosaic runs, memory is bounded by the thumbnail (at most <see cref="MaxThumbnailBytes"/>) so parallel extraction is cheap.
    /// </summary>
    public static byte[] ReadJpegThumbnail(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var cancellationState = new CancellationState(cancellationToken);
        using var raw = CreateHandle();
        LibRawNativeMethods.LibRawSetProgressHandler(raw, CancellationCallback, cancellationState.Pointer);
        OpenFile(raw, path, cancellationToken);
        CheckResult(LibRawNativeMethods.LibRawUnpackThumb(raw), "unpack embedded thumbnail", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var thumbnailPointer = LibRawNativeMethods.LibRawDcrawMakeMemThumb(raw, out var thumbnailError);
        if (thumbnailPointer == IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateThumbnailFailure(thumbnailError);
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
    /// Validates the SOI marker and trims everything after the end of the image. Camera thumbnails are often padded to a fixed
    /// slot size, usually with 0x00/0xFF fill (fast path: the data ends with EOI once the fill is skipped) but sometimes with
    /// other bytes. For those the marker structure is walked (segment lengths, entropy data where FF is only followed by 00,
    /// RSTn or FF) to find the real EOI, so an FFD9 inside an embedded EXIF thumbnail is skipped with its APP1 segment. A
    /// truncated stream (no real EOI) is rejected, unless the last FFD9 of the final 64 KB is followed only by bytes that
    /// contain no JPEG structure (no SOI/SOS/SOF/DQT/DHT/APPn marker).
    /// </summary>
    internal static byte[] TrimToEndOfImage(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new InvalidDataException("LibRaw thumbnail is not a complete JPEG stream.");
        var end = bytes.Length;
        while (end > 2 && bytes[end - 1] is 0x00 or 0xFF) end--;
        if (end >= 4 && bytes[end - 1] == 0xD9 && bytes[end - 2] == 0xFF)
            return end == bytes.Length ? bytes : bytes[..end];

        var eoi = FindEndOfImageByStructure(bytes);
        if (eoi < 0) eoi = FindTrailingEoiWithStructureFreeTail(bytes);
        if (eoi < 0) throw new InvalidDataException("LibRaw thumbnail is not a complete JPEG stream.");
        return bytes[..eoi];
    }

    /// <summary>Index just past the EOI found by walking the JPEG segments from the SOI, or -1 when the structure ends (truncated/invalid) before an EOI.</summary>
    private static int FindEndOfImageByStructure(byte[] b)
    {
        var i = 2;
        while (true)
        {
            if (i >= b.Length || b[i] != 0xFF) return -1;
            while (i < b.Length && b[i] == 0xFF) i++; // fill bytes before a marker
            if (i >= b.Length) return -1;
            var marker = b[i++];
            if (marker == 0xD9) return i;
            if (marker is 0x00 or 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7)) continue; // standalone markers carry no length
            if (i + 2 > b.Length) return -1;
            var segmentLength = (b[i] << 8) | b[i + 1];
            if (segmentLength < 2) return -1;
            i += segmentLength;
            if (i > b.Length) return -1;
            if (marker != 0xDA) continue;

            // Entropy-coded data: an FF is followed by 00 (stuffed), RSTn or more FF; any other marker ends the scan.
            while (true)
            {
                if (i >= b.Length) return -1;
                if (b[i] != 0xFF) { i++; continue; }
                if (i + 1 >= b.Length) return -1;
                var next = b[i + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                if (next == 0xFF) { i++; continue; }
                break;
            }
        }
    }

    private const int TrailingEoiSearchWindow = 64 * 1024;

    /// <summary>Fallback for streams the structure walk cannot follow: the last FFD9 of the final 64 KB when nothing after it looks like JPEG structure; else -1.</summary>
    private static int FindTrailingEoiWithStructureFreeTail(byte[] b)
    {
        var start = Math.Max(2, b.Length - TrailingEoiSearchWindow);
        for (var i = b.Length - 2; i >= start; i--)
        {
            if (b[i] != 0xFF || b[i + 1] != 0xD9) continue;
            for (var j = i + 2; j < b.Length - 1; j++)
            {
                if (b[j] != 0xFF) continue;
                var marker = b[j + 1];
                // SOI, SOFn/DHT (C0-CF), SOS, DQT, DRI, APPn, COM: the tail is more image, not padding.
                if (marker is 0xD8 or 0xDA or 0xDB or 0xDD or 0xFE or (>= 0xC0 and <= 0xCF) or (>= 0xE0 and <= 0xEF)) return -1;
            }

            return i + 2;
        }

        return -1;
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = CreateHandle();
        OpenFile(handle, path, CancellationToken.None);
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

        // LibRaw has no reduced-size decode: even a preview-sized request unpacks and processes the whole sensor image, and
        // holds LibRaw's working image plus its 8-bit RGB output (3 B/px) at once (several hundred MB for a 60-100 MP
        // sensor; a full-size request adds the managed BGRA copy, 4 B/px). Serialise every decode so parallel callers
        // (e.g. LibRaw as the general backend, preload workers) cannot exhaust memory together.
        // The gate is priority aware and cancellable (see FullDecodeGate): a viewer decode is served before queued preloads.
        using var slot = s_fullDecodeGate.Enter(request.Priority, cancellationToken);
        try
        {
            return DecodeCore(request, cancellationToken);
        }
        catch (OutOfMemoryException ex)
        {
            throw new InvalidOperationException("Not enough memory to decode this RAW image.", ex);
        }
    }

    /// <summary>Test seam: (total available, current load) of the memory the pre-decode headroom check compares against; the GC's reading by default.</summary>
    internal Func<(long TotalAvailable, long Load)> MemoryInfo { get; init; } = DecodeMemoryGuard.ReadGcMemoryInfo;

    private WpfDecodedImage DecodeCore(DecodeRequest request, CancellationToken cancellationToken)
    {
        using var cancellationState = new CancellationState(cancellationToken);
        // libraw_open_buffer stores a pointer into the caller's buffer (no copy) that unpack/process read later, so the
        // pin must outlive the LibRaw handle: declared before `raw` so it is released after libraw_close.
        using var bufferPin = request.Bytes is { } bytes ? bytes.Pin() : default;
        SafeLibRawImageHandle image;
        ProcessedImageHeader header;
        // The LibRaw handle (its 16-bit working image is 8 B/px) is closed as soon as the 8-bit RGB buffer exists, i.e. BEFORE the
        // managed/WPF bitmap is allocated, so the two never coexist at the decode's peak.
        using (var raw = CreateHandle())
        {
            LibRawNativeMethods.LibRawSetProgressHandler(raw, CancellationCallback, cancellationState.Pointer);
            // The parameters must be set BEFORE libraw_open: identify() adopts the embedded camera matrix (rgb_cam) only when
            // use_camera_wb is already set at open time (identify.cpp: use_camera_matrix & (use_camera_wb|dng_version ? 1 : 0 | 2)), and some
            // makers (Olympus ORF, Pentax PEF, Leaf) read it while parsing. Setting it afterwards leaves those files on LibRaw's generic matrix.
            if (!_configureAfterOpen) ConfigureOutput(raw);
            OpenSource(raw, request, bufferPin, cancellationToken);
            _stageObserver?.Invoke("opened");
            if (_configureAfterOpen) ConfigureOutput(raw);
            EnsureMemoryHeadroom(raw, request);
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
                throw CreateFailure(imageError, "create decoded image");
            }

            image = new SafeLibRawImageHandle(imagePointer);
            header = Marshal.PtrToStructure<ProcessedImageHeader>(imagePointer);
        }
        _stageObserver?.Invoke("handle-closed"); // LibRaw working image freed before any bitmap memory is allocated

        using (image)
        {
            if (header.Type != LibRawNativeMethods.ImageBitmap || header.Width == 0 || header.Height == 0 || !RgbBgraResampler.IsSupportedChannelCount(header.Colors) || header.Bits != 8)
                throw new InvalidDataException("LibRaw returned an unsupported processed image format.");

            var rgbLength = RgbBgraResampler.ValidateSourceLength(header.Width, header.Height, header.DataSize, header.Colors);
            var (targetWidth, targetHeight) = request.Box.Fit(header.Width, header.Height);
            _ = RgbBgraResampler.ValidateTargetLength(targetWidth, targetHeight);
            using var pixels = SourceToBuffer(image, header, rgbLength, targetWidth, targetHeight, cancellationToken);
            // The RGB buffer is freed as soon as the BGRA pixels exist, before WPF copies them into the bitmap.
            image.Dispose();
            // Reported at the moment WPF starts copying: "source-released" proves LibRaw's RGB buffer is already freed then.
            var bitmap = RgbBgraResampler.ToBitmap(pixels, () => _stageObserver?.Invoke(image.IsClosed ? "source-released" : "source-held"));
            _stageObserver?.Invoke("bitmap-created");

            var downscaled = targetWidth < header.Width || targetHeight < header.Height;
            return new WpfDecodedImage(bitmap, downscaled, actualBackend: DecoderBackend.LibRaw,
                originalWidth: header.Width, originalHeight: header.Height);
        }
    }

    private static unsafe RgbBgraResampler.BgraBuffer SourceToBuffer(SafeLibRawImageHandle image, ProcessedImageHeader header, int rgbLength,
        int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        var source = new ReadOnlySpan<byte>((byte*)IntPtr.Add(image.DangerousGetHandle(), ProcessedImageHeader.DataOffset), rgbLength);
        return RgbBgraResampler.ResizeToBuffer(source, header.Width, header.Height, targetWidth, targetHeight, header.Colors, cancellationToken);
    }

    /// <summary>Refuses a decode whose estimated peak (see <see cref="DecodeMemoryGuard"/>) exceeds the available memory headroom, before anything big is allocated.</summary>
    private void EnsureMemoryHeadroom(SafeLibRawHandle raw, DecodeRequest request)
    {
        var width = LibRawNativeMethods.LibRawGetIWidth(raw);
        var height = LibRawNativeMethods.LibRawGetIHeight(raw);
        if (width <= 0 || height <= 0) return; // unknown size: nothing to estimate from
        var (targetWidth, targetHeight) = request.Box.Fit(width, height);
        var (total, load) = MemoryInfo();
        var family = DecodeMemoryGuard.FamilyFromDecoderName(LibRawNativeMethods.TryGetDecoderName(raw));
        var estimate = DecodeMemoryGuard.EstimatePeakBytes(width, height, targetWidth, targetHeight,
            LibRawNativeMethods.LibRawGetRawWidth(raw), LibRawNativeMethods.LibRawGetRawHeight(raw), family);
        if (!DecodeMemoryGuard.HasHeadroom(estimate, total, load))
            throw new InvalidOperationException("Not enough memory to decode this RAW image.");
    }

    private void ConfigureOutput(SafeLibRawHandle raw)
    {
        LibRawNativeMethods.LibRawSetOutputColor(raw, 1); // sRGB
        LibRawNativeMethods.LibRawSetOutputBps(raw, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(raw, 1);
        // As-shot white balance, like the embedded JPEG preview; without it LibRaw applies its daylight multipliers and the zoom
        // decode looks off-white-balance. Only written on the exact tested LibRaw (0.22.2) whose struct layout is proven by the
        // sentinel check; any other build keeps LibRaw's defaults and no raw memory is touched.
        LibRawNativeMethods.TrySetUseCameraWb(raw, 1, 8, 1);
        _stageObserver?.Invoke("configured");
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
            OpenFile(handle, request.Path, cancellationToken);
            return;
        }

        CheckResult(LibRawNativeMethods.LibRawOpenBuffer(handle, (IntPtr)bufferPin.Pointer, checked((nuint)bytes.Length)), "open RAW buffer", cancellationToken);
    }

    private const int LibRawIoError = -100009; // LIBRAW_IO_ERROR

    /// <summary>Paths this long (below MAX_PATH = 260 minus room for a file name) are retried in extended-length form when libraw_open_wfile fails.</summary>
    internal const int LongPathThreshold = 248;

    /// <summary>
    /// Opens <paramref name="path"/> with libraw_open_wfile. That call fails with LIBRAW_IO_ERROR for paths beyond MAX_PATH even though the
    /// .NET readers succeed, so a long path is retried once in its extended-length form; when that fails too the ORIGINAL error is reported.
    /// </summary>
    private static void OpenFile(SafeLibRawHandle handle, string path, CancellationToken cancellationToken)
    {
        var code = LibRawNativeMethods.LibRawOpenWFile(handle, path);
        if (code == LibRawIoError && ToExtendedLengthPath(path) is { } extended && LibRawNativeMethods.LibRawOpenWFile(handle, extended) == 0) return;
        CheckResult(code, "open RAW file", cancellationToken);
    }

    /// <summary>
    /// The extended-length form of a long fully qualified path (<c>\\?\C:\...</c>; UNC paths become <c>\\?\UNC\server\share\...</c>), or null
    /// when the path is shorter than <see cref="LongPathThreshold"/>, already extended/device, relative, or cannot be normalised.
    /// </summary>
    internal static string? ToExtendedLengthPath(string path)
    {
        if (path.Length < LongPathThreshold || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            full = Path.GetFullPath(path); // collapses "." / ".." and forward slashes, which extended-length paths do not do
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
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
            throw CreateFailure(errorCode, operation);
        }
    }

    private const int LibRawUnsufficientMemory = -100007; // LIBRAW_UNSUFFICIENT_MEMORY
    private const int ErrnoNoMemory = 12;                 // LibRaw returns positive errno values from unpack/process for libc failures

    /// <summary>Insufficient memory is a resource failure of a healthy file, not corrupt data: same exception as the managed OutOfMemoryException path in Decode.</summary>
    internal static Exception CreateFailure(int errorCode, string operation) =>
        errorCode is LibRawUnsufficientMemory or ErrnoNoMemory
            ? new InvalidOperationException("Not enough memory to decode this RAW image.")
            : CreateDecodeException(errorCode, operation);

    /// <summary>Maps a make_mem_thumb failure like every other native failure: out of memory is a resource error, not a corrupt file.</summary>
    internal static Exception CreateThumbnailFailure(int errorCode) => CreateFailure(errorCode, "create embedded JPEG thumbnail");

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
