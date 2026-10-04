using Microsoft.Win32.SafeHandles;

namespace PhotoReview.Imaging.LibRaw;

internal sealed class SafeLibRawHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<IntPtr>? _close;

    internal SafeLibRawHandle() : base(ownsHandle: true) { }

    internal SafeLibRawHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>Test seam: <paramref name="close"/> replaces the native libraw_close for this handle only.</summary>
    internal SafeLibRawHandle(IntPtr handle, Action<IntPtr> close) : this(handle) => _close = close;

    protected override bool ReleaseHandle()
    {
        (_close ?? LibRawNativeMethods.LibRawClose)(handle);
        return true;
    }
}

internal sealed class SafeLibRawImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<IntPtr>? _clear;

    internal SafeLibRawImageHandle() : base(ownsHandle: true) { }

    internal SafeLibRawImageHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>Test seam: <paramref name="clear"/> replaces the native libraw_dcraw_clear_mem for this handle only.</summary>
    internal SafeLibRawImageHandle(IntPtr handle, Action<IntPtr> clear) : this(handle) => _clear = clear;

    protected override bool ReleaseHandle()
    {
        (_clear ?? LibRawNativeMethods.LibRawDcrawClearMem)(handle);
        return true;
    }
}
