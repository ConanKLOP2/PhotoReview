using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-15: phần interop renderer cần mà WP-13b chưa khai báo. CHỈ THÊM (file mới) - không đổi khai báo/slot của WP-13b:
//  - FillRoundedRectangle là slot [19] của ID2D1RenderTarget, WP-13b giữ chỗ bằng Reserved19FillRoundedRectangle(nint, nint);
//    ở đây gọi slot đó qua vtable thô với đúng chữ ký d2d1.h (const D2D1_ROUNDED_RECT*, ID2D1Brush*), giống cách
//    D3D11.ClearStateAndFlush gọi context. Test chạy thật: D2DRenderSurfaceTests.FillRoundedRectangle_*.
//  - kernel32 CloseHandle / WaitForSingleObjectEx cho waitable object của swap chain (handle phải được đóng khi bỏ
//    swap chain - tài liệu IDXGISwapChain2::GetFrameLatencyWaitableObject).

/// <summary>D2D1_ROUNDED_RECT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D2dRoundedRect
{
    public D2dRectF Rect;
    public float RadiusX;
    public float RadiusY;
}

internal static unsafe class D2D1RawCalls
{
    /// <summary>Slot vtable của ID2D1RenderTarget::FillRoundedRectangle (d2d1.h; IUnknown 0..2, ID2D1Resource 3).</summary>
    internal const int RenderTargetSlotFillRoundedRectangle = 19;

    private static readonly Guid IidRenderTarget = new(GraphicsGuids.D2D1RenderTarget);
    private static readonly Guid IidBrush = new(GraphicsGuids.D2D1Brush);

    /// <summary>ID2D1RenderTarget::FillRoundedRectangle([19]) trên wrapper <paramref name="renderTarget"/>.</summary>
    public static void FillRoundedRectangle(ID2D1RenderTarget renderTarget, D2dRoundedRect* roundedRect, ID2D1Brush brush)
    {
        nint target = QueryRaw(renderTarget, IidRenderTarget);
        try
        {
            nint brushRaw = QueryRaw(brush, IidBrush);
            try
            {
                nint* vtable = *(nint**)target;
                ((delegate* unmanaged[MemberFunction]<nint, D2dRoundedRect*, nint, void>)vtable[RenderTargetSlotFillRoundedRectangle])(
                    target, roundedRect, brushRaw);
            }
            finally
            {
                Marshal.Release(brushRaw);
            }
        }
        finally
        {
            Marshal.Release(target);
        }
    }

    /// <summary>Con trỏ interface (đã AddRef) của wrapper COM do ComWrappers sinh.</summary>
    private static nint QueryRaw(object wrapper, in Guid iid)
    {
        if (!ComWrappers.TryGetComInstance(wrapper, out nint unknown))
        {
            throw new ArgumentException("Not a COM wrapper.", nameof(wrapper));
        }

        try
        {
            return ComInterop.QueryInterface(unknown, iid);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}

internal static partial class GraphicsKernel32
{
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x102;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObjectEx(nint handle, uint milliseconds, [MarshalAs(UnmanagedType.Bool)] bool alertable);
}
