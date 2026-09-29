using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Full RAW decoder backed by the pinned LibRaw C API.</summary>
public sealed class LibRawDecoder : IImageDecoder
{
    private static readonly LibRawNativeMethods.ProgressCallback CancellationCallback = CheckCancellation;

    public ImageInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = CreateHandle();
        CheckResult(LibRawNativeMethods.LibRawOpenWFile(handle, path), "open RAW file");
        CheckResult(LibRawNativeMethods.LibRawAdjustSizesInfoOnly(handle), "read RAW dimensions");
        var width = LibRawNativeMethods.LibRawGetIWidth(handle);
        var height = LibRawNativeMethods.LibRawGetIHeight(handle);
        if (width <= 0 || height <= 0) throw new InvalidDataException("LibRaw returned invalid image dimensions.");
        return new ImageInfo(width, height, 1);
    }

    public IDecodedImage Decode(DecodeRequest request) => Decode(request, CancellationToken.None);

    public static IDecodedImage Decode(DecodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        if (!request.ApplyOrientation)
            throw new NotSupportedException("LibRaw always applies the RAW container's orientation; an un-oriented full decode is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();

        using var cancellationState = new CancellationState(cancellationToken);
        using var raw = CreateHandle();
        LibRawNativeMethods.LibRawSetProgressHandler(raw, CancellationCallback, cancellationState.Pointer);
        OpenSource(raw, request, cancellationToken);

        LibRawNativeMethods.LibRawSetOutputColor(raw, 1); // sRGB
        LibRawNativeMethods.LibRawSetOutputBps(raw, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(raw, 1);
        CheckResult(LibRawNativeMethods.LibRawUnpack(raw), "unpack RAW data", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CheckResult(LibRawNativeMethods.LibRawDcrawProcess(raw), "process RAW data", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var imagePointer = LibRawNativeMethods.LibRawDcrawMakeMemImage(raw, out var imageError);
        if (imagePointer == IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDecodeException(imageError, "create decoded image");
        }
        using var image = new SafeLibRawImageHandle(imagePointer);

        var header = Marshal.PtrToStructure<ProcessedImageHeader>(image.DangerousGetHandle());
        if (header.Type != LibRawNativeMethods.ImageBitmap || header.Width == 0 || header.Height == 0 || header.Colors != 3 || header.Bits != 8)
            throw new InvalidDataException("LibRaw returned an unsupported processed image format.");

        var pixelCount = checked((int)header.Width * header.Height);
        var rgbLength = checked(pixelCount * 3);
        if (header.DataSize < rgbLength || header.DataSize > int.MaxValue)
            throw new InvalidDataException("LibRaw returned an invalid processed image buffer length.");

        var (targetWidth, targetHeight) = request.Box.Fit(header.Width, header.Height);
        var pixels = new byte[checked(targetWidth * targetHeight * 4)];
        unsafe
        {
            var source = (byte*)IntPtr.Add(image.DangerousGetHandle(), ProcessedImageHeader.DataOffset);
            fixed (byte* target = pixels)
            {
                for (var y = 0; y < targetHeight; y++)
                {
                    var sourceY = Math.Clamp((y + 0.5d) * header.Height / targetHeight - 0.5d, 0, header.Height - 1d);
                    var y0 = (int)sourceY;
                    var y1 = Math.Min(y0 + 1, header.Height - 1);
                    var fy = sourceY - y0;
                    for (var x = 0; x < targetWidth; x++)
                    {
                        var sourceX = Math.Clamp((x + 0.5d) * header.Width / targetWidth - 0.5d, 0, header.Width - 1d);
                        var x0 = (int)sourceX;
                        var x1 = Math.Min(x0 + 1, header.Width - 1);
                        var fx = sourceX - x0;
                        var targetIndex = (y * targetWidth + x) * 4;
                        target[targetIndex] = Interpolate(source, header.Width, x0, x1, y0, y1, fx, fy, 2);
                        target[targetIndex + 1] = Interpolate(source, header.Width, x0, x1, y0, y1, fx, fy, 1);
                        target[targetIndex + 2] = Interpolate(source, header.Width, x0, x1, y0, y1, fx, fy, 0);
                        target[targetIndex + 3] = byte.MaxValue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Bgr32, null, pixels, checked(targetWidth * 4));
        var downscaled = targetWidth < header.Width || targetHeight < header.Height;
        return new WpfDecodedImage(bitmap, downscaled, actualBackend: DecoderBackend.LibRaw,
            originalWidth: header.Width, originalHeight: header.Height);
    }

    private static SafeLibRawHandle CreateHandle()
    {
        var pointer = LibRawNativeMethods.LibRawInit(0);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("LibRaw could not initialize its decoder.");
        return new SafeLibRawHandle(pointer);
    }

    private static unsafe void OpenSource(SafeLibRawHandle handle, DecodeRequest request, CancellationToken cancellationToken)
    {
        if (request.Bytes is not { } bytes)
        {
            CheckResult(LibRawNativeMethods.LibRawOpenWFile(handle, request.Path), "open RAW file", cancellationToken);
            return;
        }

        using var pin = bytes.Pin();
        CheckResult(LibRawNativeMethods.LibRawOpenBuffer(handle, (IntPtr)pin.Pointer, checked((nuint)bytes.Length)), "open RAW buffer", cancellationToken);
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

    private static unsafe byte Interpolate(byte* source, int width, int x0, int x1, int y0, int y1, double fx, double fy, int channel)
    {
        var topLeft = source[(y0 * width + x0) * 3 + channel];
        var topRight = source[(y0 * width + x1) * 3 + channel];
        var bottomLeft = source[(y1 * width + x0) * 3 + channel];
        var bottomRight = source[(y1 * width + x1) * 3 + channel];
        var top = topLeft + (topRight - topLeft) * fx;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * fx;
        return (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), byte.MinValue, byte.MaxValue);
    }

    private static void CheckResult(int errorCode, string operation, CancellationToken cancellationToken = default)
    {
        if (errorCode != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDecodeException(errorCode, operation);
        }
    }

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
