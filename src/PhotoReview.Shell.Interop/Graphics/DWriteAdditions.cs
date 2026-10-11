using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-17: phần interop DirectWrite mà WP-13b chưa khai báo. CHỈ THÊM (file mới) - không đổi khai báo/slot của WP-13b.
//  - IDWriteFactory::CreateEllipsisTrimmingSign là slot [20], WP-13b giữ chỗ bằng Reserved20CreateEllipsisTrimmingSign(nint, out nint);
//    ở đây gọi slot đó qua vtable thô với đúng chữ ký dwrite.h (IDWriteTextFormat*, IDWriteInlineObject**), cùng cách
//    D2D1RawCalls.FillRoundedRectangle. Test chạy thật: DWriteTextRendererTests (cắt "…" khi vượt MaxWidth).

internal static unsafe class DWriteRawCalls
{
    /// <summary>Slot vtable của IDWriteFactory::CreateEllipsisTrimmingSign (dwrite.h; IUnknown 0..2).</summary>
    internal const int FactorySlotCreateEllipsisTrimmingSign = 20;

    private static readonly Guid IidTextFormat = new(GraphicsGuids.DWriteTextFormat);
    private static readonly Guid IidFactory = new(GraphicsGuids.DWriteFactory);

    /// <summary>
    /// Tạo "inline object" dấu cắt "…" theo font của <paramref name="textFormat"/> (IDWriteTextFormat hoặc IDWriteTextLayout).
    /// Trả con trỏ IDWriteInlineObject đã AddRef: người gọi truyền cho <c>SetTrimming</c> rồi <see cref="Marshal.Release"/>.
    /// </summary>
    public static nint CreateEllipsisTrimmingSign(IDWriteFactory factory, object textFormat)
    {
        nint factoryRaw = QueryRaw(factory, IidFactory);
        try
        {
            nint formatRaw = QueryRaw(textFormat, IidTextFormat);
            try
            {
                nint* vtable = *(nint**)factoryRaw;
                nint sign;
                int hr = ((delegate* unmanaged[MemberFunction]<nint, nint, nint*, int>)vtable[FactorySlotCreateEllipsisTrimmingSign])(
                    factoryRaw, formatRaw, &sign);
                ComInterop.Check(hr);
                return sign;
            }
            finally
            {
                Marshal.Release(formatRaw);
            }
        }
        finally
        {
            Marshal.Release(factoryRaw);
        }
    }

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
