using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Reads a JPEG's embedded EXIF thumbnail (APP1) without decoding the full-size image.
/// Used by <see cref="Caching.ThumbnailCache"/> on a cache miss so an open-folder thumbnail
/// placeholder never forces a second full decode of the same source the preview is already
/// decoding concurrently (see ImagePresenter's preview/thumbnail race). Not a general-purpose
/// decoder: non-JPEG sources and JPEGs without an embedded thumbnail simply yield no
/// thumbnail -- the caller is expected to fall back to the in-flight preview decode instead.
/// Pure WPF managed API (<see cref="BitmapFrame.Thumbnail"/>); no WIC COM interop involved.
/// </summary>
public static class EmbeddedThumbnailReader
{
    /// <summary>
    /// Returns the source's embedded EXIF thumbnail with orientation applied, or null if the
    /// source has none (wrong format, missing APP1 thumbnail, or any read/decode failure --
    /// all treated the same: nothing to show yet, the full preview decode is already in flight).
    /// </summary>
    public static IDecodedImage? TryRead(string path, bool applyOrientation = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            // DelayCreation defers decoding the full-resolution frame; the embedded thumbnail
            // is a separate, much smaller image read from APP1 and materializes independently
            // of it, so accessing only Metadata/Thumbnail below never triggers the full decode.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            var thumbnail = frame.Thumbnail;
            if (thumbnail is null) return null;

            var width = thumbnail.PixelWidth;
            var height = thumbnail.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            // Force eager pixel materialization (CopyPixels) while the stream is still open,
            // instead of relying on BitmapCacheOption.OnLoad's caching behavior alone -- this
            // guarantees the returned image never depends on the source stream once this
            // method returns and the `using` above closes it.
            var converted = thumbnail.Format == PixelFormats.Bgra32
                ? thumbnail
                : new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);

            var materialized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            materialized.Freeze();

            var orientation = applyOrientation ? ExifOrientation.Read(frame.Metadata as BitmapMetadata) : 1;
            var oriented = ExifOrientation.Apply(materialized, orientation);

            return new WpfDecodedImage(oriented, downscaled: true, orientation: orientation, actualBackend: DecoderBackend.Wpf);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
            or FileFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
