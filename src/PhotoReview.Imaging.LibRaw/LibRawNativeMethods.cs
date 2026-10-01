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

    [DllImport(LibraryName, EntryPoint = "libraw_get_raw_width", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawGetRawWidth(SafeLibRawHandle handle);

    [DllImport(LibraryName, EntryPoint = "libraw_get_raw_height", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int LibRawGetRawHeight(SafeLibRawHandle handle);

    /// <summary>libraw_decoder_info_t: the selected load_raw decoder's name (static C string) and flags; valid once the file is open.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DecoderInfo
    {
        internal IntPtr Name;
        internal uint Flags;
    }

    [DllImport(LibraryName, EntryPoint = "libraw_get_decoder_info", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int LibRawGetDecoderInfo(SafeLibRawHandle handle, out DecoderInfo info);

    /// <summary>The decoder name LibRaw picked while opening the file (e.g. "lossless_dng_load_raw()"), or null when unavailable.</summary>
    internal static string? TryGetDecoderName(SafeLibRawHandle handle) =>
        LibRawGetDecoderInfo(handle, out var info) == 0 && info.Name != IntPtr.Zero ? Marshal.PtrToStringAnsi(info.Name) : null;

    [DllImport(LibraryName, EntryPoint = "libraw_get_iparams", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr LibRawGetIParams(SafeLibRawHandle handle);

    // libraw_iparams_t (LibRaw 0.22.x, x64 MSVC, libraw_types.h of 0.22.2; ints 4 B, no packing pragma). libraw_get_iparams returns a
    // pointer to the struct itself, so only offsets INSIDE it matter: guard[4] + make/model/software/normalized_make/normalized_model
    // (5 x 64 B) = 324 -> maker_index 324, raw_count 328, dng_version 332, is_foveon 336, colors 340, filters 344,
    // xtrans[6][6] 348, xtrans_abs[6][6] 384, cdesc[5] 420. There is no public getter for colors/filters, hence the raw read; it is used only
    // on the exact pinned runtime and a plausibility check (see TryReadRawStructure) fails closed into the conservative estimate.
    internal const int IParamsColorsOffset = 340;
    internal const int IParamsFiltersOffset = 344;
    internal const int IParamsCdescOffset = 420;

    /// <summary>
    /// Reads colors/filters from a libraw_iparams_t block and returns null when they are implausible (colors outside 1..4 or a cdesc
    /// that is not a run of ASCII capital letters of that length), i.e. when the layout does not match.
    /// </summary>
    internal static DecodeMemoryGuard.RawStructure? TryReadRawStructure(IntPtr iparams)
    {
        if (iparams == IntPtr.Zero) return null;
        var colors = Marshal.ReadInt32(iparams, IParamsColorsOffset);
        if (colors is < 1 or > 4) return null;
        for (var i = 0; i < colors; i++)
        {
            var c = Marshal.ReadByte(iparams, IParamsCdescOffset + i);
            if (c is < (byte)'A' or > (byte)'Z') return null;
        }
        return new DecodeMemoryGuard.RawStructure(colors, unchecked((uint)Marshal.ReadInt32(iparams, IParamsFiltersOffset)));
    }

    /// <summary>The file's sampling structure, or null when it cannot be read safely (other LibRaw build, implausible values).</summary>
    internal static DecodeMemoryGuard.RawStructure? TryGetRawStructure(SafeLibRawHandle handle) =>
        LibRawAvailability.IsExactPinnedVersion ? TryReadRawStructure(LibRawGetIParams(handle)) : null;

    [DllImport(LibraryName, EntryPoint = "libraw_set_progress_handler", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetProgressHandler(SafeLibRawHandle handle, ProgressCallback callback, IntPtr data);

    [DllImport(LibraryName, EntryPoint = "libraw_set_output_color", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetOutputColor(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_set_output_bps", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetOutputBps(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_set_no_auto_bright", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void LibRawSetNoAutoBright(SafeLibRawHandle handle, int value);

    [DllImport(LibraryName, EntryPoint = "libraw_version", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr LibRawVersion();

    /// <summary>The loaded runtime's version string (for example "0.22.2-Release").</summary>
    internal static string? GetVersionString() => Marshal.PtrToStringAnsi(LibRawVersion());

    /// <summary>Byte offsets, from the libraw_data_t pointer libraw_init returns, of the libraw_output_params_t fields the white-balance write depends on.</summary>
    internal readonly record struct WhiteBalanceLayout(int OutputColor, int OutputBps, int NoAutoBright, int UseCameraWb);

    // LibRaw's C API has no setter for use_camera_wb (only libraw_set_user_mul, which is not equivalent: it would double-apply
    // the camera WB to Nikon sRAW, skips LibRaw's auto-WB fallback for files without camera multipliers and ignores the CIFF
    // white patch). use_camera_wb is a plain int in libraw_output_params_t. Layout for LibRaw 0.22.x, x64 MSVC, derived from
    // libraw_types.h of 0.22.2 (ints 4 B, double 8 B, pointers 8 B, no packing pragma):
    //   offsetof(libraw_data_t, params)              = 5232
    //   offsetof(libraw_output_params_t, use_camera_wb) = 152, output_color = 160, output_bps = 200, no_auto_bright = 256
    //   => use_camera_wb 5232 + 152 = 5384, output_color 5392, output_bps 5432, no_auto_bright 5488.
    // (Verified independently by locating sentinels written through libraw_set_output_color/_output_bps/_no_auto_bright.)
    internal static readonly WhiteBalanceLayout PinnedWhiteBalanceLayout = new(OutputColor: 5392, OutputBps: 5432, NoAutoBright: 5488, UseCameraWb: 5384);

    private const int SentinelOutputColor = 0x5EA10C01;
    private const int SentinelOutputBps = 0x5EA10C02;
    private const int SentinelNoAutoBright = 0x5EA10C03;

    /// <summary>
    /// Enables the camera's as-shot white balance (LibRaw itself falls back to auto white balance when the file carries none) and applies
    /// the given output settings. Fails closed, before any raw memory write, unless <paramref name="versionPinned"/> reports the pinned
    /// LibRaw runtime; then proves the layout by writing distinctive sentinels through the supported setters and reading them back at
    /// the expected offsets. Only after that is use_camera_wb written and read back. Returns false (LibRaw's daylight default stays) on any mismatch.
    /// The output settings are always applied, whatever the result.
    /// </summary>
    internal static bool TrySetUseCameraWb(SafeLibRawHandle handle, int outputColor, int outputBps, int noAutoBright,
        Func<bool> versionPinned, WhiteBalanceLayout layout) =>
        TrySetUseCameraWb(handle.DangerousGetHandle(), value => LibRawSetOutputColor(handle, value), value => LibRawSetOutputBps(handle, value),
            value => LibRawSetNoAutoBright(handle, value), outputColor, outputBps, noAutoBright, versionPinned, layout);

    /// <summary>
    /// The logic of <see cref="TrySetUseCameraWb(SafeLibRawHandle, int, int, int, Func{bool}, WhiteBalanceLayout)"/> over a raw block and the three
    /// supported setters, so the layout proof can be tested with a fake memory block (no native library needed).
    /// </summary>
    internal static bool TrySetUseCameraWb(IntPtr pointer, Action<int> setOutputColor, Action<int> setOutputBps, Action<int> setNoAutoBright,
        int outputColor, int outputBps, int noAutoBright, Func<bool> versionPinned, WhiteBalanceLayout layout)
    {
        if (!versionPinned()) return false;
        setOutputColor(SentinelOutputColor);
        setOutputBps(SentinelOutputBps);
        setNoAutoBright(SentinelNoAutoBright);
        var layoutMatches = Marshal.ReadInt32(pointer, layout.OutputColor) == SentinelOutputColor &&
                            Marshal.ReadInt32(pointer, layout.OutputBps) == SentinelOutputBps &&
                            Marshal.ReadInt32(pointer, layout.NoAutoBright) == SentinelNoAutoBright;
        setOutputColor(outputColor);
        setOutputBps(outputBps);
        setNoAutoBright(noAutoBright);
        if (!layoutMatches) return false;
        Marshal.WriteInt32(pointer, layout.UseCameraWb, 1);
        return Marshal.ReadInt32(pointer, layout.UseCameraWb) == 1;
    }

    /// <summary>Production entry point: exact-patch gate (<see cref="LibRawAvailability.IsExactPinnedVersion"/>, 0.22.2 only) and the pinned layout.</summary>
    internal static bool TrySetUseCameraWb(SafeLibRawHandle handle, int outputColor, int outputBps, int noAutoBright) =>
        TrySetUseCameraWb(handle, outputColor, outputBps, noAutoBright, () => LibRawAvailability.IsExactPinnedVersion, PinnedWhiteBalanceLayout);

    /// <summary>Reads an int at a byte offset of the libraw_data_t (test seam).</summary>
    internal static int ReadInt32(SafeLibRawHandle handle, int offset) => Marshal.ReadInt32(handle.DangerousGetHandle(), offset);

    /// <summary>Reads use_camera_wb back (test seam); only meaningful for the pinned layout.</summary>
    internal static int ReadUseCameraWb(SafeLibRawHandle handle) => ReadInt32(handle, PinnedWhiteBalanceLayout.UseCameraWb);

    [DllImport(LibraryName, EntryPoint = "libraw_strerror", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr LibRawStrError(int errorCode);

    internal static string FormatError(int errorCode)
    {
        var message = Marshal.PtrToStringAnsi(LibRawStrError(errorCode));
        return string.IsNullOrWhiteSpace(message) ? $"LibRaw error {errorCode}" : message;
    }
}
