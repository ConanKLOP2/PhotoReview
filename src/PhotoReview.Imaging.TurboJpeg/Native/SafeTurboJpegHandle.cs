using System;
using Microsoft.Win32.SafeHandles;

namespace PhotoReview.Imaging.TurboJpeg.Native;

/// <summary>
/// SafeHandle wrapper for libjpeg-turbo 3.x instance handles (tjhandle).
/// Ensures deterministic and leak-free release via <c>tj3Destroy</c>.
/// </summary>
public sealed class SafeTurboJpegHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTurboJpegHandle() : base(true)
    {
    }

    public SafeTurboJpegHandle(IntPtr preexistingHandle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            TurboJpegNative.tj3Destroy(handle);
        }
        return true;
    }
}
