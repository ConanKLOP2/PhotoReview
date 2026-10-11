using System.Runtime.InteropServices;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Pixels;

/// <summary>
/// Final shrink of an already decoded <see cref="PixelBuffer"/> by WIC's Fant scaler (<c>IWICBitmapScaler</c>,
/// <see cref="WICBitmapInterpolationMode.Fant"/>): the fine-scale step of the DCT-scaled TurboJPEG path and of any other
/// pixel-path decoder whose native scale does not land on the target box (NO-WPF-EXEC-PLAN, owner decision "b": one scaler for
/// every WIC-capable path instead of a hand-written resampler, so TurboJPEG, WIC Direct and the disk-cache reader agree).
/// </summary>
/// <remarks>
/// One copy of the source into an <c>IWICBitmap</c> (<c>CreateBitmapFromMemory</c>, WIC cannot borrow the caller's memory), then
/// the scaler writes straight into the destination buffer. The source is only read. Out of memory inside WIC (E_OUTOFMEMORY)
/// is reported as <see cref="InsufficientMemoryException"/> (an <see cref="OutOfMemoryException"/>), never as a raw
/// <see cref="COMException"/>, so the caller's "too large for the available memory" verdict applies.
/// </remarks>
internal static class WicPixelScaler
{
    /// <summary>
    /// A new <paramref name="width"/> x <paramref name="height"/> buffer with the layout of <paramref name="source"/>; the caller
    /// owns it. Only meant for shrinking, but a same-size request is served by a plain copy and an enlargement is allowed.
    /// </summary>
    internal static PixelBuffer Resize(PixelBuffer source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (source.ByteCount > uint.MaxValue)
            throw new ArgumentException("Pixel buffer is too large for WIC.", nameof(source));

        if (width == source.Width && height == source.Height)
        {
            var copy = PixelBuffer.Allocate(width, height, source.Layout);
            try
            {
                for (var y = 0; y < height; y++) source.GetRow(y).CopyTo(copy.GetRow(y));
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        IWICImagingFactory? factory = null;
        IWICBitmapSource? bitmap = null;
        IWICBitmapScaler? scaler = null;
        PixelBuffer? destination = null;
        try
        {
            factory = WicImageEncoder.CreateFactory();
            var format = WicImageEncoder.SourceFormat(source.Layout);
            factory.CreateBitmapFromMemory((uint)source.Width, (uint)source.Height, ref format, (uint)source.Stride,
                (uint)source.ByteCount, source.Address, out var bitmapPointer);
            bitmap = WicImageEncoder.TakeObject<IWICBitmapSource>(bitmapPointer);
            factory.CreateBitmapScaler(out scaler);
            scaler.Initialize(bitmap, (uint)width, (uint)height, WICBitmapInterpolationMode.Fant);

            destination = PixelBuffer.Allocate(width, height, source.Layout);
            ((IWICBitmapSource)scaler).CopyPixels(IntPtr.Zero, (uint)destination.Stride, (uint)destination.ByteCount, destination.Address);
            var result = destination;
            destination = null;
            return result;
        }
        catch (COMException ex) when (WicCacheImageReader.IsOutOfMemory(ex))
        {
            throw new InsufficientMemoryException("Not enough memory to scale the decoded image.", ex);
        }
        finally
        {
            destination?.Dispose();
            WicImageEncoder.Release(scaler);
            WicImageEncoder.Release(bitmap);
            WicImageEncoder.Release(factory);
        }
    }
}
