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
    private readonly BoundedLruCache<RawInfoKey, CachedRaw> _containerInfoCache = new(256, _ => 1);

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

    /// <summary>
    /// The original (sensor) size of the RAW: the container's sensor size, else the size of the best embedded preview.
    /// Throws <see cref="InvalidDataException"/> when neither is known (for example a RAF whose JPEG range is invalid and whose
    /// CFA header is absent): callers show the result as image dimensions and do not treat 0x0 as "unknown", so an unknown size
    /// is an error here. <see cref="Decode"/> does not depend on this and still reaches the full-decode fallback.
    /// </summary>
    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cached = GetContainerInfo(path, SourceReadPriority.Viewer, out var headerSource, out var key);
        using (headerSource)
        {
            var info = cached.Info;
            int sensorW = info.SensorWidth;
            int sensorH = info.SensorHeight;
            if (sensorW <= 0 || sensorH <= 0)
            {
                var bestPreview = PreviewSelector.SelectPreview(headerSource, info.Previews, DecodeBox.Unbounded, info.Orientation, out var resolved);
                RememberResolvedPreviews(key, cached, resolved);
                if (bestPreview != null && bestPreview.Width > 0 && bestPreview.Height > 0)
                {
                    sensorW = bestPreview.Width;
                    sensorH = bestPreview.Height;
                }
            }

            if (sensorW <= 0 || sensorH <= 0)
                throw UserFacingError.Localized(
                    new InvalidDataException($"RAW file declares no image size (no sensor size and no sized preview): {path}"),
                    () => Tr.ImageErrorRawCorrupt);

            return new ImageInfo(sensorW, sensorH, info.Orientation);
        }
    }

    public IDecodedImage Decode(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        var cached = GetContainerInfo(request.Path, request.Priority, out var headerSource, out var key);
        using (headerSource)
        {
            var containerInfo = cached.Info;

            // The EXIF summary is part of what the cache remembers for this file identity (same key as the container info):
            // a repeated decode neither re-reads nor re-parses up to 4 MiB of EXIF block. A read that failed part-way
            // (incomplete) is never remembered as "no EXIF".
            ExifSummary? exif;
            if (cached.ExifRead)
            {
                exif = cached.Exif;
            }
            else
            {
                exif = RawExif.TryReadExif(headerSource, containerInfo, out bool exifComplete);
                if (exifComplete)
                {
                    cached = cached with { Exif = exif, ExifRead = true };
                    _containerInfoCache.Set(key, cached);
                }
            }

            var preview = PreviewSelector.SelectPreview(headerSource, containerInfo.Previews, request.Box, containerInfo.Orientation, out var resolvedPreviews);
            cached = RememberResolvedPreviews(key, cached, resolvedPreviews);
            containerInfo = cached.Info;
            IDecodedImage decoded;
            long previewBytesRead = 0;
            long fallbackThumbnailBytesRead = 0;
            long fullDecodeBytesRead = 0;
            var fromJpegPreview = true;
            var degradedFallback = false;
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
                decoded = DecodePreviewWithFallbacks(request, containerInfo, key, headerSource, preview,
                    out previewBytesRead, out fallbackThumbnailBytesRead, out fullDecodeBytesRead, out fromJpegPreview, out degradedFallback);
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
                embeddedPreviewHeight: fromJpegPreview ? decoded.OriginalHeight : 0,
                isDegradedFallback: degradedFallback || decoded.IsDegradedFallback);
        }
    }

    /// <summary>Most previews one decode will try (the chosen one plus next-best candidates) before the last-resort full decode.</summary>
    private const int MaxPreviewAttempts = 4;

    /// <summary>
    /// Decodes the chosen preview. When that fails with a recoverable error (corrupt/truncated/unsupported JPEG) the next-best
    /// preview is tried first (always: it needs no native code), then the ORF thumbnail fallback (an unsupported ORF preview
    /// only, when one exists), then the full decode (when the no-preview decoder exists). Cancellation, out-of-memory and
    /// resource-exhaustion COM failures (a transient condition, see <see cref="IsRecoverablePreviewFailure"/>) are never
    /// swallowed; when no step remains the first failure propagates unchanged.
    /// A thumbnail-sized next-best preview (long side below <see cref="MinUsefulFallbackLongSide"/>) is never taken after a
    /// LARGER preview failed, unless the failure proves the data corrupt (<see cref="IsCorruptDataFailure"/>) and every
    /// remaining preview is that small: thumbnails are a last resort for corrupt data, not a silent answer to transient or
    /// unsupported-component errors (those go on to the full decode, or propagate).
    /// </summary>
    private IDecodedImage DecodePreviewWithFallbacks(
        DecodeRequest request,
        RawContainerInfo containerInfo,
        RawInfoKey key,
        IRawHeaderSource headerSource,
        EmbeddedPreview chosen,
        out long previewBytesRead,
        out long fallbackThumbnailBytesRead,
        out long fullDecodeBytesRead,
        out bool fromJpegPreview,
        out bool degradedFallback)
    {
        degradedFallback = false;
        previewBytesRead = 0;
        fallbackThumbnailBytesRead = 0;
        fullDecodeBytesRead = 0;
        fromJpegPreview = true;

        bool orfThumbnailPossible = containerInfo.Format == RawFormat.Orf && _previewFallback is not null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? firstFailure = null;
        Exception? lastFailure = null;
        var tried = new List<long>();
        long failedBytes = 0;
        int largestFailedLongSide = 0;
        EmbeddedPreview? current = chosen;
        while (current is not null && tried.Count < MaxPreviewAttempts)
        {
            tried.Add(current.Offset);
            try
            {
                var decoded = DecodePreview(request, containerInfo, key, current, out long read);
                previewBytesRead = checked(read + failedBytes);
                // A next-best preview accepted after a larger one failed is not the best the container offers.
                degradedFallback = tried.Count > 1;
                return decoded;
            }
            catch (Exception ex) when (IsRecoverablePreviewFailure(ex))
            {
                firstFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                lastFailure = ex;
                failedBytes = checked(failedBytes + Math.Min(current.Length, RawContainerLimits.MaxPreviewBytes));
                largestFailedLongSide = Math.Max(largestFailedLongSide, LongSide(current));
                current = SelectNextPreview(headerSource, containerInfo, request, tried, largestFailedLongSide, IsCorruptDataFailure(ex));
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
                var thumbnail = DecodeFallbackThumbnail(request.Path, containerInfo.Format, thumbnailRequest, () => Tr.ImageErrorRawCorrupt, out fallbackThumbnailBytesRead);
                degradedFallback = true; // the container's real previews failed to decode
                return thumbnail;
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

    /// <summary>
    /// Corrupt, truncated or unsupported preview data; never cancellation, out-of-memory, plain I/O errors or a COM failure
    /// that reports resource exhaustion (E_OUTOFMEMORY, ERROR_NOT_ENOUGH_MEMORY, ERROR_NO_SYSTEM_RESOURCES,
    /// ERROR_COMMITMENT_LIMIT): that is transient, and a smaller preview would be cached as if it were the photo.
    /// </summary>
    private static bool IsRecoverablePreviewFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException com
            ? !IsResourceExhaustion(com.HResult)
            : ex is InvalidDataException or System.IO.FileFormatException or NotSupportedException;

    private static bool IsResourceExhaustion(int hresult) =>
        unchecked((uint)hresult) is 0x8007000E or 0x80070008 or 0x800705AA or 0x800705AF;

    /// <summary>The failure says the preview's data is damaged (not missing a component, not transient).</summary>
    private static bool IsCorruptDataFailure(Exception ex) =>
        ex is InvalidDataException or System.IO.FileFormatException
        || (ex is System.Runtime.InteropServices.COMException com
            && unchecked((uint)com.HResult) is 0x88982F60 /* WINCODEC_ERR_BADIMAGE */ or 0x88982F61 /* BADHEADER */ or 0x88982F07 /* UNKNOWNIMAGEFORMAT */);

    /// <summary>A preview shorter than this on its long side is a thumbnail, not something to show as the photo.</summary>
    private const int MinUsefulFallbackLongSide = 1000;

    private static int LongSide(EmbeddedPreview p) => Math.Max(p.Width, p.Height);

    private static EmbeddedPreview? SelectNextPreview(
        IRawHeaderSource headerSource, RawContainerInfo containerInfo, DecodeRequest request, List<long> triedOffsets,
        int largestFailedLongSide, bool failureIsCorruptData)
    {
        var remaining = containerInfo.Previews
            .Where(p => p.Kind == EmbeddedPreviewKind.Jpeg && p.Length > 0 && !triedOffsets.Contains(p.Offset))
            .ToList();
        if (remaining.Count == 0) return null;

        // Previews of unknown size (0) cannot be told apart from real photos, so they are never treated as thumbnails.
        bool IsThumbnail(EmbeddedPreview p) => LongSide(p) is > 0 and < MinUsefulFallbackLongSide;
        if (largestFailedLongSide > 0)
        {
            // A preview failed: prefer any remaining non-thumbnail; a thumbnail SMALLER than the failed preview only when
            // everything left is one and the failure proves the data corrupt.
            var usable = remaining.Where(p => !IsThumbnail(p) || LongSide(p) >= largestFailedLongSide).ToList();
            if (usable.Count > 0)
                remaining = usable;
            else if (!failureIsCorruptData)
                return null;
        }

        return PreviewSelector.SelectPreview(headerSource, remaining, request.Box, containerInfo.Orientation);
    }

    private IDecodedImage DecodePreview(DecodeRequest request, RawContainerInfo containerInfo, RawInfoKey key, EmbeddedPreview preview, out long previewBytesRead)
    {
        previewBytesRead = 0;
        // Read and cache ONLY the preview byte range. Never route a RAW file through the whole-file byte cache.
        byte[] previewBytes;
        try
        {
            if (preview.Length > RawContainerLimits.MaxPreviewBytes)
                throw new InvalidDataException($"Embedded preview is too large: {preview.Length} bytes (limit {RawContainerLimits.MaxPreviewBytes}).");
            var fileInfo = new FileInfo(request.Path);
            if (fileInfo.Exists && (fileInfo.Length != key.Length || fileInfo.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks))
            {
                // The preview offsets come from a container info keyed by another file state: never read with them.
                _containerInfoCache.Remove(key);
                throw ChangedWhileReading(request.Path);
            }

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
                previewBytes = ReadPreviewRange(request.Path, preview.Offset, (int)preview.Length, key.Length, request.Priority);
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
            catch (Exception ex) when (_noPreviewDecoder is not null && IsRecoverablePreviewFailure(ex))
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

    /// <summary>
    /// Stores <paramref name="resolved"/> (the previews with the dimensions and colour space a selection just read from the JPEG
    /// headers) in the cache entry of <paramref name="key"/> when anything changed, and returns the updated entry.
    /// </summary>
    private CachedRaw RememberResolvedPreviews(RawInfoKey key, CachedRaw cached, IReadOnlyList<EmbeddedPreview> resolved)
    {
        var current = cached.Info.Previews;
        bool changed = resolved.Count != current.Count;
        for (int i = 0; !changed && i < current.Count; i++)
            changed = !ReferenceEquals(current[i], resolved[i]);
        if (!changed) return cached;

        var updated = cached with { Info = cached.Info with { Previews = resolved.ToArray() } };
        _containerInfoCache.Set(key, updated);
        return updated;
    }

    private CachedRaw GetContainerInfo(string path, SourceReadPriority priority, out SourceRawHeaderSource headerSource, out RawInfoKey key)
    {
        // The identity (length, write time) is taken from a stat BEFORE the file is opened and must still hold after the
        // container was parsed, and the opened stream must have that same length: a file replaced (rename) at any point in
        // between can then never have its old identity cached for the new bytes (or the other way round).
        // Injectable source readers also support virtual/test paths with no physical FileInfo: no stat checks for those.
        var before = new FileInfo(path);
        var exists = before.Exists;
        long statLength = exists ? before.Length : -1;
        long lastWriteUtcTicks = exists ? before.LastWriteTimeUtc.Ticks : 0;

        headerSource = new SourceRawHeaderSource(path, _sourceReader, priority);
        try
        {
            var sourceLength = headerSource.Length;
            if (exists && sourceLength != statLength)
                throw ChangedWhileReading(path);

            var fullPath = Path.GetFullPath(path).ToUpperInvariant();
            key = new RawInfoKey(fullPath, sourceLength, lastWriteUtcTicks);
            if (_containerInfoCache.TryGet(key, out var cached))
            {
                ThrowIfStatChanged(path, exists, statLength, lastWriteUtcTicks);
                return cached;
            }

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

            ThrowIfStatChanged(path, exists, statLength, lastWriteUtcTicks);
            var entry = new CachedRaw(info, null, false);
            _containerInfoCache.Set(key, entry);
            return entry;
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

    private static IOException ChangedWhileReading(string path) =>
        UserFacingError.Localized(new IOException($"RAW source changed while reading: {path}"), () => Tr.ErrIoFileChangedWhileReading(path));

    /// <summary>Throws when a fresh stat of <paramref name="path"/> no longer equals the identity the container info was keyed with.</summary>
    private static void ThrowIfStatChanged(string path, bool existedBefore, long expectedLength, long expectedLastWriteTicks)
    {
        if (!existedBefore) return;
        var current = new FileInfo(path);
        if (!current.Exists || current.Length != expectedLength || current.LastWriteTimeUtc.Ticks != expectedLastWriteTicks)
            throw ChangedWhileReading(path);
    }

    private byte[] ReadPreviewRange(string path, long offset, int count, long expectedLength, SourceReadPriority priority)
    {
        var bytes = new byte[count];
        using var stream = _sourceReader.OpenSource(path, priority);
        if (stream.Length != expectedLength)
            throw ChangedWhileReading(path); // not the file the container info (and its preview offsets) describe
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

    /// <summary>What is remembered per file identity: the container info (with preview headers resolved as decodes need them) and the EXIF summary once read.</summary>
    private sealed record CachedRaw(RawContainerInfo Info, ExifSummary? Exif, bool ExifRead);

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
        public bool IsDegradedFallback { get; }

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
            int embeddedPreviewHeight,
            bool isDegradedFallback)
        {
            IsDegradedFallback = isDegradedFallback;
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
