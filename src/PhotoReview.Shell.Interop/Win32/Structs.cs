using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: struct khai báo đúng bố cục Win32 SDK (x64). Mọi struct phải blittable và có kích thước ghi trong InteropSignatureTests.
// Không thêm field nào mà hàm gọi không cần.

/// <summary>RECT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>POINT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

/// <summary>WNDCLASSEXW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WndClassEx
{
    public uint Size;
    public uint Style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public char* MenuName;
    public char* ClassName;
    public nint IconSmall;
}

/// <summary>MSG.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int PointX;
    public int PointY;
    public uint Private;
}

/// <summary>MONITORINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public uint Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
}

/// <summary>MONITORINFOEX (cbSize phải = sizeof(MonitorInfoEx) trước khi gọi GetMonitorInfoW).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MonitorInfoEx
{
    public uint Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
    public fixed char Device[32];
}

/// <summary>WINDOWPLACEMENT (length phải = sizeof trước khi gọi Get/SetWindowPlacement).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    public uint Length;
    public uint Flags;
    public uint ShowCmd;
    public Point MinPosition;
    public Point MaxPosition;
    public Rect NormalPosition;
}

/// <summary>INPUT_MESSAGE_SOURCE (GetCurrentInputMessageSource: touchpad/chuột/bút).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputMessageSource
{
    public uint DeviceType;
    public uint OriginId;
}

/// <summary>TASKDIALOG_BUTTON.</summary>
// commctrl.h bao TASKDIALOG_BUTTON/TASKDIALOGCONFIG trong pshpack1.h: Pack = 1 (sửa lỗi bố cục của WP-13a, phát hiện ở WP-19a: E_INVALIDARG).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct TaskDialogButton
{
    public int Id;
    public char* Text;
}

/// <summary>TASKDIALOGCONFIG. Các trường chuỗi là PCWSTR (con trỏ do bên gọi giữ sống); union icon là nint.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct TaskDialogConfig
{
    public uint Size;
    public nint ParentWindow;
    public nint Instance;
    public uint Flags;
    public uint CommonButtons;
    public char* WindowTitle;
    public nint MainIcon;
    public char* MainInstruction;
    public char* Content;
    public uint ButtonCount;
    public TaskDialogButton* Buttons;
    public int DefaultButton;
    public uint RadioButtonCount;
    public nint RadioButtons;
    public int DefaultRadioButton;
    public char* VerificationText;
    public char* ExpandedInformation;
    public char* ExpandedControlText;
    public char* CollapsedControlText;
    public nint FooterIcon;
    public char* Footer;
    public delegate* unmanaged<nint, uint, nuint, nint, nint, int> Callback;
    public nint CallbackData;
    public uint Width;
}

