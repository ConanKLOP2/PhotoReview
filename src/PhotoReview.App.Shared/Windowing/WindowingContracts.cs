namespace PhotoReview.App.Windowing;

// C-14 (NO-WPF-EXEC-PLAN mục 5): placement và F11 theo HWND. Chữ ký đóng băng (contracts.v1.txt); thực thi WP-08: JsonWindowPlacementStore,
// WindowPlacementRules (dưới đây), FullscreenController.

public enum WindowShowState
{
    Normal = 0,
    Minimized = 1,
    Maximized = 2,
}

/// <summary>
/// Lược đồ JSON y hệt window-placement.json hiện tại (IncludeFields, WriteIndented):
/// { "Length", "Flags", "ShowCommand", "MinPosition": {X,Y}, "MaxPosition": {X,Y}, "NormalPosition": {Left,Top,Right,Bottom} }.
/// Bản ghi này là dạng phẳng; WP-08 lo ánh xạ JSON (không đổi định dạng file, N-3).
/// </summary>
public sealed record WindowPlacementData(int Length, int Flags, int ShowCommand,
    int MinX, int MinY, int MaxX, int MaxY, int NormalLeft, int NormalTop, int NormalRight, int NormalBottom);

public interface IWindowPlacementStore
{
    /// <summary>Đọc trên pool; kết quả dùng một lần.</summary>
    Task<WindowPlacementData?> Prefetch(string placementPath);

    /// <summary>Null nếu thiếu/hỏng/không thấy được (IsVisible &lt; 80x80).</summary>
    WindowPlacementData? Read(string placementPath);

    /// <summary>WriteAtomically (tên tmp duy nhất).</summary>
    void Save(string placementPath, WindowPlacementData data);

    WindowPlacementData? TakePrefetched(string placementPath);
}

/// <summary>Luật thuần của placement, dời nguyên từ WindowPlacementService (WP-08).</summary>
public static class WindowPlacementRules
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    /// <summary>
    /// R2-F-26: chỉ "normal" và "maximized" là trạng thái khôi phục có nghĩa. Minimized, hidden (SW_HIDE) hay giá trị bất kỳ
    /// từ file hỏng sẽ mở cửa sổ ẩn hoặc thu nhỏ.
    /// </summary>
    public static int NormalizeShowCommand(int showCommand) => showCommand == ShowMaximized ? ShowMaximized : ShowNormal;

    /// <summary>R7-10: đóng khi đang F11 đọc lại là Maximized; lưu trạng thái cửa sổ có TRƯỚC F11 (null: dùng lệnh đang có).</summary>
    public static int ResolveShowCommand(int showCommand, WindowShowState? fullscreenRestoreState) => fullscreenRestoreState switch
    {
        WindowShowState.Maximized => ShowMaximized,
        WindowShowState.Normal or WindowShowState.Minimized => ShowNormal,
        _ => NormalizeShowCommand(showCommand),
    };

    /// <summary>Trạng thái cửa sổ mở lại theo lệnh đã lưu (cùng luật với <see cref="NormalizeShowCommand"/>).</summary>
    public static WindowShowState PlanStateBeforeShow(int savedShowCommand) =>
        NormalizeShowCommand(savedShowCommand) == ShowMaximized ? WindowShowState.Maximized : WindowShowState.Normal;
}

/// <summary>FullscreenWindowPlacer theo HWND (WP-08); thực thi: <see cref="FullscreenController"/>.</summary>
public interface IFullscreenController
{
    WindowShowState StateBefore { get; }

    (int Left, int Top, int Right, int Bottom)? NormalBounds { get; }

    void Enter(nint hwnd, WindowShowState current);

#pragma warning disable CA1716 // Tên Exit là chữ ký hợp đồng C-14 đã đóng băng (cặp với Enter); chỉ C# triển khai interface này.
    void Exit(nint hwnd);
#pragma warning restore CA1716
}
