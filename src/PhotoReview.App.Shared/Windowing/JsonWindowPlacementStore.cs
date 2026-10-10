using System.Text.Json;

namespace PhotoReview.App.Windowing;

/// <summary>
/// Lưu/đọc placement cửa sổ trong window-placement.json (C-14), JSON source-gen đúng lược đồ cũ (N-3). Không đụng WPF:
/// cả app WPF (adapter <c>WindowPlacementService</c>) lẫn shell Win32 dùng chung. Mọi trạng thái prefetch nằm trong
/// instance (app giữ một instance dùng chung).
/// </summary>
/// <remarks>
/// <see cref="Read"/> theo hợp đồng trả null khi thiếu/hỏng/không thấy được. Placement KHÔNG thấy được vẫn có ích: cửa sổ
/// đóng khi Maximized mở lại Maximized dù toạ độ Normal nằm ngoài màn hình (monitor đã rút) - nên
/// <see cref="Prefetch"/>, <see cref="TakePrefetched"/> và <see cref="ReadRaw"/> trả dữ liệu CHƯA lọc theo
/// <see cref="IsVisible"/>; nơi dùng tự áp <see cref="WindowPlacementRules"/> và <see cref="IsVisible"/>.
/// </remarks>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    private readonly IMonitorLayout _layout;
    private readonly object _prefetchGate = new();
    private (string Path, PrefetchResult Outcome)? _prefetch;

    public JsonWindowPlacementStore() : this(Win32MonitorLayout.Instance)
    {
    }

    internal JsonWindowPlacementStore(IMonitorLayout layout) => _layout = layout;

    /// <summary>Kết quả của task prefetch ghi vào đây, để luồng UI đọc kết quả đã xong mà không chạm vào Task.</summary>
    private sealed class PrefetchResult
    {
        private WindowPlacementData? _value;
        private volatile bool _succeeded;

        public void Complete(WindowPlacementData? value)
        {
            _value = value;
            _succeeded = true; // ghi volatile công bố _value
        }

        public bool TryGet(out WindowPlacementData? value)
        {
            value = _succeeded ? _value : null;
            return _succeeded;
        }
    }

    /// <summary>Null nếu thiếu, hỏng hoặc không thấy được (phần giao với mọi work area &lt; 80x80).</summary>
    public WindowPlacementData? Read(string placementPath)
    {
        WindowPlacementData? raw;
        try { raw = ReadRaw(placementPath); }
        catch (Exception ex) when (IsReadFailure(ex)) { return null; }
        return raw is not null && IsVisible(raw) ? raw : null;
    }

    /// <summary>
    /// Placement đã lưu, CHƯA lọc "thấy được"; null khi file thiếu hoặc nội dung là <c>null</c>. File hỏng ném
    /// <see cref="JsonException"/> (và lỗi I/O ném tiếp) để nơi gọi ghi log.
    /// </summary>
    public static WindowPlacementData? ReadRaw(string placementPath) =>
        File.Exists(placementPath) ? Deserialize(File.ReadAllText(placementPath)) : null;

    /// <summary>
    /// Đọc file trên pool ngay bây giờ (lần đọc JSON đầu tốn ~25 ms trên luồng UI nếu để dành cho Show()). Kết quả chưa lọc;
    /// <see cref="TakePrefetched"/> lấy đúng một lần. Lỗi đọc: task trả null (nơi dùng đọc lại và ghi log).
    /// </summary>
    public Task<WindowPlacementData?> Prefetch(string placementPath)
    {
        var result = new PrefetchResult();
        var load = Task.Run(() =>
        {
            WindowPlacementData? value;
            try { value = ReadRaw(placementPath); }
            catch (Exception ex) when (IsReadFailure(ex)) { return null; }
            result.Complete(value);
            return value;
        });
        lock (_prefetchGate) _prefetch = (placementPath, result);
        return load;
    }

    /// <summary>Placement đã prefetch của <paramref name="placementPath"/> (một lần), hoặc null: đọc file.</summary>
    public WindowPlacementData? TakePrefetched(string placementPath)
    {
        PrefetchResult result;
        lock (_prefetchGate)
        {
            if (_prefetch is not { } p || !string.Equals(p.Path, placementPath, StringComparison.OrdinalIgnoreCase)) return null;
            _prefetch = null;
            result = p.Outcome;
        }
        // Một lần đọc file (có chặn): thường xong từ lâu trước khi cửa sổ có HWND. Chưa xong hoặc lỗi: đọc lại.
        return result.TryGet(out var value) ? value : null;
    }

    public void Save(string placementPath, WindowPlacementData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(placementPath)!);
        WriteAtomically(placementPath, Serialize(data));
    }

    /// <summary>Hình chữ nhật Normal của <paramref name="data"/> thấy được trên ít nhất một monitor hiện có.</summary>
    public bool IsVisible(WindowPlacementData data) =>
        WindowPlacementVisibility.IsVisible(data.NormalLeft, data.NormalTop, data.NormalRight, data.NormalBottom,
            _layout.WorkAreas().Select(w => System.Drawing.Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom)));

    /// <summary>
    /// Ghi qua file tạm tên duy nhất để nhiều cửa sổ cùng lưu một file (chế độ per-folder) không đụng tên "*.tmp" chung
    /// (IOException / cửa sổ này ghi đè file nửa chừng của cửa sổ kia).
    /// </summary>
    internal static void WriteAtomically(string path, string content, Action<string, string>? writeText = null)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // RV-A15: việc ghi cũng nằm trong try, nên file tạm dở dang do ghi lỗi (đầy đĩa) bị xoá.
        try
        {
            (writeText ?? File.WriteAllText)(temp, content);
            File.Move(temp, path, true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort; ngoại lệ gốc được ném lại */ }
            throw;
        }
    }

    internal static string Serialize(WindowPlacementData data) =>
        JsonSerializer.Serialize(ToJson(data), WindowPlacementJsonContext.Default.WindowPlacementJson);

    internal static WindowPlacementData? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, WindowPlacementJsonContext.Default.WindowPlacementJson) is { } dto ? FromJson(dto) : null;

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;

    private static WindowPlacementJson ToJson(WindowPlacementData d) => new()
    {
        Length = d.Length,
        Flags = d.Flags,
        ShowCommand = d.ShowCommand,
        MinPosition = new PlacementPointJson { X = d.MinX, Y = d.MinY },
        MaxPosition = new PlacementPointJson { X = d.MaxX, Y = d.MaxY },
        NormalPosition = new PlacementRectJson { Left = d.NormalLeft, Top = d.NormalTop, Right = d.NormalRight, Bottom = d.NormalBottom },
    };

    private static WindowPlacementData FromJson(WindowPlacementJson j) => new(
        j.Length, j.Flags, j.ShowCommand,
        j.MinPosition.X, j.MinPosition.Y, j.MaxPosition.X, j.MaxPosition.Y,
        j.NormalPosition.Left, j.NormalPosition.Top, j.NormalPosition.Right, j.NormalPosition.Bottom);
}
