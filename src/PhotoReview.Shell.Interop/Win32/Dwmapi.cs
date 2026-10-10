using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke dwmapi.dll (LibraryImport). Giá trị DWMWA_* theo dwmapi.h.
internal static unsafe partial class Dwmapi
{
    public const uint DwmwaNcRenderingPolicy = 2;
    public const uint DwmwaTransitionsForceDisabled = 3;
    public const uint DwmwaCloak = 13;
    public const uint DwmwaUseImmersiveDarkMode = 20;
    public const uint DwmwaWindowCornerPreference = 33;
    public const uint DwmwaBorderColor = 34;
    public const uint DwmwaCaptionColor = 35;

    /// <summary>HRESULT DwmSetWindowAttribute. <paramref name="value"/> trỏ tới giá trị có kích thước <paramref name="size"/> byte.</summary>
    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}
