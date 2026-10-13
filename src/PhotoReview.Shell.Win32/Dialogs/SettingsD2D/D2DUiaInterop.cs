using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE UIA: các giao diện provider của UI Automation (UIAutomationCore.h) viết bằng <c>[GeneratedComInterface]</c> (tương thích Native
/// AOT, không reflection-COM, không WPF). Hàm trả HRESULT viết dạng "kết quả = giá trị trả về": bộ sinh mã đổi giá trị trả về thành
/// tham số [out] cuối và đổi ngoại lệ thành HRESULT (COMException giữ nguyên mã lỗi). Con trỏ giao diện/SAFEARRAY/BSTR trả về là
/// <see cref="nint"/> do mã tự giữ ref (người nhận giải phóng).
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    int GetProviderOptions();                       // [3] get_ProviderOptions
    nint GetPatternProvider(int patternId);         // [4] IUnknown*
    UiaVariant GetPropertyValue(int propertyId);    // [5]
    nint GetHostRawElementProvider();               // [6] IRawElementProviderSimple*
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    nint Navigate(int direction);                   // [3] IRawElementProviderFragment*
    nint GetRuntimeId();                            // [4] SAFEARRAY(int)*
    UiaRect GetBoundingRectangle();                 // [5]
    nint GetEmbeddedFragmentRoots();                // [6] SAFEARRAY*
    void SetFocus();                                // [7]
    nint GetFragmentRoot();                         // [8] IRawElementProviderFragmentRoot*
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y); // [3]
    nint GetFocus();                                   // [4]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider
{
    void Invoke();                                  // [3]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface IValueProvider
{
    void SetValue(string value);                    // [3]
    nint GetValue();                                // [4] BSTR (người nhận SysFreeString)
    int GetIsReadOnly();                            // [5] BOOL
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("36dc7aef-33e6-4691-afe1-2be7274b3d33")]
internal partial interface IRangeValueProvider
{
    void SetValue(double value);                    // [3]
    double GetValue();                              // [4]
    int GetIsReadOnly();                            // [5]
    double GetMaximum();                            // [6]
    double GetMinimum();                            // [7]
    double GetLargeChange();                        // [8]
    double GetSmallChange();                        // [9]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892")]
internal partial interface IToggleProvider
{
    void Toggle();                                  // [3]
    int GetToggleState();                           // [4] ToggleState: Off=0, On=1, Indeterminate=2
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d847d3a5-cab0-4a98-8c32-ecb45c59ad24")]
internal partial interface IExpandCollapseProvider
{
    void Expand();                                  // [3]
    void Collapse();                                // [4]
    int GetExpandCollapseState();                   // [5] Collapsed=0, Expanded=1, PartiallyExpanded=2, LeafNode=3
}

/// <summary>UiaRect (4 double: left, top, width, height) - toạ độ MÀN HÌNH.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left;
    public double Top;
    public double Width;
    public double Height;
}

/// <summary>
/// VARIANT (24 byte trên x64: vt + 3 reserved + union 16 byte). Chỉ các kiểu cần cho provider: I4, BOOL, R8, BSTR, EMPTY. App chỉ x64
/// (turbojpeg native x64); union trên x86 nhỏ hơn - spike không hỗ trợ.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaVariant
{
    public const ushort VtEmpty = 0;
    public const ushort VtI4 = 3;
    public const ushort VtR8 = 5;
    public const ushort VtBstr = 8;
    public const ushort VtBool = 11;

    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Data;
    public nint Data2;

    public static UiaVariant FromInt(int value) => new() { Vt = VtI4, Data = value };

    public static UiaVariant FromBool(bool value) => new() { Vt = VtBool, Data = value ? unchecked((nint)0xFFFF) : 0 };

    public static UiaVariant FromDouble(double value) => new() { Vt = VtR8, Data = (nint)BitConverter.DoubleToInt64Bits(value) };

    /// <summary>BSTR cấp mới; chủ sở hữu giải phóng bằng <c>VariantClear</c>.</summary>
    public static UiaVariant FromString(string value) => new() { Vt = VtBstr, Data = D2DUiaNative.SysAllocString(value) };
}

/// <summary>Các hàm native của UI Automation + OLE Automation mà provider cần (giữ ở đây để spike không sửa Shell.Interop).</summary>
internal static unsafe partial class D2DUiaNative
{
    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaReturnRawElementProvider")]
    public static partial nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);

    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaHostProviderFromHwnd")]
    public static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaRaiseAutomationEvent")]
    public static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaRaiseAutomationPropertyChangedEvent")]
    public static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int propertyId, UiaVariant oldValue, UiaVariant newValue);

    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaClientsAreListening")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UiaClientsAreListening();

    [LibraryImport("uiautomationcore.dll", EntryPoint = "UiaDisconnectProvider")]
    public static partial int UiaDisconnectProvider(nint provider);

    [LibraryImport("oleaut32.dll", EntryPoint = "SysAllocString", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SysAllocString(string value);

    [LibraryImport("oleaut32.dll", EntryPoint = "VariantClear")]
    public static partial int VariantClear(UiaVariant* variant);

    [LibraryImport("oleaut32.dll", EntryPoint = "SafeArrayCreateVector")]
    public static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

    [LibraryImport("oleaut32.dll", EntryPoint = "SafeArrayPutElement")]
    public static partial int SafeArrayPutElement(nint array, int* indices, void* value);
}
