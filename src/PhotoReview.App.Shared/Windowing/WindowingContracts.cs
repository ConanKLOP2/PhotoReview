namespace PhotoReview.App.Windowing;

// C-14 (NO-WPF-EXEC-PLAN mục 5): placement và F11 theo HWND. Thực thi ở WP-08.

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

/// <summary>Dời nguyên từ WindowPlacementService (thuần) ở WP-08.</summary>
public static class WindowPlacementRules
{
    public static int NormalizeShowCommand(int showCommand) => throw new NotImplementedException();

    public static int ResolveShowCommand(int showCommand, WindowShowState? fullscreenRestoreState) => throw new NotImplementedException();

    public static WindowShowState PlanStateBeforeShow(int savedShowCommand) => throw new NotImplementedException();
}

/// <summary>FullscreenWindowPlacer theo HWND (WP-08).</summary>
public interface IFullscreenController
{
    WindowShowState StateBefore { get; }

    (int Left, int Top, int Right, int Bottom)? NormalBounds { get; }

    void Enter(nint hwnd, WindowShowState current);

#pragma warning disable CA1716 // Tên Exit là chữ ký hợp đồng C-14 đã đóng băng (cặp với Enter); chỉ C# triển khai interface này.
    void Exit(nint hwnd);
#pragma warning restore CA1716
}
