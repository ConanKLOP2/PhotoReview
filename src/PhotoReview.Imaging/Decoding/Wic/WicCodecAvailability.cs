using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Core.Catalog;

namespace PhotoReview.Imaging.Decoding.Wic;

/// <summary>
/// Which optional Windows codecs this PC has for the WIC-decoded extra formats (Q-FMT-WEBP-HEIC).
/// <see cref="WebP"/>: a WIC decoder for the WebP container is registered (Windows 11 / the "WebP Image Extensions"
/// Store package). <see cref="Heif"/>: a WIC decoder for the HEIF container is registered ("HEIF Image Extensions")
/// AND a Media Foundation HEVC video decoder exists ("HEVC Video Extensions" or a device-maker equivalent): the HEIF
/// codec only parses the container and hands the HEVC-coded pixels to that video decoder.
/// </summary>
public sealed record WicCodecSupport(bool WebP, bool HeifContainer, bool HevcDecoder, string Detail)
{
    /// <summary>A HEIC/HEIF photo can be decoded only with both the container codec and the HEVC decoder.</summary>
    public bool Heif => HeifContainer && HevcDecoder;

    /// <summary>Nothing is available (used when the probe itself failed).</summary>
    public static WicCodecSupport None(string detail) => new(false, false, false, detail);

    /// <summary>True when the codec needed by <paramref name="format"/> is present (<see cref="WicImageFormat.None"/>: true).</summary>
    public bool Supports(WicImageFormat format) => format switch
    {
        WicImageFormat.WebP => WebP,
        WicImageFormat.Heif => Heif,
        _ => true,
    };
}

/// <summary>
/// Runtime probe for the optional WIC codecs: enumerates the registered WIC bitmap decoders
/// (<c>IWICImagingFactory::CreateComponentEnumerator</c>, which also lists Microsoft Store packaged codecs) and asks
/// Media Foundation for an HEVC video decoder (<c>MFTEnumEx</c>). Runs once per process, lazily, on the first WebP/HEIC
/// path or Settings visit -- never on the startup path. A codec installed while the app runs is seen after a restart.
/// </summary>
public static partial class WicCodecAvailability
{
    /// <summary>GUID_ContainerFormatWebp (wincodec.h).</summary>
    public static readonly Guid ContainerFormatWebp = new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");

    /// <summary>GUID_ContainerFormatHeif (wincodec.h).</summary>
    public static readonly Guid ContainerFormatHeif = new("e1e62521-6787-405b-a339-500715b5763f");

    private static readonly Lazy<WicCodecSupport> s_probe = new(() => Probe(EnumerateDecoders, HasMediaFoundationDecoder), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The cached probe result of this process.</summary>
    public static WicCodecSupport Current => s_probe.Value;

    /// <summary>One registered WIC decoder: its container format and (for diagnostics) its name and extensions.</summary>
    internal readonly record struct DecoderEntry(Guid ContainerFormat, string FriendlyName, string FileExtensions);

    /// <summary>Probe seam: <paramref name="enumerateDecoders"/> lists the WIC decoders, <paramref name="hasVideoDecoder"/> answers
    /// "is there an MF decoder for this video subtype". Any exception from either means "not available", never a failure.</summary>
    internal static WicCodecSupport Probe(Func<IReadOnlyList<DecoderEntry>> enumerateDecoders, Func<Guid, bool> hasVideoDecoder)
    {
        IReadOnlyList<DecoderEntry> decoders;
        try
        {
            decoders = enumerateDecoders();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WicCodecSupport.None("WIC decoder enumeration failed: " + ex.Message);
        }

        var webp = decoders.Any(d => d.ContainerFormat == ContainerFormatWebp);
        var heifContainer = decoders.Any(d => d.ContainerFormat == ContainerFormatHeif);
        bool hevc;
        string hevcDetail;
        try
        {
            hevc = hasVideoDecoder(MediaSubtypeHevc);
            hevcDetail = hevc ? "found" : "not found";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            hevc = false;
            hevcDetail = "probe failed: " + ex.Message;
        }

        var detail = $"WIC decoders: {decoders.Count}; WebP decoder: {(webp ? "found" : "not found")}; HEIF decoder: {(heifContainer ? "found" : "not found")}; HEVC video decoder: {hevcDetail}";
        return new WicCodecSupport(webp, heifContainer, hevc, detail);
    }

    // ---- WIC component enumeration ----

    private const uint WicDecoderComponent = 0x1; // WICDecoder
    private const uint WicComponentEnumerateDefault = 0x0;

    internal static IReadOnlyList<DecoderEntry> EnumerateDecoders()
    {
        int hr = WicNativeMethods.WICCreateImagingFactory_Proxy(WicNativeMethods.WINCODEC_SDK_VERSION1, out nint rawFactory);
        if (hr < 0 || rawFactory == 0)
        {
            if (rawFactory != 0) Marshal.Release(rawFactory);
            throw Marshal.GetExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005), new IntPtr(-1))!;
        }

        var factory = WicCom.Wrap<IWICImagingFactory>(rawFactory);
        var result = new List<DecoderEntry>();
        IEnumUnknown? enumerator = null;
        try
        {
            factory.CreateComponentEnumerator(WicDecoderComponent, WicComponentEnumerateDefault, out nint enumPtr);
            enumerator = WicCom.Wrap<IEnumUnknown>(enumPtr);
            while (enumerator.Next(1, out nint item, out uint fetched) == 0 && fetched == 1)
            {
                IWICBitmapCodecInfo? info = null;
                try
                {
                    // Not every enumerated component is a codec info: QueryInterface instead of a cast keeps "no" cheap (E_NOINTERFACE).
                    var codecInfoIid = typeof(IWICBitmapCodecInfo).GUID;
                    if (Marshal.QueryInterface(item, in codecInfoIid, out nint infoPointer) >= 0 && infoPointer != 0)
                    {
                        info = WicCom.Wrap<IWICBitmapCodecInfo>(infoPointer);
                        info.GetContainerFormat(out Guid container);
                        result.Add(new DecoderEntry(container, ReadString(info.GetFriendlyName), ReadString(info.GetFileExtensions)));
                    }
                }
                catch (COMException)
                {
                    // One broken third-party codec registration must not hide the others.
                }
                finally
                {
                    WicCom.Release(info);
                    if (item != 0) Marshal.Release(item);
                }
            }
        }
        finally
        {
            WicCom.Release(enumerator);
            WicCom.Release(factory);
        }

        return result;
    }

    private delegate void StringGetter(uint cch, nint buffer, out uint actual);

    private static string ReadString(StringGetter getter)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            getter(0, 0, out uint length);
            if (length is 0 or > 4096) return string.Empty;
            buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            getter(length, buffer, out _);
            return Marshal.PtrToStringUni(buffer, (int)length).TrimEnd('\0');
        }
        catch (COMException)
        {
            return string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Media Foundation decoder enumeration ----

    /// <summary>MFVideoFormat_HEVC ('HEVC' FourCC subtype).</summary>
    internal static readonly Guid MediaSubtypeHevc = new("43564548-0000-0010-8000-00aa00389b71");

    private static readonly Guid MftCategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");

    // MFT_ENUM_FLAG_SYNCMFT | ASYNCMFT | HARDWARE | LOCALMFT | SORTANDFILTER: every decoder an app could actually use.
    private const uint MftEnumFlags = 0x1 | 0x2 | 0x4 | 0x10 | 0x40;

    internal static bool HasMediaFoundationDecoder(Guid videoSubtype)
    {
        var input = new MftRegisterTypeInfo { MajorType = MediaTypeVideo, SubType = videoSubtype };
        int hr = MFTEnumEx(MftCategoryVideoDecoder, MftEnumFlags, ref input, 0, out nint activates, out uint count);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
        try
        {
            for (var i = 0; i < count; i++)
            {
                var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                if (activate != IntPtr.Zero) Marshal.Release(activate);
            }
        }
        finally
        {
            if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates);
        }

        return count > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid SubType;
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFTEnumEx(Guid guidCategory, uint flags, ref MftRegisterTypeInfo inputType, nint outputType,
        out nint pppMftActivate, out uint pnumMftActivate);
}

// IEnumUnknown (objidl.h): Next hands out raw IUnknown pointers, each one reference the caller releases.
[GeneratedComInterface]
[Guid("00000100-0000-0000-C000-000000000046")]
internal partial interface IEnumUnknown
{
    [PreserveSig]
    int Next(uint celt, out nint rgelt, out uint pceltFetched);                       // [3]
    [PreserveSig]
    int Skip(uint celt);                                                              // [4]
    void Reset();                                                                     // [5]
    void Clone(out nint ppenum);                                                      // [6]
}

// IWICBitmapCodecInfo (wincodec.h); the IWICComponentInfo slots this probe never calls are placeholders that only keep
// the vtable order right.
[GeneratedComInterface]
[Guid("E87A44C4-B76E-4C47-8B09-298EB12A2714")]
internal partial interface IWICBitmapCodecInfo
{
    void GetComponentType(out uint pType);
    void GetCLSID(out Guid pclsid);
    void GetSigningStatus(out uint pStatus);
    void GetAuthor(uint cchAuthor, nint wzAuthor, out uint pcchActual);
    void GetVendorGUID(out Guid pguidVendor);
    void GetVersion(uint cchVersion, nint wzVersion, out uint pcchActual);
    void GetSpecVersion(uint cchSpecVersion, nint wzSpecVersion, out uint pcchActual);
    void GetFriendlyName(uint cchFriendlyName, nint wzFriendlyName, out uint pcchActual);
    void GetContainerFormat(out Guid pguidContainerFormat);
    void GetPixelFormats(uint cFormats, nint pguidPixelFormats, out uint pcActual);
    void GetColorManagementVersion(uint cchColorManagementVersion, nint wzColorManagementVersion, out uint pcchActual);
    void GetDeviceManufacturer(uint cchDeviceManufacturer, nint wzDeviceManufacturer, out uint pcchActual);
    void GetDeviceModels(uint cchDeviceModels, nint wzDeviceModels, out uint pcchActual);
    void GetMimeTypes(uint cchMimeTypes, nint wzMimeTypes, out uint pcchActual);
    void GetFileExtensions(uint cchFileExtensions, nint wzFileExtensions, out uint pcchActual);
}
