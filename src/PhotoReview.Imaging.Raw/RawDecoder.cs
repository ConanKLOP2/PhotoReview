using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// RAW format decoder (PhotoReview.Imaging.Raw).
/// Implements RAW-21/22:
/// 1. Reads RAW container header via <see cref="IRawContainerReader"/> / <see cref="SourceRawHeaderSource"/>.
/// 2. Selects appropriate JPEG preview via <see cref="PreviewSelector"/>.
/// 3. Reads only the chosen preview byte range (passing through <see cref="ISourceReader"/>).
/// 4. Decodes inner JPEG using the provided fallback/primary decoder with <see cref="DecodeRequest.SourceOrientation"/>.
/// 5. Wraps result with sensor dimensions, EXIF from <see cref="RawExif"/>, and ActualBackend.
/// </summary>
public sealed class RawDecoder : IImageDecoder
{
    private const int MaxFallbackThumbnailBytes = 32 << 20;

    /// <summary>A JPEG whose long side is at most this many pixels is a thumbnail, never a viewable photo.</summary>
    private const int MaxThumbnailLongSide = 256;
    private readonly IImageDecoder _innerDecoder;
    private readonly ISourceReader _sourceReader;
    private readonly RawContainerReaderRegistry _registry;
    private readonly SourceBytesCache? _sourceBytesCache;
    private readonly IRawPreviewFallback? _previewFallback;
    private readonly IImageDecoder? _noPreviewDecoder; // full-decode last resort for a RAW with no usable JPEG (e.g. LibRaw)
    private readonly BoundedLruCache<RawInfoKey, RawContainerInfo> _containerInfoCache = new(256, _ => 1);

    public RawDecoder(
        IImageDecoder innerDecoder,
        ISourceReader? sourceReader = null,
        RawContainerReaderRegistry? registry = null,
        SourceBytesCache? sourceBytesCache = null,
        IRawPreviewFallback? previewFallback = null,
        IImageDecoder? noPreviewDecoder = null)
    {
        _noPreviewDecoder = noPreviewDecoder;
        _innerDecoder = innerDecoder ?? throw new ArgumentNullException(nameof(innerDecoder));
        _sourceReader = sourceReader ?? PhysicalSourceReader.Instance;
        _registry = registry ?? new RawContainerReaderRegistry();
        _sourceBytesCache = sourceBytesCache;
        _previewFallback = previewFallback;
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = GetContainerInfo(path, SourceReadPriority.Viewer, out var headerSource);
        using (headerSource)
        {

            int sensorW = info.SensorWidth;
            int sensorH = info.SensorHeight;
            if (sensorW <= 0 || sensorH <= 0)
            {
                var bestPreview = PreviewSelector.SelectPreview(headerSource, info.Previews, DecodeBox.Unbounded, info.Orientation);
                if (bestPreview != null && bestPreview.Width > 0 && bestPreview.Height > 0)
                {
                    sensorW = bestPreview.Width;
                    sensorH = bestPreview.Height;
                }
            }

            return new ImageInfo(sensorW, sensorH, info.Orientation);
        }
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        var containerInfo = GetContainerInfo(request.Path, request.Priority, out var headerSource);
        using (headerSource)
        {
            var exif = RawExif.TryReadExif(headerSource, containerInfo);

            var preview = PreviewSelector.SelectPreview(headerSource, containerInfo.Previews, request.Box, containerInfo.Orientation);
            IDecodedImage decoded;
            long previewBytesRead = 0;
            long fallbackThumbnailBytesRead = 0;
            long fullDecodeBytesRead = 0;
            var fromJpegPreview = true;
            if (preview == null || preview.Length <= 0)
            {
                // A valid RAW without an embedded JPEG (e.g. Leica M8 DNG): every format may use the preview fallback
                // (LibRaw thumbnail) and, when that yields no JPEG, the full-decode fallback, instead of failing.
                // Without either, the original error stands.
                if (_previewFallback is null && _noPreviewDecoder is null)
                    throw UserFacingError.Localized(
                        new InvalidDataException($"No embedded preview found in RAW file: {request.Path}"),
                        () => Tr.ImageErrorRawNoPreview);

                decoded = DecodeWithoutPreview(request, containerInfo, out fallbackThumbnailBytesRead, out fromJpegPreview);
                if (!fromJpegPreview)
                {
                    // A full decode reads the whole file, header included: count it once, not on top of the header bytes.
                    fullDecodeBytesRead = fallbackThumbnailBytesRead;
                    fallbackThumbnailBytesRead = 0;
                }
            }
            else
            {
                decoded = DecodePreviewWithFallbacks(request, containerInfo, headerSource, preview,
                    out previewBytesRead, out fallbackThumbnailBytesRead, out fullDecodeBytesRead, out fromJpegPreview);
            }

            int sensorW = containerInfo.SensorWidth;
            int sensorH = containerInfo.SensorHeight;
            if (sensorW <= 0 || sensorH <= 0)
            {
                sensorW = decoded.OriginalWidth;
                sensorH = decoded.OriginalHeight;
            }
            else if (request.ApplyOrientation && ExifOrientation.IsTransposed(containerInfo.Orientation))
            {
                // containerInfo.SensorWidth/Height are stored raw dimensions;
                // OriginalWidth/Height in IDecodedImage are visual (orientation-applied) dimensions.
                (sensorW, sensorH) = (sensorH, sensorW);
            }

            // A thumbnail-sized JPEG is never the photo: even when the container declares no sensor size (so the size
            // comparison cannot tell), mark it downscaled so the viewer zoom recovers the real pixels.
            bool tinyPreview = fromJpegPreview && Math.Max(decoded.OriginalWidth, decoded.OriginalHeight) <= MaxThumbnailLongSide;
            bool downscaled = decoded.Downscaled || tinyPreview || (decoded.PixelWidth < sensorW || decoded.PixelHeight < sensorH);

            return new RawDecodedImage(
                decoded,
                sensorWidth: sensorW,
                sensorHeight: sensorH,
                downscaled: downscaled,
                orientation: containerInfo.Orientation,
                exif: exif ?? decoded.Exif,
                actualBackend: decoded.ActualBackend,
                // LibRaw's native thumbnail path does not expose exact stream read counts. Include the returned JPEG
                // payload as an estimate; any additional LibRaw metadata I/O is not represented here.
                sourceBytesRead: fromJpegPreview || fullDecodeBytesRead <= 0
                    ? checked(headerSource.TotalBytesRead + previewBytesRead + fallbackThumbnailBytesRead)
                    : checked(fullDecodeBytesRead + previewBytesRead),
                // The inner decode's original size is the JPEG's own (orientation-applied) size, whatever box it was decoded into.
                embeddedPreviewWidth: fromJpegPreview ? decoded.OriginalWidth : 0,
                embeddedPreviewHeight: fromJpegPreview ? decoded.OriginalHeight : 0);
        }
    }

    /// <summary>Most previews one decode will try (the chosen one plus next-best candidates) before the last-resort full decode.</summary>
    private const int MaxPreviewAttempts = 4;

    /// <summary>
    /// Decodes the chosen preview. When that fails with a recoverable error (corrupt/truncated/unsupported JPEG) and a last
    /// resort exists (the no-preview full decoder, or the ORF thumbnail fallback), the next-best preview is tried first, then
    /// the ORF thumbnail fallback (an unsupported ORF preview only), then the full decode. Cancellation and out-of-memory
    /// are never swallowed; with no last resort the original failure propagates unchanged.
    /// </summary>
    private IDecodedImage DecodePreviewWithFallbacks(
        DecodeRequest request,
        RawContainerInfo containerInfo,
        IRawHeaderSource headerSource,
        EmbeddedPreview chosen,
        out long previewBytesRead,
        out long fallbackThumbnailBytesRead,
        out long fullDecodeBytesRead,
        out bool fromJpegPreview)
    {
        previewBytesRead = 0;
        fallbackThumbnailBytesRead = 0;
        fullDecodeBytesRead = 0;
        fromJpegPreview = true;

        bool orfThumbnailPossible = containerInfo.Format == RawFormat.Orf && _previewFallback is not null;
        if (_noPreviewDecoder is null && !orfThumbnailPossible)
            return DecodePreview(request, containerInfo, chosen, out previewBytesRead);

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? firstFailure = null;
        Exception? lastFailure = null;
        var tried = new List<long>();
        long failedBytes = 0;
        EmbeddedPreview? current = chosen;
        while (current is not null && tried.Count < MaxPreviewAttempts)
        {
            tried.Add(current.Offset);
            try
            {
                var decoded = DecodePreview(request, containerInfo, current, out long read);
                previewBytesRead = checked(read + failedBytes);
                return decoded;
            }
            catch (Exception ex) when (IsRecoverablePreviewFailure(ex))
            {
                firstFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                lastFailure = ex;
                failedBytes = checked(failedBytes + Math.Min(current.Length, RawContainerLimits.MaxPreviewBytes));
                current = SelectNextPreview(headerSource, containerInfo, request, tried);
            }
        }

        if (orfThumbnailPossible && lastFailure is NotSupportedException)
        {
            var thumbnailRequest = new DecodeRequest(
                path: request.Path,
                box: request.Box,
                applyOrientation: request.ApplyOrientation,
                bytes: ReadOnlyMemory<byte>.Empty,
                priority: request.Priority,
                sourceOrientation: containerInfo.Orientation);
            try
            {
                return DecodeFallbackThumbnail(request.Path, containerInfo.Format, thumbnailRequest, () => Tr.ImageErrorRawCorrupt, out fallbackThumbnailBytesRead);
            }
            catch (Exception ex) when (_noPreviewDecoder is not null && IsRecoverablePreviewFailure(ex))
            {
                fallbackThumbnailBytesRead = 0;
            }
        }

        if (_noPreviewDecoder is null)
        {
            firstFailure!.Throw();
            throw lastFailure!; // unreachable: Throw() never returns
        }

        fromJpegPreview = false; // the LibRaw full decode: pixels are the sensor's, not a preview
        var full = _noPreviewDecoder.Decode(request);
        var file = new FileInfo(request.Path);
        fullDecodeBytesRead = file.Exists ? file.Length : 0;
        previewBytesRead = failedBytes;
        return full;
    }

    /// <summary>Corrupt, truncated or unsupported preview data; never cancellation, out-of-memory or plain I/O errors.</summary>
    private static bool IsRecoverablePreviewFailure(Exception ex) =>
        ex is InvalidDataException or System.IO.FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException;

    private static EmbeddedPreview? SelectNextPreview(IRawHeaderSource headerSource, RawContainerInfo containerInfo, DecodeRequest request, List<long> triedOffsets)
    {
        var remaining = containerInfo.Previews
            .Where(p => p.Kind == EmbeddedPreviewKind.Jpeg && p.Length > 0 && !triedOffsets.Contains(p.Offset))
            .ToList();
        return remaining.Count == 0
            ? null
            : PreviewSelector.SelectPreview(headerSource, remaining, request.Box, containerInfo.Orientation);
    }

    private IDecodedImage DecodePreview(DecodeRequest request, RawContainerInfo containerInfo, EmbeddedPreview preview, out long previewBytesRead)
    {
        previewBytesRead = 0;
        // Read and cache ONLY the preview byte range. Never route a RAW file through the whole-file byte cache.
        byte[] previewBytes;
        try
        {
            if (preview.Length > RawContainerLimits.MaxPreviewBytes)
                throw new InvalidDataException($"Embedded preview is too large: {preview.Length} bytes (limit {RawContainerLimits.MaxPreviewBytes}).");
            var fileInfo = new FileInfo(request.Path);
            if (_sourceBytesCache is not null && fileInfo.Exists && _sourceBytesCache.CanCacheRange(preview.Length))
            {
                // A range-cache hit reads nothing from the source; only a miss counts the preview bytes.
                if (_sourceBytesCache.TryGetRange(request.Path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks,
                        preview.Offset, (int)preview.Length, out var cachedRange))
                {
                    previewBytes = cachedRange;
                }
                else
                {
                    previewBytes = _sourceBytesCache.GetOrReadRange(request.Path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks,
                        preview.Offset, (int)preview.Length, request.Priority);
                    previewBytesRead = preview.Length;
                }
            }
            else
            {
                previewBytes = ReadPreviewRange(request.Path, preview.Offset, (int)preview.Length, request.Priority);
                previewBytesRead = preview.Length;
            }
            if (preview.ColorSpace == PreviewColorSpace.AdobeRgb)
                previewBytes = RawJpegIccProfile.EnsureAdobeRgbProfile(previewBytes);
        }
        catch (InvalidDataException ex) when (!UserFacingError.IsLocalized(ex))
        {
            // Oversized/garbled preview or a non-JPEG tagged Adobe RGB: same type for callers, localized "corrupt" sentence for the UI.
            UserFacingError.Localized(ex, () => Tr.ImageErrorRawCorrupt);
            throw;
        }

        // Inner decode with preview bytes and container orientation
        var innerRequest = new DecodeRequest(
            path: request.Path,
            box: request.Box,
            applyOrientation: request.ApplyOrientation,
            bytes: (ReadOnlyMemory<byte>)previewBytes,
            priority: request.Priority,
            sourceOrientation: containerInfo.Orientation);

        return _innerDecoder.Decode(innerRequest);
    }

    private IDecodedImage DecodeWithoutPreview(DecodeRequest request, RawContainerInfo containerInfo, out long bytesRead, out bool isJpegPreview)
    {
        bytesRead = 0;
        isJpegPreview = true;
        if (_previewFallback is not null)
        {
            var thumbnailRequest = new DecodeRequest(
                path: request.Path,
                box: request.Box,
                applyOrientation: request.ApplyOrientation,
                bytes: ReadOnlyMemory<byte>.Empty,
                priority: request.Priority,
                sourceOrientation: containerInfo.Orientation);
            try
            {
                return DecodeFallbackThumbnail(request.Path, containerInfo.Format, thumbnailRequest, () => Tr.ImageErrorRawNoPreview, out bytesRead);
            }
            // The thumbnail is missing or not a JPEG (e.g. Leica M8 DNG stores a bitmap): fall through to the full decode.
            catch (Exception ex) when (_noPreviewDecoder is not null && ex is InvalidDataException or NotSupportedException)
            {
                bytesRead = 0;
            }
        }

        isJpegPreview = false; // the LibRaw full decode: pixels are the sensor's, not a preview
        var decoded = _noPreviewDecoder!.Decode(request);
        var file = new FileInfo(request.Path);
        bytesRead = file.Exists ? file.Length : 0; // a full decode reads the whole file
        return decoded;
    }

    private IDecodedImage DecodeFallbackThumbnail(string path, RawFormat format, DecodeRequest innerRequest, Func<string> invalidThumbnailText, out long thumbnailBytesRead)
    {
        var thumbnailBytes = _previewFallback!.ReadJpegThumbnail(path, format);
        if (thumbnailBytes.Length <= 0 || thumbnailBytes.Length > MaxFallbackThumbnailBytes)
            throw UserFacingError.Localized(
                new InvalidDataException($"RAW fallback thumbnail size is invalid: {thumbnailBytes.Length} bytes."), invalidThumbnailText);

        thumbnailBytesRead = thumbnailBytes.Length;
        return _innerDecoder.Decode(innerRequest with { Bytes = thumbnailBytes });
    }

    private RawContainerInfo GetContainerInfo(string path, SourceReadPriority priority, out SourceRawHeaderSource headerSource)
    {
        headerSource = new SourceRawHeaderSource(path, _sourceReader, priority);
        try
        {
            var file = new FileInfo(path);
            var exists = file.Exists;
            // Injectable source readers also support virtual/test paths with no physical FileInfo.
            var sourceLength = headerSource.Length;
            var lastWriteUtcTicks = exists ? file.LastWriteTimeUtc.Ticks : 0;
            var fullPath = Path.GetFullPath(path).ToUpperInvariant();
            var key = new RawInfoKey(fullPath, sourceLength, lastWriteUtcTicks);
            if (_containerInfoCache.TryGet(key, out var cached)) return cached;

            var ext = Path.GetExtension(path);
            var probeSpan = headerSource.Read(0, RawContainerLimits.InitialProbeLength(headerSource.Length));
            var reader = _registry.FindReader(probeSpan, ext)
                ?? throw UserFacingError.Localized(new NotSupportedException($"Unsupported RAW format: {ext}"), () => Tr.ImageErrorRawUnsupported);
            RawContainerInfo info;
            try
            {
                info = reader.Read(headerSource, CancellationToken.None);
            }
            catch (InvalidDataException ex) when (!UserFacingError.IsLocalized(ex))
            {
                // A container the reader rejects as malformed: same exception type for callers, localized sentence for the UI.
                UserFacingError.Localized(ex, () => Tr.ImageErrorRawCorrupt);
                throw;
            }

            var current = new FileInfo(path);
            if (exists && (!current.Exists || current.Length != key.Length || current.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks))
                throw new IOException($"RAW source changed while reading its container: {path}");
            _containerInfoCache.Set(key, info);
            return info;
        }
        catch (InvalidDataException ex) when (ex.InnerException is IOException or ObjectDisposedException)
        {
            // The header source reports I/O failures as InvalidDataException(inner); a disk/share error is not a corrupt RAW.
            headerSource.Dispose();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
            throw;
        }
        catch
        {
            headerSource.Dispose();
            throw;
        }
    }

    private byte[] ReadPreviewRange(string path, long offset, int count, SourceReadPriority priority)
    {
        var bytes = new byte[count];
        using var stream = _sourceReader.OpenSource(path, priority);
        stream.Seek(offset, SeekOrigin.Begin);
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead != bytes.Length)
            throw new EndOfStreamException($"Truncated embedded preview in {path}");
        return bytes;
    }

    private readonly record struct RawInfoKey(string Path, long Length, long LastWriteUtcTicks);

    private sealed class RawDecodedImage : IDecodedImage, ISourceReadMetrics, IRawPreviewInfo
    {
        private readonly IDecodedImage _inner;

        public int PixelWidth => _inner.PixelWidth;
        public int PixelHeight => _inner.PixelHeight;
        public bool Downscaled { get; }
        public int Orientation { get; }
        public long EstimatedBytes => _inner.EstimatedBytes;
        public long SourceBytesRead { get; }
        public object PlatformImage => _inner.PlatformImage;
        public DecoderBackend ActualBackend { get; }
        public int OriginalWidth { get; }
        public int OriginalHeight { get; }
        public ExifSummary? Exif { get; }
        public int EmbeddedPreviewWidth { get; }
        public int EmbeddedPreviewHeight { get; }

        public RawDecodedImage(
            IDecodedImage inner,
            int sensorWidth,
            int sensorHeight,
            bool downscaled,
            int orientation,
            ExifSummary? exif,
            DecoderBackend actualBackend,
            long sourceBytesRead,
            int embeddedPreviewWidth,
            int embeddedPreviewHeight)
        {
            EmbeddedPreviewWidth = embeddedPreviewWidth;
            EmbeddedPreviewHeight = embeddedPreviewHeight;
            _inner = inner;
            OriginalWidth = sensorWidth;
            OriginalHeight = sensorHeight;
            Downscaled = downscaled;
            Orientation = orientation;
            Exif = exif;
            ActualBackend = actualBackend;
            SourceBytesRead = sourceBytesRead;
        }
    }
}
