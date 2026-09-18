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

    public WpfDecodedImage(BitmapSource source, bool downscaled = false, int orientation = 1, DecoderBackend actualBackend = DecoderBackend.Wpf)
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
    }
}
