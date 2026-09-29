using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.LibRaw;

internal static class LibRawNativeMethods
{
    internal const string LibraryName = "libraw.dll";
    internal const int ImageBitmap = 2;
    internal const int ImageJpeg = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(IntPtr data, int stage, int iteration, int expected);

    [DllImport(LibraryName, EntryPoint = "libraw_init", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawInit(uint flags);

    [DllImport(LibraryName, EntryPoint = "libraw_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawClose(IntPtr handle);

    [DllImport(LibraryName, EntryPoint = "libraw_open_wfile", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int LibRawOpenWFile(SafeLibRawHandle handle, string path);

    [DllImport(LibraryName, EntryPoint = "libraw_open_buffer", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawOpenBuffer(SafeLibRawHandle handle, IntPtr buffer, nuint size);

    [DllImport(LibraryName, EntryPoint = "libraw_unpack", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawUnpack(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_unpack_thumb", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawUnpackThumb(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_dcraw_process", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawDcrawProcess(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_dcraw_make_mem_image", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawDcrawMakeMemImage(SafeLibRawHandle handle, out int errorCode);

    [DllImport(LibraryName, EntryPoint = "libraw_dcraw_make_mem_thumb", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawDcrawMakeMemThumb(SafeLibRawHandle handle, out int errorCode);

    [DllImport(LibraryName, EntryPoint = "libraw_dcraw_clear_mem", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawDcrawClearMem(IntPtr image);

    [DllImport(LibraryName, EntryPoint = "libraw_adjust_sizes_info_only", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawAdjustSizesInfoOnly(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_get_iwidth", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawGetIWidth(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_get_iheight", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawGetIHeight(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_set_progress_handler", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetProgressHandler(SafeLibRawHandle handle, ProgressCallback callback, IntPtr data);

    [DllImport(LibraryName, EntryPoint = "libraw_set_output_color", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetOutputColor(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_set_output_bps", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetOutputBps(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_set_no_auto_bright", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetNoAutoBright(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_strerror", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawStrError(int errorCode);

    internal static string FormatError(int errorCode)
    {
        var message = Marshal.PtrToStringAnsi(LibRawStrError(errorCode));
        return string.IsNullOrWhiteSpace(message) ? $"LibRaw error {errorCode}" : message;
    }
}
