using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke gdi32.dll (LibraryImport). BlackBrush giữ tên WP-01.
internal static partial class Gdi32
{
    public const int BlackBrush = 4;

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial nint GetStockObject(int index);
}
