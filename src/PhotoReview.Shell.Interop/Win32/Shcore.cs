using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke shcore.dll (LibraryImport).
internal static unsafe partial class Shcore
{
    public const int MdtEffectiveDpi = 0;

    /// <summary>HRESULT GetDpiForMonitor (dpiType: MDT_*).</summary>
    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, uint* dpiX, uint* dpiY);
}
