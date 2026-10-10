using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke kernel32.dll (LibraryImport).
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial nint GetModuleHandle(nint moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalAlloc", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalFree", SetLastError = true)]
    public static partial nint GlobalFree(nint memory);
}
