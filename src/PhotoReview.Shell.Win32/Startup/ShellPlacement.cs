using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>Seam của <see cref="ShellPlacement"/> trên HWND (production = user32; test dùng fake).</summary>
internal interface IWindowPlacementNative
{
    /// <summary>SetWindowPlacement với lệnh hiện = SW_HIDE (đặt vị trí Normal, KHÔNG hiện cửa sổ).</summary>
    bool SetHidden(nint hwnd, WindowPlacementData data);

    bool TryGet(nint hwnd, out WindowPlacementData data);
}

internal sealed unsafe class NativeWindowPlacement : IWindowPlacementNative
{
    public static readonly NativeWindowPlacement Instance = new();

    public bool SetHidden(nint hwnd, WindowPlacementData data)
    {
        var placement = new WindowPlacement
        {
            Length = (uint)sizeof(WindowPlacement),
            Flags = (uint)data.Flags,
            ShowCmd = WindowMessages.SwHide,
            MinPosition = new Point { X = data.MinX, Y = data.MinY },
            MaxPosition = new Point { X = data.MaxX, Y = data.MaxY },
            NormalPosition = new Rect { Left = data.NormalLeft, Top = data.NormalTop, Right = data.NormalRight, Bottom = data.NormalBottom },
        };
        return User32.SetWindowPlacement(hwnd, &placement);
    }

    public bool TryGet(nint hwnd, out WindowPlacementData data)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        if (!User32.GetWindowPlacement(hwnd, &placement))
        {
            data = null!;
            return false;
        }

        data = new WindowPlacementData((int)placement.Length, (int)placement.Flags, (int)placement.ShowCmd,
            placement.MinPosition.X, placement.MinPosition.Y, placement.MaxPosition.X, placement.MaxPosition.Y,
            placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom);
        return true;
    }
}

/// <summary>
/// Bản Win32 của <c>WindowPlacementService.RestoreBeforeShow</c>/<c>Save</c> (C-14, luật ở <see cref="WindowPlacementRules"/>):
/// đặt vị trí lúc đóng cho cửa sổ ĐÃ có HWND nhưng CHƯA hiện, rồi người gọi hiện nó thẳng vào đúng chỗ/trạng thái
/// (không hiện cửa sổ 1200x800 rồi mới nhảy). Placement không thấy được trên monitor nào (monitor đã rút): cửa sổ đóng khi
/// Maximized vẫn mở Maximized (trên monitor Windows chọn), Normal giữ cỡ mặc định.
/// </summary>
internal static class ShellPlacement
{
    /// <returns>Trạng thái cần hiện cửa sổ, hoặc null khi không áp gì (mặc định).</returns>
    public static WindowShowState? ApplyBeforeShow(nint hwnd, WindowPlacementData? data, Func<WindowPlacementData, bool> isVisible,
        IWindowPlacementNative native, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(isVisible);
        ArgumentNullException.ThrowIfNull(native);
        if (data is null || hwnd == 0)
        {
            return null;
        }

        try
        {
            var state = WindowPlacementRules.PlanStateBeforeShow(data.ShowCommand);
            if (!isVisible(data))
            {
                return state == WindowShowState.Maximized ? state : null;
            }

            return native.SetHidden(hwnd, data) ? state : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Error("Could not restore window placement before show", ex);
            return null;
        }
    }

    /// <summary>
    /// Ghi placement hiện tại. <paramref name="fullscreenRestoreState"/>: trạng thái trước F11 (null = không ở fullscreen).
    /// <paramref name="fullscreenNormalBounds"/>: rect Normal (pixel vật lý) lúc vào F11 - F11 đặt cửa sổ thường lên cả monitor mà
    /// không đổi trạng thái, nên GetWindowPlacement sẽ báo rect monitor đó; chỉ dùng khi đang F11 (như WindowPlacementService.Save).
    /// </summary>
    public static bool Save(nint hwnd, string placementPath, IWindowPlacementStore store, IWindowPlacementNative native,
        WindowShowState? fullscreenRestoreState = null, ILog? log = null,
        (int Left, int Top, int Right, int Bottom)? fullscreenNormalBounds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(native);
        try
        {
            if (hwnd == 0 || !native.TryGet(hwnd, out var current))
            {
                return false;
            }

            var resolved = current with
            {
                ShowCommand = WindowPlacementRules.ResolveShowCommand(current.ShowCommand, fullscreenRestoreState),
            };
            if (fullscreenRestoreState is not null && fullscreenNormalBounds is { } b)
            {
                resolved = resolved with { NormalLeft = b.Left, NormalTop = b.Top, NormalRight = b.Right, NormalBottom = b.Bottom };
            }

            store.Save(placementPath, resolved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Error("Could not save window placement", ex);
            return false;
        }
    }

    /// <summary>Lệnh <c>ShowWindow</c> tương ứng với trạng thái đã lên kế hoạch; <paramref name="activate"/> = false không lấy focus.</summary>
    public static int ShowCommandFor(WindowShowState? state, bool activate) => (state, activate) switch
    {
        (WindowShowState.Maximized, _) => WindowMessages.SwShowMaximized,
        (_, true) => WindowMessages.SwShowNormal,
        _ => WindowMessages.SwShowNoActivate,
    };
}
