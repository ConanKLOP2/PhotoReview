using System;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// WPF-specific implementation of <see cref="IDecodedImage"/> wrapping a frozen <see cref="BitmapSource"/>.
/// </summary>
public sealed class WpfDecodedImage : IDecodedImage
{
    public BitmapSource Source { get; }
    public int PixelWidth => Source.PixelWidth;
    public int PixelHeight => Source.PixelHeight;
    public bool Downscaled { get; }
    public int Orientation { get; }
    public long EstimatedBytes => Math.Max(1, (long)Source.PixelWidth * Source.PixelHeight * 4);
    public object PlatformImage => Source;
    public DecoderBackend ActualBackend { get; }

    /// <summary>See <see cref="IDecodedImage.OriginalWidth"/>. Defaults to <see cref="PixelWidth"/>
    /// when the caller doesn't know a different (larger, pre-downscale) source size.</summary>
    public int OriginalWidth { get; }

    /// <summary>See <see cref="IDecodedImage.OriginalHeight"/>.</summary>
    public int OriginalHeight { get; }

    public WpfDecodedImage(BitmapSource source, bool downscaled = false, int orientation = 1,
        DecoderBackend actualBackend = DecoderBackend.Wpf, int originalWidth = 0, int originalHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen && source.CanFreeze)
        {
            source.Freeze();
        }
        Source = source;
        Downscaled = downscaled;
        Orientation = orientation;
        ActualBackend = actualBackend;
        OriginalWidth = originalWidth > 0 ? originalWidth : source.PixelWidth;
        OriginalHeight = originalHeight > 0 ? originalHeight : source.PixelHeight;
    }
}
