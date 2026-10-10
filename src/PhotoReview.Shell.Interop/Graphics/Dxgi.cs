using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Graphics;

// WP-13b: DXGI. Sá»‘ trong comment "[n]" lÃ  SLOT vtable (IUnknown = 0..2) theo header dxgi.h / dxgi1_2.h / dxgi1_3.h.
// Má»i method lÃ  [PreserveSig]: HRESULT tráº£ nguyÃªn (ComInterop.Check) - khÃ´ng Ä‘á»ƒ generator nÃ©m cho hÃ m void/struct.
// ReservedNN = chá»— giá»¯ slot, KHÃ”NG Ä‘Æ°á»£c gá»i vÃ  KHÃ”NG Ä‘Æ°á»£c xoÃ¡/Ä‘á»•i thá»© tá»±.

internal enum DxgiFormat : uint
{
    Unknown = 0,
    R8G8B8A8Unorm = 28,
    B8G8R8A8Unorm = 87,
}

internal enum DxgiSwapEffect : uint
{
    Discard = 0,
    Sequential = 1,
    FlipSequential = 3,
    FlipDiscard = 4,
}

internal enum DxgiScaling : uint
{
    Stretch = 0,
    None = 1,
    AspectRatioStretch = 2,
}

internal enum DxgiAlphaMode : uint
{
    Unspecified = 0,
    Premultiplied = 1,
    Straight = 2,
    Ignore = 3,
}

internal static class DxgiConstants
{
    public const uint UsageRenderTargetOutput = 0x20;
    public const uint SwapChainFlagFrameLatencyWaitableObject = 0x40;
    public const uint PresentDoNotWait = 0x8;
    public const uint PresentTest = 0x1;
    public const int StatusOccluded = 0x087A0001;
    public const int ErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int ErrorDeviceReset = unchecked((int)0x887A0007);
    public const int ErrorWasStillDrawing = unchecked((int)0x887A000A);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSampleDesc
{
    public uint Count;
    public uint Quality;
}

/// <summary>DXGI_SWAP_CHAIN_DESC1 (48 byte trÃªn x64? khÃ´ng: 10 uint + SampleDesc = 48).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSwapChainDesc1
{
    public uint Width;
    public uint Height;
    public DxgiFormat Format;
    public int Stereo;
    public DxgiSampleDesc SampleDesc;
    public uint BufferUsage;
    public uint BufferCount;
    public DxgiScaling Scaling;
    public DxgiSwapEffect SwapEffect;
    public DxgiAlphaMode AlphaMode;
    public uint Flags;
}

/// <summary>DXGI_PRESENT_PARAMETERS (dirty rect / scroll cho Present1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiPresentParameters
{
    public uint DirtyRectsCount;
    public D2dRectL* DirtyRects;
    public D2dRectL* ScrollRect;
    public D2dPointL* ScrollOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dRectL
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2dPointL
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSurfaceDesc
{
    public uint Width;
    public uint Height;
    public DxgiFormat Format;
    public DxgiSampleDesc SampleDesc;
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiObject)]
internal partial interface IDxgiObject
{
    [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);          // [3]
    [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);             // [4]
    [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);      // [5]
    [PreserveSig] int GetParent(in Guid riid, out nint parent);                        // [6]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiDeviceSubObject)]
internal partial interface IDxgiDeviceSubObject : IDxgiObject
{
    [PreserveSig] int GetDevice(in Guid riid, out nint device);                        // [7]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiAdapter)]
internal partial interface IDxgiAdapter : IDxgiObject
{
    [PreserveSig] int Reserved07EnumOutputs(uint output, out nint outputInterface);    // [7]
    [PreserveSig] int Reserved08GetDesc(nint desc);                                    // [8]
    [PreserveSig] int Reserved09CheckInterfaceSupport(in Guid interfaceName, out long umdVersion); // [9]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiDevice)]
internal partial interface IDxgiDevice : IDxgiObject
{
    [PreserveSig] int GetAdapter(out nint adapter);                            // [7]
    [PreserveSig] int Reserved08CreateSurface(nint desc, uint numSurfaces, uint usage, nint sharedResource, nint surface); // [8]
    [PreserveSig] int Reserved09QueryResourceResidency(nint resources, nint residencyStatus, uint numResources);          // [9]
    [PreserveSig] int Reserved10SetGpuThreadPriority(int priority);                    // [10]
    [PreserveSig] int Reserved11GetGpuThreadPriority(out int priority);                // [11]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiDevice1)]
internal partial interface IDxgiDevice1 : IDxgiDevice
{
    [PreserveSig] int SetMaximumFrameLatency(uint maxLatency);                         // [12]
    [PreserveSig] int GetMaximumFrameLatency(out uint maxLatency);                     // [13]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiSurface)]
internal partial interface IDxgiSurface : IDxgiDeviceSubObject
{
    [PreserveSig] int GetDesc(out DxgiSurfaceDesc desc);                               // [8]
    [PreserveSig] int Reserved09Map(nint mappedRect, uint mapFlags);                   // [9]
    [PreserveSig] int Reserved10Unmap();                                               // [10]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiFactory)]
internal partial interface IDxgiFactory : IDxgiObject
{
    [PreserveSig] int Reserved07EnumAdapters(uint adapter, out nint adapterInterface);                       // [7]
    [PreserveSig] int Reserved08MakeWindowAssociation(nint hwnd, uint flags);                                // [8]
    [PreserveSig] int Reserved09GetWindowAssociation(out nint hwnd);                                         // [9]
    [PreserveSig] int Reserved10CreateSwapChain(nint device, nint desc, out nint swapChain);                 // [10]
    [PreserveSig] int Reserved11CreateSoftwareAdapter(nint module, out nint adapter);                        // [11]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiFactory1)]
internal partial interface IDxgiFactory1 : IDxgiFactory
{
    [PreserveSig] int Reserved12EnumAdapters1(uint adapter, out nint adapterInterface);                      // [12]
    [PreserveSig] int IsCurrent();                                                                           // [13] (BOOL)
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiFactory2)]
internal unsafe partial interface IDxgiFactory2 : IDxgiFactory1
{
    [PreserveSig] int Reserved14IsWindowedStereoEnabled();                                                   // [14]

    /// <summary>[15] <paramref name="device"/> lÃ  D3D11 device (IUnknown).</summary>
    [PreserveSig]
    int CreateSwapChainForHwnd(ID3D11Device device, nint hwnd, DxgiSwapChainDesc1* desc, nint fullscreenDesc,
        nint restrictToOutput, out nint swapChain);

    [PreserveSig] int Reserved16CreateSwapChainForCoreWindow(nint device, nint window, nint desc, nint restrictToOutput, out nint swapChain); // [16]
    [PreserveSig] int Reserved17GetSharedResourceAdapterLuid(nint resource, out long luid);                  // [17]
    [PreserveSig] int Reserved18RegisterStereoStatusWindow(nint hwnd, uint msg, out uint cookie);            // [18]
    [PreserveSig] int Reserved19RegisterStereoStatusEvent(nint evt, out uint cookie);                        // [19]
    [PreserveSig] void Reserved20UnregisterStereoStatus(uint cookie);                                        // [20]
    [PreserveSig] int Reserved21RegisterOcclusionStatusWindow(nint hwnd, uint msg, out uint cookie);         // [21]
    [PreserveSig] int Reserved22RegisterOcclusionStatusEvent(nint evt, out uint cookie);                     // [22]
    [PreserveSig] void Reserved23UnregisterOcclusionStatus(uint cookie);                                     // [23]

    /// <summary>[24] Swap chain khÃ´ng cáº§n HWND (DirectComposition) - dÃ¹ng cho smoke test offscreen.</summary>
    [PreserveSig]
    int CreateSwapChainForComposition(ID3D11Device device, DxgiSwapChainDesc1* desc, nint restrictToOutput,
        out nint swapChain);
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiSwapChain)]
internal partial interface IDxgiSwapChain : IDxgiDeviceSubObject
{
    [PreserveSig] int Present(uint syncInterval, uint flags);                                                // [8]
    [PreserveSig] int GetBuffer(uint buffer, in Guid riid, out nint surface);                                // [9]
    [PreserveSig] int Reserved10SetFullscreenState(int fullscreen, nint target);                             // [10]
    [PreserveSig] int Reserved11GetFullscreenState(out int fullscreen, out nint target);                     // [11]
    [PreserveSig] int Reserved12GetDesc(nint desc);                                                          // [12]
    [PreserveSig] int ResizeBuffers(uint bufferCount, uint width, uint height, DxgiFormat newFormat, uint swapChainFlags); // [13]
    [PreserveSig] int Reserved14ResizeTarget(nint newTargetParameters);                                      // [14]
    [PreserveSig] int Reserved15GetContainingOutput(out nint output);                                        // [15]
    [PreserveSig] int Reserved16GetFrameStatistics(nint stats);                                              // [16]
    [PreserveSig] int Reserved17GetLastPresentCount(out uint lastPresentCount);                              // [17]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiSwapChain1)]
internal unsafe partial interface IDxgiSwapChain1 : IDxgiSwapChain
{
    [PreserveSig] int GetDesc1(out DxgiSwapChainDesc1 desc);                                                 // [18]
    [PreserveSig] int Reserved19GetFullscreenDesc(nint desc);                                                // [19]
    [PreserveSig] int Reserved20GetHwnd(out nint hwnd);                                                      // [20]
    [PreserveSig] int Reserved21GetCoreWindow(in Guid refiid, out nint unk);                                 // [21]
    [PreserveSig] int Present1(uint syncInterval, uint presentFlags, DxgiPresentParameters* presentParameters); // [22]
    [PreserveSig] int Reserved23IsTemporaryMonoSupported();                                                  // [23]
    [PreserveSig] int Reserved24GetRestrictToOutput(out nint output);                                        // [24]
    [PreserveSig] int Reserved25SetBackgroundColor(nint color);                                              // [25]
    [PreserveSig] int Reserved26GetBackgroundColor(nint color);                                              // [26]
    [PreserveSig] int Reserved27SetRotation(uint rotation);                                                  // [27]
    [PreserveSig] int Reserved28GetRotation(out uint rotation);                                              // [28]
}

[GeneratedComInterface]
[Guid(GraphicsGuids.DxgiSwapChain2)]
internal unsafe partial interface IDxgiSwapChain2 : IDxgiSwapChain1
{
    [PreserveSig] int Reserved29SetSourceSize(uint width, uint height);                                      // [29]
    [PreserveSig] int Reserved30GetSourceSize(out uint width, out uint height);                              // [30]
    [PreserveSig] int SetMaximumFrameLatency(uint maxLatency);                                               // [31]
    [PreserveSig] int GetMaximumFrameLatency(out uint maxLatency);                                           // [32]

    /// <summary>[33] Tráº£ HANDLE (khÃ´ng pháº£i HRESULT) - chá»‰ há»£p lá»‡ khi táº¡o vá»›i cá» FRAME_LATENCY_WAITABLE_OBJECT; khÃ´ng Ä‘Ã³ng handle nÃ y.</summary>
    [PreserveSig] nint GetFrameLatencyWaitableObject();

    [PreserveSig] int Reserved34SetMatrixTransform(nint matrix);                                             // [34]
    [PreserveSig] int Reserved35GetMatrixTransform(nint matrix);                                             // [35]
}

internal static partial class Dxgi
{
    /// <summary>CreateDXGIFactory2(flags, riid, void**). <paramref name="flags"/> 0, hoáº·c 1 = DEBUG.</summary>
    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2")]
    public static partial int CreateDxgiFactory2(uint flags, in Guid riid, out nint factory);
}
