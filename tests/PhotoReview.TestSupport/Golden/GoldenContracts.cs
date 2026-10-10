namespace PhotoReview.TestSupport.Golden;

// C-17 (NO-WPF-EXEC-PLAN mục 5): lược đồ golden JSON (tests/Fixtures/golden/*.v1.json). WP-10 ghi từ bản WPF thật và thêm
// JsonSerializerContext; các gói shell chỉ đọc. TestSupport (net10.0) không tham chiếu App.Shared nên *Dto là bản phẳng của
// ViewportInput/ViewportLayout (C-08); enum ghi bằng tên (ScrollBarPolicy, ViewerStretchMode).

public sealed record GoldenViewportCase(string Name, ViewportInputDto Input, ViewportLayoutDto Expected);

public sealed record GoldenInputScript(string Name, GoldenSetup Setup, IReadOnlyList<GoldenInputStep> Steps, IReadOnlyList<GoldenCheckpoint> Expected);

/// <param name="ClientWidth">DIP.</param>
/// <param name="ClientHeight">DIP.</param>
/// <param name="DpiScale">Hệ số DPI (1.0 = 96).</param>
/// <param name="ImagePixelWidth">Kích thước ảnh nguồn (px).</param>
/// <param name="ImagePixelHeight">Kích thước ảnh nguồn (px).</param>
/// <param name="SettingsOverridesJson">Phần config.json ghi đè (InitialViewMode, ClickZoomPercent, KineticPanEnabled, ...).</param>
public sealed record GoldenSetup(double ClientWidth, double ClientHeight, double DpiScale, int ImagePixelWidth, int ImagePixelHeight,
    string SettingsOverridesJson);

/// <param name="Kind">"wheel" | "hwheel" | "press" | "move" | "release" | "key" | "command" | "frame"(ms) | "resize".</param>
/// <param name="X">DIP.</param>
/// <param name="Y">DIP.</param>
/// <param name="Delta">Wheel delta hoặc số ms cho "frame".</param>
/// <param name="Key">Tên KeyId/Key, hoặc null.</param>
/// <param name="Modifiers">Tên KeyModifiers, hoặc null.</param>
/// <param name="TimestampMs">Dấu thời gian cố định.</param>
/// <param name="Command">Tên lệnh VM cho "command", hoặc null.</param>
public sealed record GoldenInputStep(string Kind, double X, double Y, int Delta, string? Key, string? Modifiers, int TimestampMs, string? Command);

public sealed record GoldenCheckpoint(int AfterStep, double Zoom, bool IsFit, double HorizontalOffset, double VerticalOffset,
    double ExtentWidth, double ExtentHeight, double ViewportWidth, double ViewportHeight, int DisplayZoomPercent);

public sealed record GoldenKeyName(string Name, int KeyValue, int VirtualKey);

public sealed record GoldenMenuCase(string Name, string SettingsOverridesJson, IReadOnlyList<string> VisibleIdsInOrder);

/// <summary>Bản phẳng của ViewportInput (C-08). ScrollBars/Stretch là tên enum.</summary>
public sealed record ViewportInputDto(
    double ClientWidth, double ClientHeight,
    double ScrollBarThickness,
    string ScrollBars,
    string Stretch,
    double ImageWidth, double ImageHeight,
    double MaxImageWidth, double MaxImageHeight,
    double BitmapWidth, double BitmapHeight);

/// <summary>Bản phẳng của ViewportLayout (C-08); ImageRect tách thành ImageX/ImageY/ImageWidth/ImageHeight.</summary>
public sealed record ViewportLayoutDto(
    double ViewportWidth, double ViewportHeight,
    double ExtentWidth, double ExtentHeight,
    bool HorizontalBarVisible, bool VerticalBarVisible,
    double ImageX, double ImageY, double ImageWidth, double ImageHeight,
    double MaxHorizontalOffset, double MaxVerticalOffset);
