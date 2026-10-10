using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Shell.Interop.Shell;

/// <summary>POINTL (8 byte, truyền theo giá trị trong IDropTarget).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PointL
{
    public int X;
    public int Y;
}

/// <summary>DROPEFFECT_*.</summary>
[Flags]
internal enum DropEffect : uint
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
    Scroll = 0x80000000,
}

/// <summary>
/// WP-13b: IDropTarget (oleidl.h). Shell triển khai bằng <c>[GeneratedComClass]</c> rồi đăng ký qua
/// RegisterDragDrop (WP-13a/WP-14). "[n]" = SLOT vtable. Tham số hiệu ứng là con trỏ vào/ra
/// (<c>ref</c>): đặt effect trả về trước khi return.
/// </summary>
[GeneratedComInterface]
[Guid(ShellGuids.DropTarget)]
internal partial interface IDropTarget
{
    [PreserveSig] int DragEnter(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect);  // [3]
    [PreserveSig] int DragOver(uint keyState, PointL point, ref DropEffect effect);                           // [4]
    [PreserveSig] int DragLeave();                                                                            // [5]
    [PreserveSig] int Drop(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect);       // [6]
}
