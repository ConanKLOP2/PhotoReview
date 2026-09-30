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

    // LibRaw's C API has no setter for use_camera_wb. It is a plain int in libraw_output_params_t at a fixed offset of the
    // libraw_data_t that libraw_init returns. Offsets below are for LibRaw 0.22.x x64 and were measured on the pinned libraw.dll
    // by writing sentinel values through libraw_set_output_color/_output_bps/_no_auto_bright and locating them. Within the struct
    // (libraw_types.h) use_camera_wb sits two ints before output_color, output_bps 40 bytes after it, no_auto_bright 96 after it.
    private const int OutputColorOffset = 5392;
    private const int UseCameraWbOffset = OutputColorOffset - 2 * sizeof(int);
    private const int OutputBpsOffset = OutputColorOffset + 40;
    private const int NoAutoBrightOffset = OutputColorOffset + 96;

    /// <summary>
    /// Enables the camera's as-shot white balance (LibRaw itself falls back to auto white balance when the file carries none).
    /// Call after the output_color/output_bps/no_auto_bright setters: it writes only when those three values read back at their
    /// expected offsets, so a LibRaw build with another struct layout is never poked; returns false then.
    /// </summary>
    internal static bool TrySetUseCameraWb(SafeLibRawHandle handle, int expectedOutputColor, int expectedOutputBps, int expectedNoAutoBright)
    {
        var pointer = handle.DangerousGetHandle();
        if (Marshal.ReadInt32(pointer, OutputColorOffset) != expectedOutputColor ||
            Marshal.ReadInt32(pointer, OutputBpsOffset) != expectedOutputBps ||
            Marshal.ReadInt32(pointer, NoAutoBrightOffset) != expectedNoAutoBright)
            return false;
        Marshal.WriteInt32(pointer, UseCameraWbOffset, 1);
        return true;
    }

    /// <summary>Reads use_camera_wb back (test seam); only meaningful for the pinned layout.</summary>
    internal static int ReadUseCameraWb(SafeLibRawHandle handle) => Marshal.ReadInt32(handle.DangerousGetHandle(), UseCameraWbOffset);

    [DllImport(LibraryName, EntryPoint = "libraw_strerror", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawStrError(int errorCode);

    internal static string FormatError(int errorCode)
    {
        var message = Marshal.PtrToStringAnsi(LibRawStrError(errorCode));
        return string.IsNullOrWhiteSpace(message) ? $"LibRaw error {errorCode}" : message;
    }
}
