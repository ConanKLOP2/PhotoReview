using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>Loại hộp thoại thông báo: quyết định nút bấm và biểu tượng (khớp <c>WpfDialogService</c>).</summary>
internal enum TaskDialogKind
{
    /// <summary>Có/Không (không biểu tượng, như MessageBoxImage.Question của bản WPF khi shell mới chỉ có biểu tượng hệ thống).</summary>
    Confirmation,

    /// <summary>OK + biểu tượng thông tin.</summary>
    Information,

    /// <summary>OK + biểu tượng cảnh báo (bản WPF dùng <c>MessageBoxImage.Warning</c> cho ShowError).</summary>
    Error,
}

/// <summary>Seam cho <c>TaskDialogIndirect</c>: test thay bằng fake, không mở hộp thoại thật (WP-19a).</summary>
internal interface ITaskDialogPort
{
    /// <summary>Trả ID nút (<c>IDYES</c>/<c>IDNO</c>/<c>IDOK</c>/<c>IDCANCEL</c>); đóng bằng X/Esc/Alt+F4 trả <c>IDCANCEL</c>.</summary>
    int Show(nint owner, string title, string message, TaskDialogKind kind);
}

/// <summary>
/// <c>TaskDialogIndirect</c> (comctl32 v6 qua manifest, WP-01). Chạy vòng lặp modal lồng của chính hộp thoại trên luồng gọi.
/// </summary>
internal sealed unsafe class NativeTaskDialogPort : ITaskDialogPort
{
    /// <summary>TDN_CREATED: hộp thoại đã tạo xong, chưa hiện (thông báo callback).</summary>
    private const uint TdnCreated = 0;

    private readonly Action<nint>? _onCreated;

    /// <summary>Lý do lần <see cref="Show"/> gần nhất không hiện được hộp thoại (null nếu hiện bình thường); để ghi nhật ký/chẩn đoán.</summary>
    internal string? LastFailure { get; private set; }

    public NativeTaskDialogPort() : this(null)
    {
    }

    /// <summary>Chỉ test: <paramref name="onCreated"/> nhận HWND hộp thoại ngay khi tạo (để test đóng nó bằng PostMessage).</summary>
    internal NativeTaskDialogPort(Action<nint>? onCreated) => _onCreated = onCreated;

    public int Show(nint owner, string title, string message, TaskDialogKind kind)
    {
        var handle = GCHandle.Alloc(this);
        try
        {
            fixed (char* titlePtr = title)
            fixed (char* messagePtr = message)
            {
                var config = new TaskDialogConfig
                {
                    Size = (uint)sizeof(TaskDialogConfig),
                    ParentWindow = owner,
                    // Cho phép Esc/Alt+F4/nút X kể cả khi chỉ có Có/Không (trả IDCANCEL).
                    Flags = Comctl32.TdfAllowDialogCancellation,
                    CommonButtons = kind == TaskDialogKind.Confirmation
                        ? Comctl32.TdcbfYesButton | Comctl32.TdcbfNoButton
                        : Comctl32.TdcbfOkButton,
                    WindowTitle = titlePtr,
                    Content = messagePtr,
                    MainIcon = kind switch
                    {
                        TaskDialogKind.Information => Comctl32.TdInformationIcon,
                        TaskDialogKind.Error => Comctl32.TdWarningIcon,
                        _ => 0,
                    },
                    Callback = &OnCallback,
                    CallbackData = GCHandle.ToIntPtr(handle),
                };
                var pressed = 0;
                try
                {
                    var hr = Comctl32.TaskDialogIndirect(&config, &pressed, null, null);
                    if (hr == 0) return pressed;
                    LastFailure = $"HRESULT 0x{hr:X8}";
                    return WindowMessages.IdCancel;
                }
                catch (EntryPointNotFoundException)
                {
                    // Tiến trình không khai manifest comctl32 v6 (vd. testhost): coi như người dùng đóng hộp thoại.
                    LastFailure = "TaskDialogIndirect không có (comctl32 v5)";
                    return WindowMessages.IdCancel;
                }
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnCallback(nint hwnd, uint notification, nuint wParam, nint lParam, nint data)
    {
        if (notification == TdnCreated && data != 0 && GCHandle.FromIntPtr(data).Target is NativeTaskDialogPort port)
        {
            try
            {
                port._onCreated?.Invoke(hwnd);
            }
            catch (Exception)
            {
                // Ngoại lệ không được vượt qua ranh giới unmanaged; hook chỉ dành cho test.
            }
        }

        return 0; // S_OK
    }
}

