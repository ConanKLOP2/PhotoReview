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

        var pixels = new byte[checked(pixelCount * 4)];
        unsafe
        {
            var source = (byte*)IntPtr.Add(image.DangerousGetHandle(), ProcessedImageHeader.DataOffset);
            fixed (byte* target = pixels)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var sourceIndex = pixelIndex * 3;
                    var targetIndex = pixelIndex * 4;
                    target[targetIndex] = source[sourceIndex + 2];
                    target[targetIndex + 1] = source[sourceIndex + 1];
                    target[targetIndex + 2] = source[sourceIndex];
                    target[targetIndex + 3] = byte.MaxValue;
                    if ((pixelIndex & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(header.Width, header.Height, 96, 96, PixelFormats.Bgr32, null, pixels, checked((int)header.Width * 4));
        return new WpfDecodedImage(bitmap, actualBackend: DecoderBackend.LibRaw);
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
