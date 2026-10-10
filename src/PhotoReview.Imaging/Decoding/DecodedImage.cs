namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02: thay WpfDecodedImage cho mọi decoder không-WPF. OriginalWidth/Height = pixel khi truyền 0. Thực thi ở WP-02.
/// </summary>
public sealed class DecodedImage : IDecodedImage
{
    public DecodedImage(object platformImage, int pixelWidth, int pixelHeight, long estimatedBytes,
        bool downscaled = false, int orientation = 1, DecoderBackend actualBackend = DecoderBackend.WicDirect,
        int originalWidth = 0, int originalHeight = 0, PhotoReview.Imaging.Metadata.ExifSummary? exif = null,
        bool isDegradedFallback = false) => throw new NotImplementedException();

    public int PixelWidth => throw new NotImplementedException();

    public int PixelHeight => throw new NotImplementedException();

    public bool Downscaled => throw new NotImplementedException();

    public int Orientation => throw new NotImplementedException();

    public long EstimatedBytes => throw new NotImplementedException();

    public object PlatformImage => throw new NotImplementedException();

    public DecoderBackend ActualBackend => throw new NotImplementedException();

    public int OriginalWidth => throw new NotImplementedException();

    public int OriginalHeight => throw new NotImplementedException();

    public PhotoReview.Imaging.Metadata.ExifSummary? Exif => throw new NotImplementedException();

    public bool IsDegradedFallback => throw new NotImplementedException();
}
