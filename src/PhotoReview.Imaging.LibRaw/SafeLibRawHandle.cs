using Microsoft.Win32.SafeHandles;

namespace PhotoReview.Imaging.LibRaw;

internal sealed class SafeLibRawHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeLibRawHandle() : base(ownsHandle: true) { }

    internal SafeLibRawHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        LibRawNativeMethods.LibRawClose(handle);
        return true;
    }
}

internal sealed class SafeLibRawImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeLibRawImageHandle() : base(ownsHandle: true) { }

    internal SafeLibRawImageHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        LibRawNativeMethods.LibRawDcrawClearMem(handle);
        return true;
    }
}
