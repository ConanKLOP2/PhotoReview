using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-13b: Direct3D 11 - chỉ phần D2D cần (device BGRA để có DXGI device cho D2D + texture đích). Context không
// khai báo (ID3D11DeviceContext ~115 slot, renderer không cần: D2D tự dùng context của nó).
// Số "[n]" = slot vtable theo d3d11.h.

internal enum D3DDriverType : uint
{
    Unknown = 0,
    Hardware = 1,
    Reference = 2,
    Null = 3,
    Software = 4,
    Warp = 5,
}

internal static class D3D11Constants
{
    public const uint SdkVersion = 7;
    public const uint CreateDeviceSingleThreaded = 0x1;
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;
    public const uint UsageDefault = 0;
    public const int FeatureLevel110 = 0xb000;
    public const int FeatureLevel101 = 0xa100;
    public const int FeatureLevel100 = 0xa000;
    public const int FeatureLevel93 = 0x9300;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public DxgiFormat Format;
    public DxgiSampleDesc SampleDesc;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[GeneratedComInterface]
[Guid(GraphicsGuids.D3D11Device)]
internal unsafe partial interface ID3D11Device
{
    [PreserveSig] int Reserved03CreateBuffer(nint desc, nint initialData, out nint buffer);                        // [3]
    [PreserveSig] int Reserved04CreateTexture1D(nint desc, nint initialData, out nint texture);                    // [4]

    /// <summary>[5] <paramref name="texture"/> là ID3D11Texture2D* thô (QueryInterface sang IDXGISurface qua ComInterop).</summary>
    [PreserveSig] int CreateTexture2D(D3D11Texture2DDesc* desc, nint initialData, out nint texture);

    [PreserveSig] int Reserved06CreateTexture3D(nint desc, nint initialData, out nint texture);                    // [6]
    [PreserveSig] int Reserved07CreateShaderResourceView(nint resource, nint desc, out nint view);                 // [7]
    [PreserveSig] int Reserved08CreateUnorderedAccessView(nint resource, nint desc, out nint view);                // [8]
    [PreserveSig] int Reserved09CreateRenderTargetView(nint resource, nint desc, out nint view);                   // [9]
    [PreserveSig] int Reserved10CreateDepthStencilView(nint resource, nint desc, out nint view);                   // [10]
    [PreserveSig] int Reserved11CreateInputLayout(nint a, uint b, nint c, nuint d, out nint e);                    // [11]
    [PreserveSig] int Reserved12CreateVertexShader(nint a, nuint b, nint c, out nint d);                           // [12]
    [PreserveSig] int Reserved13CreateGeometryShader(nint a, nuint b, nint c, out nint d);                         // [13]
    [PreserveSig] int Reserved14CreateGeometryShaderWithStreamOutput(nint a, nuint b, nint c, uint d, nint e, uint f, uint g, nint h, out nint i); // [14]
    [PreserveSig] int Reserved15CreatePixelShader(nint a, nuint b, nint c, out nint d);                            // [15]
    [PreserveSig] int Reserved16CreateHullShader(nint a, nuint b, nint c, out nint d);                             // [16]
    [PreserveSig] int Reserved17CreateDomainShader(nint a, nuint b, nint c, out nint d);                           // [17]
    [PreserveSig] int Reserved18CreateComputeShader(nint a, nuint b, nint c, out nint d);                          // [18]
    [PreserveSig] int Reserved19CreateClassLinkage(out nint a);                                                    // [19]
    [PreserveSig] int Reserved20CreateBlendState(nint a, out nint b);                                              // [20]
    [PreserveSig] int Reserved21CreateDepthStencilState(nint a, out nint b);                                       // [21]
    [PreserveSig] int Reserved22CreateRasterizerState(nint a, out nint b);                                         // [22]
    [PreserveSig] int Reserved23CreateSamplerState(nint a, out nint b);                                            // [23]
    [PreserveSig] int Reserved24CreateQuery(nint a, out nint b);                                                   // [24]
    [PreserveSig] int Reserved25CreatePredicate(nint a, out nint b);                                               // [25]
    [PreserveSig] int Reserved26CreateCounter(nint a, out nint b);                                                 // [26]
    [PreserveSig] int Reserved27CreateDeferredContext(uint a, out nint b);                                         // [27]
    [PreserveSig] int Reserved28OpenSharedResource(nint a, in Guid b, out nint c);                                 // [28]
    [PreserveSig] int Reserved29CheckFormatSupport(uint a, out uint b);                                            // [29]
    [PreserveSig] int Reserved30CheckMultisampleQualityLevels(uint a, uint b, out uint c);                         // [30]
    [PreserveSig] void Reserved31CheckCounterInfo(nint a);                                                         // [31]
    [PreserveSig] int Reserved32CheckCounter(nint a, nint b, nint c, nint d, nint e, nint f, nint g, nint h);      // [32]
    [PreserveSig] int Reserved33CheckFeatureSupport(uint a, nint b, uint c);                                       // [33]
    [PreserveSig] int Reserved34GetPrivateData(in Guid a, ref uint b, nint c);                                     // [34]
    [PreserveSig] int Reserved35SetPrivateData(in Guid a, uint b, nint c);                                         // [35]
    [PreserveSig] int Reserved36SetPrivateDataInterface(in Guid a, nint b);                                        // [36]

    /// <summary>[37] D3D_FEATURE_LEVEL (trả thẳng, không HRESULT).</summary>
    [PreserveSig] int GetFeatureLevel();

    [PreserveSig] uint Reserved38GetCreationFlags();                                                               // [38]

    /// <summary>[39] S_OK nếu thiết bị còn sống, DXGI_ERROR_DEVICE_REMOVED/RESET... nếu mất.</summary>
    [PreserveSig] int GetDeviceRemovedReason();

    /// <summary>[40] Con trỏ ID3D11DeviceContext thô (đã AddRef: người gọi Marshal.Release). Xem <see cref="D3D11.ClearStateAndFlush"/>.</summary>
    [PreserveSig] void GetImmediateContext(out nint context);
    [PreserveSig] int Reserved41SetExceptionMode(uint raiseFlags);                                                 // [41]
    [PreserveSig] uint Reserved42GetExceptionMode();                                                               // [42]
}

internal static unsafe partial class D3D11
{
    /// <summary>
    /// D3D11CreateDevice. <paramref name="adapter"/> = 0 với driverType Hardware/Warp. Context ra luôn bị bỏ
    /// (truyền NULL): D2D tự quản lý.
    /// </summary>
    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3D11CreateDeviceNative(nint adapter, D3DDriverType driverType, nint software, uint flags,
        int* featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, nint context);

    // Slot của ID3D11DeviceContext (d3d11.h): 3..6 ID3D11DeviceChild, 7.. các method pipeline.
    private const int ContextSlotClearState = 110;
    private const int ContextSlotFlush = 111;
    private const int ContextSlotGetType = 112;

    /// <summary>
    /// ClearState + Flush trên immediate context. Gọi sau khi bỏ target D2D (SetTarget(null) + release bitmap) và
    /// TRƯỚC IDXGISwapChain::ResizeBuffers: pipeline D3D có thể còn giữ ref back buffer tới khi flush (khuyến nghị
    /// của mẫu D2D 1.1). Context chỉ dùng qua vtable thô (không khai báo ~115 slot của ID3D11DeviceContext).
    /// </summary>
    public static void ClearStateAndFlush(ID3D11Device device)
    {
        device.GetImmediateContext(out nint context);
        if (context == 0)
        {
            return;
        }

        try
        {
            nint* vtable = *(nint**)context;
            ((delegate* unmanaged[MemberFunction]<nint, void>)vtable[ContextSlotClearState])(context);
            ((delegate* unmanaged[MemberFunction]<nint, void>)vtable[ContextSlotFlush])(context);
        }
        finally
        {
            Marshal.Release(context);
        }
    }

    /// <summary>D3D11_DEVICE_CONTEXT_TYPE của immediate context (0 = IMMEDIATE) - dùng để kiểm đúng số slot ở test.</summary>
    internal static uint GetImmediateContextType(ID3D11Device device)
    {
        device.GetImmediateContext(out nint context);
        try
        {
            nint* vtable = *(nint**)context;
            return ((delegate* unmanaged[MemberFunction]<nint, uint>)vtable[ContextSlotGetType])(context);
        }
        finally
        {
            Marshal.Release(context);
        }
    }

    public static int CreateDevice(D3DDriverType driverType, uint flags, out ID3D11Device? device, out int featureLevel)
    {
        int* levels = stackalloc int[]
        {
            D3D11Constants.FeatureLevel110, D3D11Constants.FeatureLevel101,
            D3D11Constants.FeatureLevel100, D3D11Constants.FeatureLevel93,
        };
        int hr = D3D11CreateDeviceNative(0, driverType, 0, flags, levels, 4, D3D11Constants.SdkVersion,
            out nint raw, out featureLevel, 0);
        device = hr >= 0 && raw != 0 ? ComInterop.Wrap<ID3D11Device>(raw) : null;
        return hr;
    }
}
