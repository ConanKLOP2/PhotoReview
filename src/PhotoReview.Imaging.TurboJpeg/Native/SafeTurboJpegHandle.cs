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

    public SafeTurboJpegHandle(IntPtr preexistingHandle, bool ownsHandle) : this(preexistingHandle, ownsHandle, null)
    {
    }

    /// <summary>Test seam: <paramref name="destroy"/> replaces <c>tj3Destroy</c> (null, the production value, calls the native one).</summary>
    internal SafeTurboJpegHandle(IntPtr preexistingHandle, bool ownsHandle, Action<IntPtr>? destroy) : base(ownsHandle)
    {
        _destroy = destroy;
        SetHandle(preexistingHandle);
    }

    private readonly Action<IntPtr>? _destroy;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            if (_destroy is null) TurboJpegNative.tj3Destroy(handle);
            else _destroy(handle);
        }
        return true;
    }
}
