using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke ole32.dll (LibraryImport). RegisterDragDrop cần OleInitialize trên STA của cửa sổ.
internal static partial class Ole32
{
    [LibraryImport("ole32.dll", EntryPoint = "OleInitialize")]
    public static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll", EntryPoint = "OleUninitialize")]
    public static partial void OleUninitialize();

    /// <summary>HRESULT RegisterDragDrop. <paramref name="dropTarget"/> là IDropTarget* (WP-13b cung cấp vtable).</summary>
    [LibraryImport("ole32.dll", EntryPoint = "RegisterDragDrop")]
    public static partial int RegisterDragDrop(nint hwnd, nint dropTarget);

    [LibraryImport("ole32.dll", EntryPoint = "RevokeDragDrop")]
    public static partial int RevokeDragDrop(nint hwnd);
}
