using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// RAW format decoder (PhotoReview.Imaging.Raw).
/// Implements RAW-21:
/// 1. Reads RAW container header via <see cref="IRawContainerReader"/> / <see cref="SourceRawHeaderSource"/>.
/// 2. Selects appropriate JPEG preview via <see cref="PreviewSelector"/>.
/// 3. Reads only the chosen preview byte range (passing through <see cref="ISourceReader"/>).
/// 4. Decodes inner JPEG using the provided fallback/primary decoder with <see cref="DecodeRequest.SourceOrientation"/>.
/// 5. Wraps result with sensor dimensions, EXIF from <see cref="RawExif"/>, and ActualBackend.
/// </summary>
public sealed class RawDecoder : IImageDecoder
{
    private readonly IImageDecoder _innerDecoder;
    private readonly ISourceReader _sourceReader;
    private readonly RawContainerReaderRegistry _registry;

    public RawDecoder(
        IImageDecoder innerDecoder,
        ISourceReader? sourceReader = null,
        RawContainerReaderRegistry? registry = null)
    {
        _innerDecoder = innerDecoder ?? throw new ArgumentNullException(nameof(innerDecoder));
        _sourceReader = sourceReader ?? PhysicalSourceReader.Instance;
        _registry = registry ?? new RawContainerReaderRegistry();
    }

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var headerSource = new SourceRawHeaderSource(path, _sourceReader, SourceReadPriority.Viewer);
        var ext = Path.GetExtension(path);
        var probeSpan = headerSource.Read(0, Math.Min(64, (int)headerSource.Length));

        var reader = _registry.FindReader(probeSpan, ext)
            ?? throw new NotSupportedException($"Unsupported RAW format: {ext}");

        var info = reader.Read(headerSource, CancellationToken.None);

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

    public IDecodedImage Decode(DecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        using var headerSource = new SourceRawHeaderSource(request.Path, _sourceReader, request.Priority);
        var ext = Path.GetExtension(request.Path);
        var probeSpan = headerSource.Read(0, Math.Min(64, (int)headerSource.Length));

        var reader = _registry.FindReader(probeSpan, ext)
            ?? throw new NotSupportedException($"Unsupported RAW format: {ext}");

        var containerInfo = reader.Read(headerSource, CancellationToken.None);
        var exif = RawExif.TryReadExif(headerSource, containerInfo);

        var preview = PreviewSelector.SelectPreview(headerSource, containerInfo.Previews, request.Box, containerInfo.Orientation);
        if (preview == null || preview.Length <= 0)
        {
            throw new InvalidDataException($"No embedded preview found in RAW file: {request.Path}");
        }

        // Read ONLY the preview bytes
        byte[] previewBytes = new byte[preview.Length];
        using (var stream = _sourceReader.OpenSource(request.Path, request.Priority))
        {
            stream.Seek(preview.Offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < previewBytes.Length)
            {
                int read = stream.Read(previewBytes, totalRead, previewBytes.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead != previewBytes.Length)
            {
                throw new EndOfStreamException($"Truncated embedded preview in {request.Path}");
            }
        }

        // Inner decode with preview bytes and container orientation
        var innerRequest = new DecodeRequest(
            path: request.Path,
            box: request.Box,
            applyOrientation: request.ApplyOrientation,
            bytes: (ReadOnlyMemory<byte>)previewBytes,
            priority: request.Priority,
            sourceOrientation: containerInfo.Orientation);

        var decoded = _innerDecoder.Decode(innerRequest);

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
            actualBackend: decoded.ActualBackend);
    }

    private sealed class RawDecodedImage : IDecodedImage
    {
        private readonly IDecodedImage _inner;

        public int PixelWidth => _inner.PixelWidth;
        public int PixelHeight => _inner.PixelHeight;
        public bool Downscaled { get; }
        public int Orientation { get; }
        public long EstimatedBytes => _inner.EstimatedBytes;
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
            DecoderBackend actualBackend)
        {
            _inner = inner;
            OriginalWidth = sensorWidth;
            OriginalHeight = sensorHeight;
            Downscaled = downscaled;
            Orientation = orientation;
            Exif = exif;
            ActualBackend = actualBackend;
        }
    }
}
