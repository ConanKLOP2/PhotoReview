using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Shell;

// WP-13b: OLE IDataObject (objidl.h) - đọc nội dung kéo-thả (CF_HDROP) và đặt dữ liệu kéo ra. "[n]" = SLOT vtable.

internal static class ClipboardFormats
{
    public const ushort HDrop = 15;
    public const ushort UnicodeText = 13;
}

[Flags]
internal enum Tymed : uint
{
    Null = 0,
    HGlobal = 1,
    File = 2,
    IStream = 4,
    IStorage = 8,
}

internal static class DataViewAspect
{
    public const uint Content = 1;
}

internal static class DataObjectDirection
{
    public const uint Get = 1;
    public const uint Set = 2;
}

/// <summary>DV_E_FORMATETC: định dạng không có trong data object.</summary>
internal static class OleErrors
{
    public const int DvEFormatEtc = unchecked((int)0x80040064);
    public const int DragDropSEffectNone = 0;
}

/// <summary>FORMATETC (32 byte trên x64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FormatEtc
{
    public ushort ClipboardFormat;
    public nint TargetDevice;
    public uint Aspect;
    public int Index;
    public Tymed Medium;
}

/// <summary>STGMEDIUM (24 byte trên x64). <see cref="Handle"/> là union (HGLOBAL/IStream*/...).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StgMedium
{
    public Tymed Medium;
    public nint Handle;
    public nint ReleaseUnknown;
}

[GeneratedComInterface]
[Guid(ShellGuids.DataObject)]
internal unsafe partial interface IDataObject
{
    [PreserveSig] int GetData(FormatEtc* format, StgMedium* medium);                                          // [3] người gọi ReleaseStgMedium
    [PreserveSig] int Reserved04GetDataHere(FormatEtc* format, StgMedium* medium);                            // [4]
    [PreserveSig] int QueryGetData(FormatEtc* format);                                                        // [5] S_OK = có
    [PreserveSig] int Reserved06GetCanonicalFormatEtc(FormatEtc* a, FormatEtc* b);                            // [6]
    [PreserveSig] int SetData(FormatEtc* format, StgMedium* medium, int release);                             // [7]
    [PreserveSig] int Reserved08EnumFormatEtc(uint direction, out nint enumerator);                           // [8]
    [PreserveSig] int Reserved09DAdvise(FormatEtc* a, uint b, nint c, out uint d);                            // [9]
    [PreserveSig] int Reserved10DUnadvise(uint a);                                                            // [10]
    [PreserveSig] int Reserved11EnumDAdvise(out nint a);                                                      // [11]
}

internal static partial class OleNative
{
    [LibraryImport("ole32.dll", EntryPoint = "ReleaseStgMedium")]
    public static unsafe partial void ReleaseStgMedium(StgMedium* medium);
}
