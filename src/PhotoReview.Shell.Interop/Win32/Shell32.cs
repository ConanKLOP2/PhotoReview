using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke shell32.dll (dự phòng cho kéo-thả file kiểu cũ, WM_DROPFILES).
internal static partial class Shell32
{
    [LibraryImport("shell32.dll", EntryPoint = "DragAcceptFiles")]
    public static partial void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);
}
