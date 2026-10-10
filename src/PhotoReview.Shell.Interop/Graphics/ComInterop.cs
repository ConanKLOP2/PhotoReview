using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Graphics;

/// <summary>
/// WP-13b: ranh giới giữa con trỏ COM thô (từ hàm C như D3D11CreateDevice / IDXGISwapChain::GetBuffer) và
/// đối tượng <c>[GeneratedComInterface]</c>. <see cref="Release"/> trả COM ref NGAY (không đợi GC) - bắt buộc
/// cho swap chain: <c>ResizeBuffers</c> hỏng nếu còn ref tới back buffer.
/// <para>
/// QUY ƯỚC: mọi tham số ra kiểu interface được khai báo <c>out nint</c> và bọc bằng <see cref="Wrap{T}"/>. Wrapper
/// do marshaller sinh cho tham số <c>out IFoo</c> nằm trong cache chung và <c>FinalRelease</c> KHÔNG trả ref của nó
/// (đã đo: ref vẫn còn, ResizeBuffers hỏng); chỉ wrapper UniqueInstance của <see cref="Wrap{T}"/> trả ref thật.
/// Tham số VÀO kiểu interface (<c>ID2D1Brush brush</c>...) vẫn dùng kiểu interface bình thường.
/// </para>
/// </summary>
internal static class ComInterop
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>HRESULT thất bại -> <see cref="COMException"/> (dùng cho các hàm <c>[PreserveSig]</c>).</summary>
    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>
    /// Bọc con trỏ COM thô thành <typeparamref name="T"/> bằng wrapper RIÊNG (UniqueInstance - cache chung không
    /// trả lại một wrapper đã <see cref="Release"/>). CHIẾM quyền sở hữu 1 ref của <paramref name="unknown"/>:
    /// wrapper giữ ref của nó, ref truyền vào được trả lại trong hàm này.
    /// </summary>
    public static T Wrap<T>(nint unknown)
        where T : class
    {
        if (unknown == 0)
        {
            throw new ArgumentNullException(nameof(unknown));
        }

        try
        {
            object wrapper = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            return (T)wrapper;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>QueryInterface trên con trỏ thô; trả con trỏ mới (đã AddRef) hoặc ném nếu không hỗ trợ.</summary>
    public static nint QueryInterface(nint unknown, in Guid iid)
    {
        Guid copy = iid;
        Check(Marshal.QueryInterface(unknown, in copy, out nint result));
        return result;
    }

    /// <summary>Trả COM ref của wrapper ngay lập tức; gọi lại là no-op. Không dùng wrapper sau khi gọi.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper)
        {
            wrapper.FinalRelease();
        }
    }
}
