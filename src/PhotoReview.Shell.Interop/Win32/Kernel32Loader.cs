using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-18: nạp động một DLL hệ thống và lấy hàm theo ordinal (dark mode menu qua uxtheme, không công bố tên). LibraryImport.
internal static partial class Kernel32
{
    /// <summary>LoadLibraryExW với LOAD_LIBRARY_SEARCH_SYSTEM32: chỉ nạp từ System32 (không tìm trong thư mục app).</summary>
    public const uint LoadLibrarySearchSystem32 = 0x00000800;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    /// <summary>GetProcAddress theo ordinal: <paramref name="ordinal"/> nằm ở 16 bit thấp của con trỏ tên (MAKEINTRESOURCE).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    public static partial nint GetProcAddress(nint module, nint ordinal);
}
