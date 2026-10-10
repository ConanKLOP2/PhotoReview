using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-13b: Direct2D 1.1 (d2d1.h + d2d1_1.h). "[n]" = SLOT vtable (IUnknown 0..2). ReservedNN = chỗ giữ slot.
// Quy ước ABI x64 (quan trọng):
//  - Mọi method là [PreserveSig] và khai báo ĐÚNG kiểu trả của C++: HRESULT -> int, "void" -> void,
//    struct 8 byte (D2D1_SIZE_F/U) trả trong RAX -> trả thẳng struct (function pointer unmanaged lo ABI).
//    KHÔNG dùng kiểu "bỏ HRESULT" của generator cho hàm void: rax rác sẽ bị coi là HRESULT và ném giả.
//  - Struct truyền theo giá trị (D2D1_POINT_2F, D2D1_SIZE_U) khai báo như tham số struct blittable.

internal enum D2dFactoryType : uint
{
    SingleThreaded = 0,
    MultiThreaded = 1,
}

internal enum D2dAlphaMode : uint
{
    Unknown = 0,
    Premultiplied = 1,
    Straight = 2,
    Ignore = 3,
}

internal enum D2dInterpolationMode : uint
{
    NearestNeighbor = 0,
    Linear = 1,
    Cubic = 2,
    MultiSampleLinear = 3,
    Anisotropic = 4,
    HighQualityCubic = 5,
}

internal enum D2dBitmapInterpolationMode : uint
{
    NearestNeighbor = 0,
    Linear = 1,
}

internal enum D2dAntialiasMode : uint
{
    PerPrimitive = 0,
    Aliased = 1,
}

internal enum D2dTextAntialiasMode : uint
{
    Default = 0,
    ClearType = 1,
    Grayscale = 2,
    Aliased = 3,
}

internal enum D2dUnitMode : uint
{
    Dips = 0,
    Pixels = 1,
}

internal enum D2dPrimitiveBlend : uint
{
    SourceOver = 0,
    Copy = 1,
    Min = 2,
    Add = 3,
}

[Flags]
internal enum D2dBitmapOptions : uint
{
    None = 0,
    Target = 1,
    CannotDraw = 2,
    CpuRead = 4,
    GdiCompatible = 8,
}

[Flags]
internal enum D2dDrawTextOptions : uint
{
    None = 0,
    NoSnap = 1,
    Clip = 2,
    EnableColorFont = 4,
}

[Flags]
internal enum D2dMapOptions : uint
{
    None = 0,
    Read = 1,
    Write = 2,
    Discard = 4,
}

internal static class D2dConstants
{
    /// <summary>D2DERR_RECREATE_TARGET: render target/device mất, phải tạo lại toàn bộ tài nguyên.</summary>
    public const int ErrorRecreateTarget = unchecked((int)0x8899000C);
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dPointF
{
    public float X;
    public float Y;

    public D2dPointF(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dSizeF
{
    public float Width;
    public float Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dSizeU
{
    public uint Width;
    public uint Height;

    public D2dSizeU(uint width, uint height)
    {
        Width = width;
        Height = height;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dRectF
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;

    public D2dRectF(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dColorF
{
    public float R;
    public float G;
    public float B;
    public float A;

    public D2dColorF(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
}

/// <summary>D2D1_MATRIX_3X2_F (hàng-vector: x' = x*M11 + y*M21 + Dx).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D2dMatrix3x2F
{
    public float M11;
    public float M12;
    public float M21;
    public float M22;
    public float Dx;
    public float Dy;

    public static D2dMatrix3x2F Identity => new() { M11 = 1, M22 = 1 };
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dPixelFormat
{
    public DxgiFormat Format;
    public D2dAlphaMode AlphaMode;
}

/// <summary>D2D1_BITMAP_PROPERTIES1 (tham số CreateBitmap/CreateBitmapFromDxgiSurface của device context).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D2dBitmapProperties1
{
    public D2dPixelFormat PixelFormat;
    public float DpiX;
    public float DpiY;
    public D2dBitmapOptions BitmapOptions;
    public nint ColorContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dBitmapProperties
{
    public D2dPixelFormat PixelFormat;
    public float DpiX;
    public float DpiY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dFactoryOptions
{
    /// <summary>D2D1_DEBUG_LEVEL: 0 none, 1 error, 2 warning, 3 information.</summary>
    public uint DebugLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D2dMappedRect
{
    public uint Pitch;
    public byte* Bits;
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Resource)]
internal partial interface ID2D1Resource
{
    [PreserveSig] void Reserved03GetFactory(out nint factory);                                                // [3]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Image)]
internal partial interface ID2D1Image : ID2D1Resource
{
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Bitmap)]
internal unsafe partial interface ID2D1Bitmap : ID2D1Image
{
    [PreserveSig] D2dSizeF GetSize();                                                                         // [4]
    [PreserveSig] D2dSizeU GetPixelSize();                                                                    // [5]
    [PreserveSig] D2dPixelFormat GetPixelFormat();                                                            // [6]
    [PreserveSig] void GetDpi(out float dpiX, out float dpiY);                                                // [7]

    /// <summary>[8] Sao chép bitmap -> bitmap (dùng để đọc lại pixel: đích tạo với CpuRead|CannotDraw, rồi Map).</summary>
    [PreserveSig] int CopyFromBitmap(D2dPointU* destPoint, ID2D1Bitmap bitmap, D2dRectU* srcRect);

    [PreserveSig] int Reserved09CopyFromRenderTarget(nint destPoint, nint renderTarget, nint srcRect);        // [9]

    /// <summary>[10] Ghi pixel từ bộ nhớ (BGRA premultiplied) vào vùng <paramref name="destRect"/> (null = toàn bộ).</summary>
    [PreserveSig] int CopyFromMemory(D2dRectU* destRect, void* srcData, uint pitch);
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dPointU
{
    public uint X;
    public uint Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dRectU
{
    public uint Left;
    public uint Top;
    public uint Right;
    public uint Bottom;
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Bitmap1)]
internal unsafe partial interface ID2D1Bitmap1 : ID2D1Bitmap
{
    [PreserveSig] int Reserved11GetColorContext(out nint colorContext);                                       // [11]
    [PreserveSig] D2dBitmapOptions GetOptions();                                                              // [12]
    [PreserveSig] int GetSurface(out nint dxgiSurface);                                               // [13]
    [PreserveSig] int Map(D2dMapOptions options, D2dMappedRect* mappedRect);                                  // [14]
    [PreserveSig] int Unmap();                                                                                // [15]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Brush)]
internal unsafe partial interface ID2D1Brush : ID2D1Resource
{
    [PreserveSig] void SetOpacity(float opacity);                                                             // [4]
    [PreserveSig] void SetTransform(D2dMatrix3x2F* transform);                                                // [5]
    [PreserveSig] float GetOpacity();                                                                         // [6]
    [PreserveSig] void GetTransform(D2dMatrix3x2F* transform);                                                // [7]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1SolidColorBrush)]
internal unsafe partial interface ID2D1SolidColorBrush : ID2D1Brush
{
    [PreserveSig] void SetColor(D2dColorF* color);                                                            // [8]
    [PreserveSig] void GetColor(D2dColorF* color);                                                            // [9] (D2D1_COLOR_F trả qua con trỏ ẩn)
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1RenderTarget)]
internal unsafe partial interface ID2D1RenderTarget : ID2D1Resource
{
    [PreserveSig] int CreateBitmap(D2dSizeU size, void* srcData, uint pitch, D2dBitmapProperties* props, out nint bitmap); // [4]
    [PreserveSig] int Reserved05CreateBitmapFromWicBitmap(nint wicBitmapSource, nint props, out nint bitmap);  // [5]
    [PreserveSig] int Reserved06CreateSharedBitmap(in Guid riid, nint data, nint props, out nint bitmap);      // [6]
    [PreserveSig] int Reserved07CreateBitmapBrush(nint bitmap, nint bitmapBrushProps, nint brushProps, out nint brush); // [7]
    [PreserveSig] int CreateSolidColorBrush(D2dColorF* color, nint brushProperties, out nint brush); // [8]
    [PreserveSig] int Reserved09CreateGradientStopCollection(nint stops, uint count, uint gamma, uint extend, out nint collection); // [9]
    [PreserveSig] int Reserved10CreateLinearGradientBrush(nint a, nint b, nint c, out nint d);                 // [10]
    [PreserveSig] int Reserved11CreateRadialGradientBrush(nint a, nint b, nint c, out nint d);                 // [11]
    [PreserveSig] int Reserved12CreateCompatibleRenderTarget(nint a, nint b, nint c, uint d, out nint e);      // [12]
    [PreserveSig] int Reserved13CreateLayer(nint size, out nint layer);                                        // [13]
    [PreserveSig] int Reserved14CreateMesh(out nint mesh);                                                     // [14]
    [PreserveSig] void DrawLine(D2dPointF point0, D2dPointF point1, ID2D1Brush brush, float strokeWidth, nint strokeStyle); // [15]
    [PreserveSig] void DrawRectangle(D2dRectF* rect, ID2D1Brush brush, float strokeWidth, nint strokeStyle);   // [16]
    [PreserveSig] void FillRectangle(D2dRectF* rect, ID2D1Brush brush);                                        // [17]
    [PreserveSig] void Reserved18DrawRoundedRectangle(nint a, nint b, float c, nint d);                        // [18]
    [PreserveSig] void Reserved19FillRoundedRectangle(nint a, nint b);                                         // [19]
    [PreserveSig] void Reserved20DrawEllipse(nint a, nint b, float c, nint d);                                 // [20]
    [PreserveSig] void Reserved21FillEllipse(nint a, nint b);                                                  // [21]
    [PreserveSig] void Reserved22DrawGeometry(nint a, nint b, float c, nint d);                                // [22]
    [PreserveSig] void Reserved23FillGeometry(nint a, nint b, nint c);                                         // [23]
    [PreserveSig] void Reserved24FillMesh(nint a, nint b);                                                     // [24]
    [PreserveSig] void Reserved25FillOpacityMask(nint a, nint b, uint c, nint d, nint e);                      // [25]

    /// <summary>[26] Bản D2D 1.0 (interpolation chỉ Nearest/Linear). Với device context dùng <see cref="ID2D1DeviceContext.DrawBitmapEx"/>.</summary>
    [PreserveSig] void DrawBitmap(ID2D1Bitmap bitmap, D2dRectF* destinationRectangle, float opacity,
        D2dBitmapInterpolationMode interpolationMode, D2dRectF* sourceRectangle);

    [PreserveSig] void Reserved27DrawText(nint a, uint b, nint c, nint d, nint e, uint f, uint g);             // [27]

    /// <summary>[28]</summary>
    [PreserveSig] void DrawTextLayout(D2dPointF origin, IDWriteTextLayout textLayout, ID2D1Brush defaultFillBrush,
        D2dDrawTextOptions options);

    [PreserveSig] void Reserved29DrawGlyphRun(D2dPointF a, nint b, nint c, uint d);                            // [29]
    [PreserveSig] void SetTransform(D2dMatrix3x2F* transform);                                                 // [30]
    [PreserveSig] void GetTransform(D2dMatrix3x2F* transform);                                                 // [31]
    [PreserveSig] void SetAntialiasMode(D2dAntialiasMode antialiasMode);                                       // [32]
    [PreserveSig] D2dAntialiasMode GetAntialiasMode();                                                         // [33]
    [PreserveSig] void SetTextAntialiasMode(D2dTextAntialiasMode textAntialiasMode);                           // [34]
    [PreserveSig] D2dTextAntialiasMode GetTextAntialiasMode();                                                 // [35]
    [PreserveSig] void Reserved36SetTextRenderingParams(nint a);                                               // [36]
    [PreserveSig] void Reserved37GetTextRenderingParams(out nint a);                                           // [37]
    [PreserveSig] void Reserved38SetTags(ulong a, ulong b);                                                    // [38]
    [PreserveSig] void Reserved39GetTags(nint a, nint b);                                                      // [39]
    [PreserveSig] void Reserved40PushLayer(nint a, nint b);                                                    // [40]
    [PreserveSig] void Reserved41PopLayer();                                                                   // [41]
    [PreserveSig] int Flush(ulong* tag1, ulong* tag2);                                                         // [42]
    [PreserveSig] void Reserved43SaveDrawingState(nint a);                                                     // [43]
    [PreserveSig] void Reserved44RestoreDrawingState(nint a);                                                  // [44]
    [PreserveSig] void PushAxisAlignedClip(D2dRectF* clipRect, D2dAntialiasMode antialiasMode);                // [45]
    [PreserveSig] void PopAxisAlignedClip();                                                                   // [46]
    [PreserveSig] void Clear(D2dColorF* clearColor);                                                           // [47]
    [PreserveSig] void BeginDraw();                                                                            // [48]
    [PreserveSig] int EndDraw(ulong* tag1, ulong* tag2);                                                       // [49]
    [PreserveSig] D2dPixelFormat GetPixelFormat();                                                             // [50]
    [PreserveSig] void SetDpi(float dpiX, float dpiY);                                                         // [51]
    [PreserveSig] void GetDpi(out float dpiX, out float dpiY);                                                 // [52]
    [PreserveSig] D2dSizeF GetSize();                                                                          // [53]
    [PreserveSig] D2dSizeU GetPixelSize();                                                                     // [54]
    [PreserveSig] uint GetMaximumBitmapSize();                                                                 // [55]
    [PreserveSig] int Reserved56IsSupported(nint a);                                                           // [56] (BOOL trả thẳng)
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1DeviceContext)]
internal unsafe partial interface ID2D1DeviceContext : ID2D1RenderTarget
{
    [PreserveSig] int CreateBitmapEx(D2dSizeU size, void* sourceData, uint pitch, D2dBitmapProperties1* props,
        out nint bitmap);                                                                             // [57]
    [PreserveSig] int Reserved58CreateBitmapFromWicBitmap(nint a, nint b, out nint c);                         // [58]
    [PreserveSig] int Reserved59CreateColorContext(uint a, nint b, uint c, out nint d);                        // [59]
    [PreserveSig] int Reserved60CreateColorContextFromFilename(nint a, out nint b);                            // [60]
    [PreserveSig] int Reserved61CreateColorContextFromWicColorContext(nint a, out nint b);                     // [61]
    [PreserveSig] int CreateBitmapFromDxgiSurface(IDxgiSurface surface, D2dBitmapProperties1* props,
        out nint bitmap);                                                                             // [62]
    [PreserveSig] int Reserved63CreateEffect(in Guid a, out nint b);                                           // [63]
    [PreserveSig] int Reserved64CreateGradientStopCollection(nint a, uint b, uint c, uint d, uint e, uint f, out nint g); // [64]
    [PreserveSig] int Reserved65CreateImageBrush(nint a, nint b, nint c, out nint d);                          // [65]
    [PreserveSig] int Reserved66CreateBitmapBrush(nint a, nint b, nint c, out nint d);                         // [66]
    [PreserveSig] int Reserved67CreateCommandList(out nint a);                                                 // [67]
    [PreserveSig] int Reserved68IsDxgiFormatSupported(uint a);                                                 // [68] (BOOL)
    [PreserveSig] int Reserved69IsBufferPrecisionSupported(uint a);                                            // [69] (BOOL)
    [PreserveSig] int Reserved70GetImageLocalBounds(nint a, nint b);                                           // [70]
    [PreserveSig] int Reserved71GetImageWorldBounds(nint a, nint b);                                           // [71]
    [PreserveSig] int Reserved72GetGlyphRunWorldBounds(D2dPointF a, nint b, uint c, nint d);                   // [72]
    [PreserveSig] void Reserved73GetDevice(out nint a);                                                        // [73]
    [PreserveSig] void SetTarget(ID2D1Image? image);                                                           // [74]
    [PreserveSig] void GetTarget(out nint image);                                                       // [75]
    [PreserveSig] void Reserved76SetRenderingControls(nint a);                                                 // [76]
    [PreserveSig] void Reserved77GetRenderingControls(nint a);                                                 // [77]
    [PreserveSig] void SetPrimitiveBlend(D2dPrimitiveBlend primitiveBlend);                                    // [78]
    [PreserveSig] D2dPrimitiveBlend GetPrimitiveBlend();                                                       // [79]
    [PreserveSig] void SetUnitMode(D2dUnitMode unitMode);                                                      // [80]
    [PreserveSig] D2dUnitMode GetUnitMode();                                                                   // [81]
    [PreserveSig] void Reserved82DrawGlyphRun(D2dPointF a, nint b, nint c, nint d, uint e);                    // [82]
    [PreserveSig] void Reserved83DrawImage(nint a, nint b, nint c, uint d, uint e);                            // [83]
    [PreserveSig] void Reserved84DrawGdiMetafile(nint a, nint b);                                              // [84]

    /// <summary>[85] DrawBitmap của D2D 1.1: interpolation đầy đủ (HighQualityCubic...). Hai rect/ma trận 4x4 = null được.</summary>
    [PreserveSig] void DrawBitmapEx(ID2D1Bitmap bitmap, D2dRectF* destinationRectangle, float opacity,
        D2dInterpolationMode interpolationMode, D2dRectF* sourceRectangle, nint perspectiveTransform);

    [PreserveSig] void Reserved86PushLayer(nint a, nint b);                                                    // [86]
    [PreserveSig] int Reserved87InvalidateEffectInputRectangle(nint a, uint b, nint c);                        // [87]
    [PreserveSig] int Reserved88GetEffectInvalidRectangleCount(nint a, out uint b);                            // [88]
    [PreserveSig] int Reserved89GetEffectInvalidRectangles(nint a, nint b, uint c);                            // [89]
    [PreserveSig] int Reserved90GetEffectRequiredInputRectangles(nint a, nint b, nint c, uint d, nint e);      // [90]
    [PreserveSig] void Reserved91FillOpacityMask(nint a, nint b, nint c, nint d);                              // [91]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Device)]
internal partial interface ID2D1Device : ID2D1Resource
{
    [PreserveSig] int CreateDeviceContext(uint options, out nint deviceContext);                 // [4]
    [PreserveSig] int Reserved05CreatePrintControl(nint a, nint b, nint c, out nint d);                        // [5]
    [PreserveSig] void SetMaximumTextureMemory(ulong maximumInBytes);                                          // [6]
    [PreserveSig] ulong GetMaximumTextureMemory();                                                             // [7]
    [PreserveSig] void ClearResources(uint millisecondsSinceUse);                                              // [8]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Factory)]
internal partial interface ID2D1Factory
{
    [PreserveSig] int ReloadSystemMetrics();                                                                   // [3]
    [PreserveSig] void GetDesktopDpi(out float dpiX, out float dpiY);                                          // [4]
    [PreserveSig] int Reserved05CreateRectangleGeometry(nint a, out nint b);                                   // [5]
    [PreserveSig] int Reserved06CreateRoundedRectangleGeometry(nint a, out nint b);                            // [6]
    [PreserveSig] int Reserved07CreateEllipseGeometry(nint a, out nint b);                                     // [7]
    [PreserveSig] int Reserved08CreateGeometryGroup(uint a, nint b, uint c, out nint d);                       // [8]
    [PreserveSig] int Reserved09CreateTransformedGeometry(nint a, nint b, out nint c);                         // [9]
    [PreserveSig] int Reserved10CreatePathGeometry(out nint a);                                                // [10]
    [PreserveSig] int Reserved11CreateStrokeStyle(nint a, nint b, uint c, out nint d);                         // [11]
    [PreserveSig] int Reserved12CreateDrawingStateBlock(nint a, nint b, out nint c);                           // [12]
    [PreserveSig] int Reserved13CreateWicBitmapRenderTarget(nint a, nint b, out nint c);                       // [13]
    [PreserveSig] int Reserved14CreateHwndRenderTarget(nint a, nint b, out nint c);                            // [14]
    [PreserveSig] int Reserved15CreateDxgiSurfaceRenderTarget(nint a, nint b, out nint c);                     // [15]
    [PreserveSig] int Reserved16CreateDCRenderTarget(nint a, out nint b);                                      // [16]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D2D1Factory1)]
internal partial interface ID2D1Factory1 : ID2D1Factory
{
    [PreserveSig] int CreateDevice(IDxgiDevice dxgiDevice, out nint d2dDevice);                         // [17]
    [PreserveSig] int Reserved18CreateStrokeStyle1(nint a, nint b, uint c, out nint d);                        // [18]
    [PreserveSig] int Reserved19CreatePathGeometry1(out nint a);                                               // [19]
    [PreserveSig] int Reserved20CreateDrawingStateBlock1(nint a, nint b, nint c, out nint d);                  // [20]
    [PreserveSig] int Reserved21CreateGdiMetafile(nint a, out nint b);                                         // [21]
    [PreserveSig] int Reserved22RegisterEffectFromStream(in Guid a, nint b, nint c, uint d, nint e);           // [22]
    [PreserveSig] int Reserved23RegisterEffectFromString(in Guid a, nint b, nint c, uint d, nint e);           // [23]
    [PreserveSig] int Reserved24UnregisterEffect(in Guid a);                                                   // [24]
    [PreserveSig] int Reserved25GetRegisteredEffects(nint a, uint b, nint c, nint d);                          // [25]
    [PreserveSig] int Reserved26GetEffectProperties(in Guid a, out nint b);                                    // [26]
}

internal static unsafe partial class D2D1
{
    [LibraryImport("d2d1.dll", EntryPoint = "D2D1CreateFactory")]
    private static partial int D2D1CreateFactoryNative(D2dFactoryType factoryType, in Guid riid,
        D2dFactoryOptions* options, out nint factory);

    /// <summary>Tạo ID2D1Factory1 (đơn luồng hoặc đa luồng).</summary>
    public static ID2D1Factory1 CreateFactory(D2dFactoryType type, uint debugLevel = 0)
    {
        D2dFactoryOptions options = new() { DebugLevel = debugLevel };
        ComInterop.Check(D2D1CreateFactoryNative(type, GraphicsGuids.IidD2D1Factory1, &options, out nint raw));
        return ComInterop.Wrap<ID2D1Factory1>(raw);
    }
}
