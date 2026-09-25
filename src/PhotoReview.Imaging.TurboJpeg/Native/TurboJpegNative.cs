using System;
using System.Runtime.InteropServices;

// Load turbojpeg.dll only from the application folder: without this Windows falls back to its default search order
// (working directory, PATH) and a planted turbojpeg.dll would be loaded into the process. A missing DLL simply reports "unavailable".
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace PhotoReview.Imaging.TurboJpeg.Native;

public enum TjInit
{
    Compress = 0,
    Decompress = 1,
    Transform = 2
}

public enum TjParam
{
    StopOnWarning = 0,
    BottomUp = 1,
    NoRealloc = 2,
    Quality = 3,
    Subsamp = 4,
    JpegWidth = 5,
    JpegHeight = 6,
    Precision = 7,
    Colorspace = 8,
    FastUpsample = 9,
    FastDct = 10,
    Optimize = 11,
    Progressive = 12,
    ScanLimit = 13,
    Arithmetic = 14,
    Lossless = 15,
    LosslessPsv = 16,
    LosslessPt = 17,
    RestartBlocks = 18,
    RestartRows = 19,
    XDensity = 20,
    YDensity = 21,
    DensityUnits = 22
}

public enum TjPixelFormat
{
    Rgb = 0,
    Bgr = 1,
    Rgbx = 2,
    Bgrx = 3,
    Xbgr = 4,
    Xrgb = 5,
    Gray = 6,
    Rgba = 7,
    Bgra = 8,
    Abgr = 9,
    Argb = 10,
    Cmyk = 11
}

public enum TjColorspace
{
    Rgb = 0,
    YCbCr = 1,
    Gray = 2,
    Cmyk = 3,
    Ycck = 4
}

[StructLayout(LayoutKind.Sequential)]
public struct TjScalingFactor : IEquatable<TjScalingFactor>
{
    public int Num;
    public int Denom;

    public TjScalingFactor(int num, int denom)
    {
        Num = num;
        Denom = denom;
    }

    public double Value => Denom == 0 ? 0 : (double)Num / Denom;

    public bool Equals(TjScalingFactor other) => Num == other.Num && Denom == other.Denom;
    public override bool Equals(object? obj) => obj is TjScalingFactor f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(Num, Denom);
    public override string ToString() => $"{Num}/{Denom}";

    public static bool operator ==(TjScalingFactor left, TjScalingFactor right) => left.Equals(right);
    public static bool operator !=(TjScalingFactor left, TjScalingFactor right) => !left.Equals(right);

    public static readonly TjScalingFactor One = new(1, 1);
}

internal static unsafe class TurboJpegNative
{
    public const string DllName = "turbojpeg.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tj3Init(int initType);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void tj3Destroy(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3DecompressHeader(SafeTurboJpegHandle handle, byte* jpegBuf, nuint jpegSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3Get(SafeTurboJpegHandle handle, int param);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3Set(SafeTurboJpegHandle handle, int param, int value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3SetScalingFactor(SafeTurboJpegHandle handle, TjScalingFactor scalingFactor);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3Decompress8(
        SafeTurboJpegHandle handle,
        byte* jpegBuf,
        nuint jpegSize,
        byte* dstBuf,
        int pitch,
        int pixelFormat);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tj3GetErrorStr(SafeTurboJpegHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tj3GetErrorCode(SafeTurboJpegHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tj3GetScalingFactors(out int numScalingFactors);

    internal static SafeTurboJpegHandle CreateDecompressor()
    {
        var ptr = tj3Init((int)TjInit.Decompress);
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to initialize TurboJPEG decompressor instance.");
        }
        return new SafeTurboJpegHandle(ptr, ownsHandle: true);
    }

    internal static string? GetErrorMessage(SafeTurboJpegHandle handle)
    {
        var ptr = tj3GetErrorStr(handle);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }
}
