namespace PhotoReview.Shell.Interop;

// WP-13a: hằng số Win32 (giá trị theo WinUser.h). Chỉ khai báo, không logic.
internal static class WindowMessages
{
    // Message (WM_*)
    public const uint WmNull = 0x0000;
    public const uint WmCreate = 0x0001;
    public const uint WmDestroy = 0x0002;
    public const uint WmMove = 0x0003;
    public const uint WmSize = 0x0005;
    public const uint WmActivate = 0x0006;
    public const uint WmSetFocus = 0x0007;
    public const uint WmKillFocus = 0x0008;
    public const uint WmEnable = 0x000A;
    public const uint WmSetText = 0x000C;
    public const uint WmPaint = 0x000F;
    public const uint WmClose = 0x0010;
    public const uint WmQuit = 0x0012;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmShowWindow = 0x0018;
    public const uint WmSettingChange = 0x001A;
    public const uint WmDisplayChange = 0x007E;
    public const uint WmActivateApp = 0x001C;
    public const uint WmSetCursor = 0x0020;
    public const uint WmMouseActivate = 0x0021;
    public const uint WmGetMinMaxInfo = 0x0024;
    public const uint WmWindowPosChanging = 0x0046;
    public const uint WmWindowPosChanged = 0x0047;
    public const uint WmContextMenu = 0x007B;
    public const uint WmNcCreate = 0x0081;
    public const uint WmNcDestroy = 0x0082;
    public const uint WmNcCalcSize = 0x0083;
    public const uint WmNcHitTest = 0x0084;
    public const uint WmNcActivate = 0x0086;
    public const uint WmGetDlgCode = 0x0087;
    public const uint WmInput = 0x00FF;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmChar = 0x0102;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmSysKeyUp = 0x0105;
    public const uint WmSysChar = 0x0106;
    public const uint WmCommand = 0x0111;
    public const uint WmSysCommand = 0x0112;
    public const uint WmTimer = 0x0113;
    public const uint WmMenuChar = 0x0120;
    public const uint WmEnterIdle = 0x0121;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmLButtonDblClk = 0x0203;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmRButtonDblClk = 0x0206;
    public const uint WmMButtonDown = 0x0207;
    public const uint WmMButtonUp = 0x0208;
    public const uint WmMButtonDblClk = 0x0209;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmXButtonDown = 0x020B;
    public const uint WmXButtonUp = 0x020C;
    public const uint WmXButtonDblClk = 0x020D;
    public const uint WmMouseHWheel = 0x020E;
    public const uint WmCaptureChanged = 0x0215;
    public const uint WmEnterSizeMove = 0x0231;
    public const uint WmExitSizeMove = 0x0232;
    public const uint WmDropFiles = 0x0233;
    public const uint WmDpiChanged = 0x02E0;
    public const uint WmDpiChangedBeforeParent = 0x02E2;
    public const uint WmDpiChangedAfterParent = 0x02E3;
    public const uint WmGetDpiScaledSize = 0x02E4;
    public const uint WmUser = 0x0400;
    public const uint WmApp = 0x8000;

    // Window style (WS_*)
    public const uint WsOverlapped = 0x00000000;
    public const uint WsPopup = 0x80000000;
    public const uint WsChild = 0x40000000;
    public const uint WsVisible = 0x10000000;
    public const uint WsCaption = 0x00C00000;
    public const uint WsSysMenu = 0x00080000;
    public const uint WsThickFrame = 0x00040000;
    public const uint WsMinimizeBox = 0x00020000;
    public const uint WsMaximizeBox = 0x00010000;
    public const uint WsOverlappedWindow = 0x00CF0000;

    // Extended style (WS_EX_*)
    public const uint WsExAcceptFiles = 0x00000010;
    public const uint WsExTopmost = 0x00000008;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExLayered = 0x00080000;
    public const uint WsExAppWindow = 0x00040000;
    public const uint WsExNoRedirectionBitmap = 0x00200000;
    public const uint WsExNoActivate = 0x08000000;

    // SetWindowPos (SWP_*) và vị trí z-order (HWND_*)
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoRedraw = 0x0008;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpHideWindow = 0x0080;
    public const uint SwpNoOwnerZOrder = 0x0200;
    public const uint SwpNoSendChanging = 0x0400;
    public const nint HwndTop = 0;
    public const nint HwndBottom = 1;
    public const nint HwndTopMost = -1;
    public const nint HwndNoTopMost = -2;

    // ShowWindow (SW_*)
    public const int SwHide = 0;
    public const int SwShowNormal = 1;
    public const int SwShowMinimized = 2;
    public const int SwShowMaximized = 3;
    public const int SwShowNoActivate = 4;
    public const int SwShow = 5;
    public const int SwMinimize = 6;
    public const int SwShowMinNoActive = 7;
    public const int SwShowNa = 8;
    public const int SwRestore = 9;
    public const int SwShowDefault = 10;

    // GetWindowLongPtr index (GWL_*/GWLP_*)
    public const int GwlExStyle = -20;
    public const int GwlStyle = -16;
    public const int GwlpWndProc = -4;
    public const int GwlpHInstance = -6;
    public const int GwlpUserData = -21;

    // GetSystemMetrics (SM_*)
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;
    public const int SmCyCaption = 4;
    public const int SmCxDoubleClk = 36;
    public const int SmCyDoubleClk = 37;
    public const int SmCxSizeFrame = 32;
    public const int SmCySizeFrame = 33;
    public const int SmCxSmIcon = 49;
    public const int SmCySmIcon = 50;
    public const int SmCxDrag = 68;
    public const int SmCyDrag = 69;

    // MonitorFromWindow/Rect/Point (MONITOR_*), MONITORINFOF_PRIMARY
    public const uint MonitorDefaultToNull = 0x00000000;
    public const uint MonitorDefaultToPrimary = 0x00000001;
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const uint MonitorInfoFPrimary = 0x00000001;

    // Menu (MF_*, TPM_*)
    public const uint MfString = 0x00000000;
    public const uint MfGrayed = 0x00000001;
    public const uint MfDisabled = 0x00000002;
    public const uint MfChecked = 0x00000008;
    public const uint MfPopup = 0x00000010;
    public const uint MfByCommand = 0x00000000;
    public const uint MfByPosition = 0x00000400;
    public const uint MfSeparator = 0x00000800;
    public const uint TpmLeftAlign = 0x0000;
    public const uint TpmRightAlign = 0x0008;
    public const uint TpmTopAlign = 0x0000;
    public const uint TpmBottomAlign = 0x0020;
    public const uint TpmLeftButton = 0x0000;
    public const uint TpmRightButton = 0x0002;
    public const uint TpmReturnCmd = 0x0100;
    public const uint TpmNonotify = 0x0080;

    // Clipboard (CF_*) và GlobalAlloc (GMEM_*)
    public const uint CfUnicodeText = 13;
    public const uint CfHDrop = 15;
    public const uint GmemMoveable = 0x0002;
    public const uint GmemZeroInit = 0x0040;
    public const uint GHnd = 0x0042;

    // PeekMessage (PM_*), MsgWaitForMultipleObjectsEx (QS_*, MWMO_*), WAIT_*
    public const uint PmNoRemove = 0x0000;
    public const uint PmRemove = 0x0001;
    public const uint PmNoYield = 0x0002;
    public const uint QsAllInput = 0x04FF;
    public const uint MwmoAlertable = 0x0002;
    public const uint MwmoInputAvailable = 0x0004;
    public const uint WaitObject0 = 0x00000000;
    public const uint WaitFailed = 0xFFFFFFFF;
    public const uint InfiniteWait = 0xFFFFFFFF;

    // MessageBox/dialog return (ID*)
    public const int IdOk = 1;
    public const int IdCancel = 2;
    public const int IdYes = 6;
    public const int IdNo = 7;
}
