using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke comctl32.dll (TaskDialogIndirect). Cần manifest Common-Controls v6 (đã khai báo ở WP-01).
internal static unsafe partial class Comctl32
{
    // TDCBF_* (TASKDIALOG_COMMON_BUTTON_FLAGS)
    public const uint TdcbfOkButton = 0x0001;
    public const uint TdcbfYesButton = 0x0002;
    public const uint TdcbfNoButton = 0x0004;
    public const uint TdcbfCancelButton = 0x0008;
    public const uint TdcbfRetryButton = 0x0010;
    public const uint TdcbfCloseButton = 0x0020;

    // TDF_* (TASKDIALOG_FLAGS)
    public const uint TdfEnableHyperlinks = 0x0001;
    public const uint TdfUseHIconMain = 0x0002;
    public const uint TdfUseHIconFooter = 0x0004;
    public const uint TdfAllowDialogCancellation = 0x0008;
    public const uint TdfUseCommandLinks = 0x0010;
    public const uint TdfExpandedByDefault = 0x0080;
    public const uint TdfVerificationFlagChecked = 0x0100;
    public const uint TdfPositionRelativeToWindow = 0x1000;
    public const uint TdfRtlLayout = 0x2000;
    public const uint TdfNoDefaultRadioButton = 0x4000;
    public const uint TdfCanBeMinimized = 0x8000;
    public const uint TdfSizeToContent = 0x1000000;

    // TD_*_ICON (MAKEINTRESOURCE(-n)) cho MainIcon/FooterIcon
    public const nint TdWarningIcon = -1;
    public const nint TdErrorIcon = -2;
    public const nint TdInformationIcon = -3;
    public const nint TdShieldIcon = -4;

    /// <summary>HRESULT TaskDialogIndirect. Các con trỏ nullable (button/radio/verification) có thể là null.</summary>
    [LibraryImport("comctl32.dll", EntryPoint = "TaskDialogIndirect")]
    public static partial int TaskDialogIndirect(TaskDialogConfig* config, int* button, int* radioButton, int* verificationChecked);
}
