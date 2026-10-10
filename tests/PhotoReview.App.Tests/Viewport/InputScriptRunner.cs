using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.App.Tests.Viewport;

/// <summary>
/// WP-16 / G-INPUT: chạy một <see cref="GoldenInputScript"/> (C-17) qua <see cref="PointerInputController"/> và
/// <see cref="FitViewController"/> THẬT trên engine thuần (<see cref="EngineImageSurface"/>), đồng hồ và dấu thời gian cố định,
/// rồi trả checkpoint cùng dạng golden. Bản Win32 (WP-22) dùng cùng runner với <c>ViewportController</c> khi C-07 sang App.Shared.
/// <para>Từ vựng bước (phải khớp bộ ghi WP-10 khi nó merge - lead đối chiếu; bước lạ làm test đỏ, không bị bỏ qua):
/// <c>wheel</c>/<c>hwheel</c> (Delta, X, Y, Modifiers chứa Control = Ctrl, TimestampMs); <c>press</c> (nút trái, Delta = số click,
/// mặc định 1) / <c>move</c> / <c>release</c> (X, Y, TimestampMs); <c>key</c> (Key = Left/Right/Up/Down, Delta != 0 = auto-repeat);
/// <c>command</c> (Fit, FitWidth, FitWidth2, FitHeight, ZoomIn, ZoomOut, ActualSize, ClickZoom, ClickZoomLevel với Delta = %);
/// <c>frame</c> (TimestampMs = RenderingTime); <c>resize</c> (X = rộng, Y = cao, DIP). Checkpoint <c>AfterStep</c> = chỉ số bước
/// (0-based) mà sau đó trạng thái được đọc; -1 = ngay sau khi ảnh hiện với InitialViewMode.</para>
/// </summary>
internal static class InputScriptRunner
{
    public static List<GoldenCheckpoint> Run(GoldenInputScript script)
    {
        var settings = string.IsNullOrWhiteSpace(script.Setup.SettingsOverridesJson)
            ? new AppSettings()
            : JsonSerializer.Deserialize(script.Setup.SettingsOverridesJson, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        var viewer = new ViewerState { DpiScale = script.Setup.DpiScale, ZoomStep = settings.KeyboardZoomStepPercent / 100.0 };
        viewer.SetSourceSize(script.Setup.ImagePixelWidth, script.Setup.ImagePixelHeight, newImage: true);
        var version = new ViewportOperationVersion();
        var surface = new EngineImageSurface(viewer, script.Setup.ClientWidth, script.Setup.ClientHeight,
            (script.Setup.ImagePixelWidth, script.Setup.ImagePixelHeight));
        FitViewController? fit = null;
        var pointer = new PointerInputController(surface, viewer, () => settings, version,
            new PointerCommands(() => true, () => Task.CompletedTask, () => Task.CompletedTask, viewer.ZoomToActualSize, () => fit!.ApplyFitAsync()));
        fit = new FitViewController(surface, viewer, version, pointer.CancelPan);
        viewer.ZoomModeChanged += (_, _) => pointer.StopKinetic(); // MainWindow.WireViewModelEvents

        var checkpoints = new List<GoldenCheckpoint>();
        var wanted = script.Expected.Select(c => c.AfterStep).ToHashSet();
        Drive(surface, pointer.ApplyInitialViewAsync(settings.InitialViewMode, settings.ClickZoomPercent));
        if (wanted.Contains(-1)) checkpoints.Add(Checkpoint(-1, viewer, surface));
        for (var i = 0; i < script.Steps.Count; i++)
        {
            Apply(script.Steps[i], i, settings, surface, pointer, fit);
            surface.Pump();
            if (wanted.Contains(i)) checkpoints.Add(Checkpoint(i, viewer, surface));
        }
        return checkpoints;
    }

    private static void Apply(GoldenInputStep step, int index, AppSettings settings, EngineImageSurface surface, PointerInputController pointer,
        FitViewController fit)
    {
        var point = new PointD(step.X, step.Y);
        var ctrl = step.Modifiers?.Contains("Control", StringComparison.OrdinalIgnoreCase) == true;
        switch (step.Kind)
        {
            case "wheel":
            case "hwheel":
                Drive(surface, pointer.OnWheelAsync(new WheelInput(step.Delta, step.Kind == "hwheel", ctrl, step.TimestampMs), point));
                break;
            case "press":
                pointer.OnWindowPreviewMouseDown();
                pointer.OnImagePress(PointerButton.Left, step.Delta > 0 ? step.Delta : 1, point, step.TimestampMs);
                break;
            case "move":
                pointer.OnImageMove(true, point, step.TimestampMs);
                break;
            case "release":
                pointer.OnImageRelease(point, step.TimestampMs);
                break;
            case "key":
                var key = step.Key is { } name && Enum.TryParse<KeyId>(name, out var parsed) && parsed is KeyId.Left or KeyId.Right or KeyId.Up or KeyId.Down
                    ? parsed
                    : throw Unsupported(index, step);
                pointer.TryPanByArrow(key, isRepeat: step.Delta != 0);
                break;
            case "command":
                Drive(surface, step.Command switch
                {
                    "Fit" => fit.ApplyFitAsync(),
                    "FitWidth" => pointer.FitWidthAsync(settings.FitWidthAnchor),
                    "FitWidth2" => pointer.FitWidthAsync(settings.FitWidthAnchor2),
                    "FitHeight" => pointer.FitHeightAsync(),
                    "ZoomIn" => pointer.ZoomInAsync(),
                    "ZoomOut" => pointer.ZoomOutAsync(),
                    "ActualSize" => pointer.ZoomActualSizeAsync(),
                    "ClickZoom" => pointer.ToggleClickZoomAsync(),
                    "ClickZoomLevel" => pointer.SetClickZoomLevelAsync(step.Delta),
                    _ => throw Unsupported(index, step),
                });
                break;
            case "frame":
                surface.Frame(TimeSpan.FromMilliseconds(step.TimestampMs));
                break;
            case "resize":
                surface.Resize(step.X, step.Y);
                break;
            default:
                throw Unsupported(index, step);
        }
    }

    private static void Drive(EngineImageSurface surface, Task operation) => surface.Run(operation).GetAwaiter().GetResult();

    private static NotSupportedException Unsupported(int index, GoldenInputStep step) =>
        new($"G-INPUT bước {index} chưa được runner hỗ trợ: {step} - đồng bộ từ vựng với bộ ghi WP-10");

    private static GoldenCheckpoint Checkpoint(int afterStep, ViewerState viewer, EngineImageSurface surface) => new(
        afterStep, viewer.Zoom, viewer.IsFit, surface.HorizontalOffset, surface.VerticalOffset,
        surface.ExtentWidth, surface.ExtentHeight, surface.ViewportWidth, surface.ViewportHeight, viewer.DisplayZoomPercent);

    /// <summary>So một checkpoint: zoom 0,001; độ dài 0,5 DIP; IsFit và DisplayZoomPercent tuyệt đối (NO-WPF-EXEC-PLAN 7.3).</summary>
    public static IEnumerable<string> Diff(GoldenCheckpoint expected, GoldenCheckpoint actual)
    {
        if (!(Math.Abs(expected.Zoom - actual.Zoom) <= 0.001)) yield return $"Zoom {expected.Zoom} != {actual.Zoom}";
        if (expected.IsFit != actual.IsFit) yield return $"IsFit {expected.IsFit} != {actual.IsFit}";
        if (expected.DisplayZoomPercent != actual.DisplayZoomPercent) yield return $"DisplayZoomPercent {expected.DisplayZoomPercent} != {actual.DisplayZoomPercent}";
        foreach (var (field, e, a) in new[]
        {
            ("HorizontalOffset", expected.HorizontalOffset, actual.HorizontalOffset), ("VerticalOffset", expected.VerticalOffset, actual.VerticalOffset),
            ("ExtentWidth", expected.ExtentWidth, actual.ExtentWidth), ("ExtentHeight", expected.ExtentHeight, actual.ExtentHeight),
            ("ViewportWidth", expected.ViewportWidth, actual.ViewportWidth), ("ViewportHeight", expected.ViewportHeight, actual.ViewportHeight),
        })
        {
            if (!(Math.Abs(e - a) <= 0.5)) yield return $"{field} {e} != {a}";
        }
    }
}

/// <summary>Bản App.Tests của Shell.Tests GoldenFixture (xem đó; tạm tới khi WP-10 có GoldenFile).</summary>
internal static class InputGoldenFixture
{
    public const string InputScriptsFile = "input-scripts.v1.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
    };

    public static string? Find(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Fixtures", "golden", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IReadOnlyList<T> ReadCases<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("golden không có mảng ca nào");
        return array.EnumerateArray().Select(e => e.Deserialize<T>(Options) ?? throw new InvalidDataException("ca golden rỗng")).ToList();
    }
}

/// <summary>[Fact] tự Skip khi file golden chưa có (WP-10 chưa merge), tự chạy khi có.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class InputGoldenFactAttribute : FactAttribute
{
    public InputGoldenFactAttribute(string fileName)
    {
        FileName = fileName;
        if (InputGoldenFixture.Find(fileName) is null) Skip = $"Chờ WP-10: chưa có tests/Fixtures/golden/{fileName}";
    }

    public string FileName { get; }
}
