using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Caching;
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
    private readonly IImageDecoder _innerDecoder;
    private readonly ISourceReader _sourceReader;
    private readonly RawContainerReaderRegistry _registry;
    private readonly SourceBytesCache? _sourceBytesCache;
    private readonly IRawPreviewFallback? _previewFallback;
    private readonly BoundedLruCache<RawInfoKey, RawContainerInfo> _containerInfoCache = new(256, _ => 1);

    public RawDecoder(
        IImageDecoder innerDecoder,
        ISourceReader? sourceReader = null,
        RawContainerReaderRegistry? registry = null,
        SourceBytesCache? sourceBytesCache = null,
        IRawPreviewFallback? previewFallback = null)
    {
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
            if (preview == null || preview.Length <= 0)
            {
                throw new InvalidDataException($"No embedded preview found in RAW file: {request.Path}");
            }

            // Read and cache ONLY the preview byte range. Never route a RAW file through the whole-file byte cache.
            if (preview.Length > int.MaxValue)
                throw new InvalidDataException($"Embedded preview is too large: {preview.Length} bytes.");
            var fileInfo = new FileInfo(request.Path);
            var previewBytes = _sourceBytesCache is not null && fileInfo.Exists && _sourceBytesCache.CanCacheRange(preview.Length)
                ? _sourceBytesCache.GetOrReadRange(request.Path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks,
                    preview.Offset, (int)preview.Length, request.Priority)
                : ReadPreviewRange(request.Path, preview.Offset, (int)preview.Length, request.Priority);
            if (preview.ColorSpace == PreviewColorSpace.AdobeRgb)
                previewBytes = RawJpegIccProfile.EnsureAdobeRgbProfile(previewBytes);

            // Inner decode with preview bytes and container orientation
            var innerRequest = new DecodeRequest(
                path: request.Path,
                box: request.Box,
                applyOrientation: request.ApplyOrientation,
                bytes: (ReadOnlyMemory<byte>)previewBytes,
                priority: request.Priority,
                sourceOrientation: containerInfo.Orientation);

            IDecodedImage decoded;
            long fallbackThumbnailBytesRead = 0;
            try
            {
                decoded = _innerDecoder.Decode(innerRequest);
            }
            catch (NotSupportedException) when (containerInfo.Format == RawFormat.Orf && _previewFallback is not null)
            {
                var thumbnailBytes = _previewFallback.ReadJpegThumbnail(request.Path, containerInfo.Format);
                if (thumbnailBytes.Length <= 0 || thumbnailBytes.Length > MaxFallbackThumbnailBytes)
                    throw new InvalidDataException($"RAW fallback thumbnail size is invalid: {thumbnailBytes.Length} bytes.");

                fallbackThumbnailBytesRead = thumbnailBytes.Length;
                decoded = _innerDecoder.Decode(innerRequest with { Bytes = thumbnailBytes });
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

            bool downscaled = decoded.Downscaled || (decoded.PixelWidth < sensorW || decoded.PixelHeight < sensorH);

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
                sourceBytesRead: checked(headerSource.TotalBytesRead + preview.Length + fallbackThumbnailBytesRead));
        }
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
                ?? throw new NotSupportedException($"Unsupported RAW format: {ext}");
            var info = reader.Read(headerSource, CancellationToken.None);

            var current = new FileInfo(path);
            if (exists && (!current.Exists || current.Length != key.Length || current.LastWriteTimeUtc.Ticks != key.LastWriteUtcTicks))
                throw new IOException($"RAW source changed while reading its container: {path}");
            _containerInfoCache.Set(key, info);
            return info;
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

    private sealed class RawDecodedImage : IDecodedImage, ISourceReadMetrics
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

        public RawDecodedImage(
            IDecodedImage inner,
            int sensorWidth,
            int sensorHeight,
            bool downscaled,
            int orientation,
            ExifSummary? exif,
            DecoderBackend actualBackend,
            long sourceBytesRead)
        {
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
