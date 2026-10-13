using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-35 GĐ2: phần user32/comctl32 mà các điều khiển bổ sung của hộp thoại Win32 gốc cần (combobox, listview report, progress,
// trackbar, tooltip, owner-draw, subclass). CHỈ THÊM (file mới, cùng lớp partial). LibraryImport (L-AOT), chỉ ở Shell.Interop (L-PINV).

internal static unsafe partial class User32
{
    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(Point* point);

    /// <summary>GetComboBoxInfo: <paramref name="info"/> trỏ tới COMBOBOXINFO (64 byte, x64), <c>cbSize</c> đã đặt.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetComboBoxInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetComboBoxInfo(nint combo, void* info);

    [LibraryImport("user32.dll", EntryPoint = "DrawFocusRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawFocusRect(nint hdc, Rect* rect);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    public static partial int FillRect(nint hdc, Rect* rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "GetParent")]
    public static partial nint GetParent(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hwnd, char* buffer, int maxCount);
}

internal static unsafe partial class Gdi32
{
    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "RoundRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll", EntryPoint = "CreatePen")]
    public static partial nint CreatePen(int style, int width, uint colorRef);
}

internal static unsafe partial class CommonControls
{
    [LibraryImport("comctl32.dll", EntryPoint = "InitCommonControlsEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(uint* controls);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc,
        nuint id, nuint refData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc,
        nuint id);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    public static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>Hằng của điều khiển common (CommCtrl.h) và owner-draw (WinUser.h). Chỉ khai báo.</summary>
internal static class ControlConstants
{
    // InitCommonControlsEx
    public const uint IccListViewClasses = 0x00000001;
    public const uint IccBarClasses = 0x00000004;
    public const uint IccProgressClass = 0x00000020;
    public const uint IccWin95Classes = 0x000000FF;
    public const uint IccStandardClasses = 0x00004000;

    // Lớp cửa sổ
    public const string ListViewClass = "SysListView32";
    public const string ProgressClass = "msctls_progress32";
    public const string TrackbarClass = "msctls_trackbar32";
    public const string TooltipClass = "tooltips_class32";

    // Thông điệp chung
    public const uint WmNotify = 0x004E;
    public const uint WmMeasureItem = 0x002C;
    public const uint WmDrawItem = 0x002B;
    public const uint WmHScroll = 0x0114;
    public const uint WmVScroll = 0x0115;
    public const uint WmUser = 0x0400;
    public const uint WmNcDestroy = 0x0082;
    public const uint WmMouseMove = 0x0200;
    public const uint WmMouseLeave = 0x02A3;
    public const uint WmThemeChanged = 0x031A;

    // NMHDR codes
    public const int NmClick = -2;
    public const int NmDblClk = -3;
    public const int NmCustomDraw = -12;
    public const int LvnItemChanged = -101;
    public const int LvnColumnClick = -108;
    public const int TtnGetDispInfo = -530; // TTN_GETDISPINFOW

    // Custom draw
    public const uint CddsPrePaint = 0x00000001;
    public const uint CddsItem = 0x00010000;
    public const uint CddsItemPrePaint = 0x00010001;
    public const uint CddsSubItem = 0x00020000;
    public const nint CdrfDoDefault = 0x00;
    public const nint CdrfNewFont = 0x02;
    public const nint CdrfSkipDefault = 0x04;
    public const nint CdrfNotifyItemDraw = 0x20;
    public const nint CdrfNotifySubItemDraw = 0x20;
    public const uint CdisSelected = 0x0001;

    // Button
    public const uint BsAutoCheckBox = 0x0003;
    public const uint BsAutoRadioButton = 0x0009;
    public const uint BsGroupBox = 0x0007;
    public const uint BmGetCheck = 0x00F0;
    public const uint BmSetCheck = 0x00F1;
    public const int BstUnchecked = 0;
    public const int BstChecked = 1;

    // Edit
    public const uint EsMultiline = 0x0004;
    public const uint EsAutoVScroll = 0x0040;
    public const uint EsReadOnly = 0x0800;
    public const uint EsWantReturn = 0x1000;
    public const uint EsNumber = 0x2000;

    // Static
    public const uint SsEtchedFrame = 0x0012;
    public const uint SsCenterImage = 0x0200;

    // Combobox
    public const uint CbsDropDownList = 0x0003;
    public const uint CbsHasStrings = 0x0200;
    public const uint CbAddString = 0x0143;
    public const uint CbGetCount = 0x0146;
    public const uint CbGetCurSel = 0x0147;
    public const uint CbResetContent = 0x014B;
    public const uint CbSetCurSel = 0x014E;
    public const uint CbShowDropDown = 0x014F;
    public const uint CbSetItemHeight = 0x0153;
    public const uint CbGetDroppedState = 0x0157;
    public const uint CbGetLbText = 0x0148;
    public const uint CbGetLbTextLen = 0x0149;
    public const int CbnSelChange = 1;
    public const int CbnDropDown = 7;
    public const int ComboBoxInfoSize = 64;
    public const int ComboBoxInfoListOffset = 56;

    // Listbox
    public const uint LbsMultipleSel = 0x0008;
    public const uint LbsExtendedSel = 0x0800;
    public const uint LbsOwnerDrawFixed = 0x0010;
    public const uint LbsHasStrings = 0x0040;
    public const uint LbSetSel = 0x0185;
    public const uint LbSetCurSel = 0x0186;
    public const uint LbGetSel = 0x0187;
    public const uint LbGetCurSel = 0x0188;
    public const uint LbGetItemRect = 0x0198;
    public const uint LbGetItemData = 0x0199;
    public const uint LbSetItemData = 0x019A;
    public const uint LbGetSelCount = 0x0190;
    public const uint LbSetItemHeight = 0x01A0;
    public const uint LbItemFromPoint = 0x01A9;
    public const uint LbSetTopIndex = 0x0197;
    public const uint LbGetTopIndex = 0x018E;
    public const uint LbSelItemRange = 0x019B;
    public const uint LbGetCount = 0x018B;
    public const uint LbDeleteString = 0x0182;
    public const uint LbInsertString = 0x0181;
    public const uint LbGetText = 0x0189;
    public const uint LbGetTextLen = 0x018A;
    public const int LbnSelChange = 1;
    public const int LbnDblClk = 2;
    public const uint OdsSelected = 0x0001;
    public const uint OdsFocus = 0x0010;
    public const uint OdsDisabled = 0x0004;
    public const int DrawItemSize = 64;
    public const int MeasureItemSize = 32;

    // Progress
    public const uint PbsSmooth = 0x01;
    public const uint PbmSetPos = 0x0402;
    public const uint PbmSetRange32 = 0x0406;
    public const uint PbmGetPos = 0x0408;
    public const uint PbmSetBarColor = 0x0409;
    public const uint PbmSetBkColor = 0x2001;

    // Trackbar
    public const uint TbsAutoTicks = 0x0001;
    public const uint TbsHorz = 0x0000;
    public const uint TbsNoTicks = 0x0010;
    public const uint TbsToolTips = 0x0100;
    public const uint TbmGetPos = 0x0400;
    public const uint TbmSetPos = 0x0405;
    public const uint TbmSetRange = 0x0406;
    public const uint TbmSetTicFreq = 0x0414;
    public const uint TbmSetPageSize = 0x0415;
    public const uint TbmSetLineSize = 0x0417;
    public const uint TbmGetRangeMin = 0x0401;
    public const uint TbmGetRangeMax = 0x0402;
    public const int TbThumbTrack = 5;
    public const int TbEndTrack = 8;

    // Tooltip
    public const uint TtsAlwaysTip = 0x01;
    public const uint TtsNoPrefix = 0x02;
    public const uint TtfIdIsHwnd = 0x0001;
    public const uint TtfSubclass = 0x0010;
    public const uint TtmActivate = 0x0401;
    public const uint TtmSetDelayTime = 0x0403;
    public const uint TtmSetTipBkColor = 0x0413;
    public const uint TtmSetTipTextColor = 0x0414;
    public const uint TtmSetMaxTipWidth = 0x0418;
    public const uint TtmPop = 0x041C;
    public const uint TtmAddTool = 0x0432; // TTM_ADDTOOLW
    public const uint TtmUpdateTipText = 0x0439; // TTM_UPDATETIPTEXTW
    public const uint TtmGetToolCount = 0x040D;
    public const int ToolInfoSize = 72;
    public const nint LpstrTextCallback = -1;

    // ListView
    public const uint LvsReport = 0x0001;
    public const uint LvsSingleSel = 0x0004;
    public const uint LvsShowSelAlways = 0x0008;
    public const uint LvsNoSortHeader = 0x8000;
    public const uint LvsExFullRowSelect = 0x00000020;
    public const uint LvsExDoubleBuffer = 0x00010000;
    public const uint LvsExLabelTip = 0x00004000;
    public const uint LvmSetBkColor = 0x1001;
    public const uint LvmGetItemCount = 0x1004;
    public const uint LvmDeleteAllItems = 0x1009;
    public const uint LvmGetNextItem = 0x100C;
    public const uint LvmEnsureVisible = 0x1013;
    public const uint LvmGetColumnWidth = 0x101D;
    public const uint LvmSetColumnWidth = 0x101E;
    public const uint LvmGetHeader = 0x101F;
    public const uint LvmSetItemState = 0x102B;
    public const uint LvmGetItemState = 0x102C;
    public const uint LvmGetSelectedCount = 0x1032;
    public const uint LvmSetExtendedStyle = 0x1036;
    public const uint LvmSetTextColor = 0x1024;
    public const uint LvmSetTextBkColor = 0x1026;
    public const uint LvmGetItemText = 0x1073; // LVM_GETITEMTEXTW
    public const uint LvmSetItemText = 0x1074; // LVM_SETITEMTEXTW
    public const uint LvmInsertColumn = 0x1061; // LVM_INSERTCOLUMNW
    public const uint LvmInsertItem = 0x104D; // LVM_INSERTITEMW
    public const uint LvmGetItem = 0x104B; // LVM_GETITEMW
    public const uint LvmSetItem = 0x104C; // LVM_SETITEMW
    public const uint LvifText = 0x0001;
    public const uint LvifParam = 0x0004;
    public const uint LvifState = 0x0008;
    public const uint LvisFocused = 0x0001;
    public const uint LvisSelected = 0x0002;
    public const uint LvcfFmt = 0x0001;
    public const uint LvcfWidth = 0x0002;
    public const uint LvcfText = 0x0004;
    public const uint LvcfSubItem = 0x0008;
    public const int LvcfmtLeft = 0;
    public const int LvcfmtRight = 1;
    public const int LvnisSelected = 0x0002;
    public const int LvniSelected = 0x0002;
    public const int LvColumnSize = 56;
    public const int LvItemSize = 88;
    public const int NmHdrSize = 24;
    public const int NmListViewSize = 88;
    public const int NmCustomDrawSize = 80;
    public const int NmLvCustomDrawSize = 104;

    // Header
    public const uint HdmGetItem = 0x120B; // HDM_GETITEMW
    public const uint HdmSetItem = 0x120C; // HDM_SETITEMW
    public const uint HdiFormat = 0x0004;
    public const int HdfSortUp = 0x0400;
    public const int HdfSortDown = 0x0200;
    public const int HdItemSize = 72;
}
