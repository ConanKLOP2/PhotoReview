using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Shell.Integration.Tests.Input;

/// <summary>
/// WP-22 / G-INPUT: chạy một <see cref="GoldenInputScript"/> (C-17) trên <see cref="InputHarness"/> bằng THÔNG ĐIỆP Win32 THẬT gửi
/// vào HWND (WM_MOUSEWHEEL, WM_LBUTTONDOWN, WM_KEYDOWN...), qua đúng ba handler của shell, controller con trỏ/Fit/phím tắt THẬT
/// và ViewportController THẬT; rồi trả checkpoint cùng dạng golden. Khác bản WP-16 (cùng golden nhưng gọi thẳng controller).
/// Từ vựng bước: xem <c>InputScriptRecorder</c> của WP-10 (đã đối chiếu ở WP-16).
/// </summary>
internal static class InputGoldenReplay
{
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<List<GoldenCheckpoint>> RunAsync(UiThread ui, GoldenInputScript script)
    {
        var setup = script.Setup;
        using var harness = new InputHarness(ui, setup.ClientWidth, setup.ClientHeight, setup.ImagePixelWidth, setup.ImagePixelHeight,
            setup.DpiScale);
        var settings = harness.Settings;

        // Bộ ghi WP-10: ảnh đầu hiện với cài đặt MẶC ĐỊNH, Fit, RỒI MỚI áp SettingsOverridesJson.
        await harness.Input.ApplyInitialViewAsync();
        await harness.SettleAsync();
        await harness.Input.ApplyFitAsync();
        await harness.SettleAsync();
        ApplySettings(setup.SettingsOverridesJson, settings, harness.Viewer);
        harness.Input.RebuildShortcuts(settings);

        var wanted = script.Expected.Select(c => c.AfterStep).ToHashSet();
        var checkpoints = new List<GoldenCheckpoint>();
        if (wanted.Contains(-1))
        {
            checkpoints.Add(Checkpoint(-1, harness));
        }

        var leftDown = false;
        for (var i = 0; i < script.Steps.Count; i++)
        {
            leftDown = await ApplyAsync(script.Steps[i], i, harness, leftDown);
            await harness.SettleAsync();
            if (wanted.Contains(i))
            {
                checkpoints.Add(Checkpoint(i, harness));
            }
        }

        return checkpoints;
    }

    private static bool HasModifier(string? modifiers, string name) =>
        modifiers is not null && modifiers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);

    private static async Task<bool> ApplyAsync(GoldenInputStep step, int index, InputHarness h, bool leftDown)
    {
        var x = (int)step.X;
        var y = (int)step.Y;
        if (step.Kind is "wheel" or "hwheel" or "press" or "move" or "release")
        {
            h.ScriptedMouse = new PointD(step.X, step.Y);
            h.MessageTime = step.TimestampMs;
        }

        var ctrl = HasModifier(step.Modifiers, "Control");
        switch (step.Kind)
        {
            case "wheel":
            case "hwheel":
                h.WheelDevice = step.Key == "Touchpad" ? WheelDeviceHint.Touchpad : WheelDeviceHint.Unknown;
                h.SendWheel(step.Kind == "wheel" ? WindowMessages.WmMouseWheel : WindowMessages.WmMouseHWheel, step.Delta, x, y, ctrl);
                break;
            case "press":
                // Số lần nhấn do golden quy định (Delta): đặt chuỗi nhấn như thể đã có Delta-1 lần ngay trước đó.
                h.Input.Clicks.Reset();
                var button = step.Key == "Middle" ? PointerButton.Middle : PointerButton.Left;
                if (step.Delta > 1)
                {
                    h.Input.Clicks.Seed(button, x, y, step.TimestampMs, step.Delta - 1);
                }

                h.SendMouse(button == PointerButton.Middle ? WindowMessages.WmMButtonDown : WindowMessages.WmLButtonDown, x, y);
                return button == PointerButton.Left || leftDown;
            case "move":
                h.SendMouse(WindowMessages.WmMouseMove, x, y, leftDown ? InputNative.MkLButton : 0);
                break;
            case "release":
                h.SendMouse(WindowMessages.WmLButtonUp, x, y);
                return false;
            case "key":
                SendKey(step, index, h);
                break;
            case "command":
                await CommandAsync(step, index, h);
                break;
            case "frame":
                h.Timestamp = (long)(step.TimestampMs * (Stopwatch.Frequency / 1000.0));
                h.Clock.Tick(step.TimestampMs);
                break;
            case "resize":
                h.Window.Resize(step.X, step.Y);
                break;
            default:
                throw Unsupported(index, step);
        }

        return leftDown;
    }

    private static void SendKey(GoldenInputStep step, int index, InputHarness h)
    {
        if (step.Key is not { } name || !Enum.TryParse<KeyId>(name, out var key))
        {
            throw Unsupported(index, step);
        }

        var modifiers = KeyModifiers.None;
        if (HasModifier(step.Modifiers, "Control"))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (HasModifier(step.Modifiers, "Shift"))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (HasModifier(step.Modifiers, "Alt"))
        {
            modifiers |= KeyModifiers.Alt;
        }

        h.Modifiers = modifiers;
        h.MessageTime = step.TimestampMs;
        h.SendKeyDown(KeyIdMapping.ToVirtualKey(key), repeat: HasModifier(step.Modifiers, "Repeat"));
    }

    /// <summary>
    /// Lệnh của bộ ghi: tên <c>ReviewCommandType</c> chạy qua <see cref="ShellInput.ExecuteAsync"/> (đường menu/nút), cộng các
    /// lệnh dựng cảnh (SetDpi, LoadImage, SwapSourceSize, SetClickZoomLevel, ZoomToLevelMenu).
    /// </summary>
    private static async Task CommandAsync(GoldenInputStep step, int index, InputHarness h)
    {
        var input = h.Input;
        switch (step.Command)
        {
            case "ToggleFit" or "Fit":
                await input.ExecuteAsync(ReviewCommand.ToggleFit);
                break;
            case "FitWidth":
                await input.ExecuteAsync(ReviewCommand.FitWidth);
                break;
            case "FitWidth2":
                await input.ExecuteAsync(ReviewCommand.FitWidth2);
                break;
            case "FitHeight":
                await input.ExecuteAsync(ReviewCommand.FitHeight);
                break;
            case "ZoomIn":
                await input.ExecuteAsync(ReviewCommand.ZoomIn);
                break;
            case "ZoomOut":
                await input.ExecuteAsync(ReviewCommand.ZoomOut);
                break;
            case "ZoomActualSize" or "ActualSize":
                await input.ExecuteAsync(ReviewCommand.ZoomActualSize);
                break;
            case "ClickZoom":
                await input.ExecuteAsync(ReviewCommand.ClickZoom);
                break;
            case "SetClickZoomLevel":
                await input.Pointer.SetClickZoomLevelAsync((int)step.X);
                break;
            case "ClickZoomLevel":
                await input.Pointer.SetClickZoomLevelAsync(step.Delta);
                break;
            case "ZoomToLevelMenu":
                await input.Pointer.SetClickZoomLevelAsync(h.Settings.ClickZoomPercent);
                break;
            case "Next" or "Previous":
                break; // bộ ghi chỉ đếm điều hướng; viewport không đổi
            case "SetDpi":
                h.Viewer.DpiScale = step.X;
                h.Viewport.UpdateFitSize(); // MainWindow.UpdateFitSize: DpiScale vào hệ số Fit
                break;
            case "LoadImage":
                input.NoteCurrentIndexChanged(0);
                h.SetBitmap((int)step.X, (int)step.Y);
                h.Viewer.SetSourceSize((int)step.X, (int)step.Y, newImage: true);
                h.Viewport.UpdateLayout();
                if (h.Viewer.IsFit)
                {
                    h.Viewport.UpdateFitSize();
                }

                h.Viewport.UpdateLayout();
                await input.ApplyInitialViewAsync();
                break;
            case "SwapSourceSize":
                h.SetBitmap((int)step.X, (int)step.Y);
                h.Viewer.SwapSourceSize((int)step.X, (int)step.Y);
                h.Viewport.UpdateLayout();
                break;
            default:
                throw Unsupported(index, step);
        }
    }

    /// <summary>GoldenWpfView.ApplySettings: mặc định cho mọi script, rồi từng khoá override (không phân biệt hoa thường).</summary>
    private static void ApplySettings(string? overridesJson, AppSettings settings, ViewerState viewer)
    {
        var defaults = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0
                && (property.PropertyType.IsValueType || property.PropertyType == typeof(string) || property.PropertyType == typeof(List<string>)))
            {
                property.SetValue(settings, property.GetValue(defaults));
            }
        }

        if (!string.IsNullOrWhiteSpace(overridesJson))
        {
            using var document = JsonDocument.Parse(overridesJson);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var property = typeof(AppSettings).GetProperty(entry.Name,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase)
                    ?? throw new InvalidOperationException($"Unknown AppSettings property in golden setup: {entry.Name}");
                property.SetValue(settings, JsonSerializer.Deserialize(entry.Value.GetRawText(), property.PropertyType, SettingsOptions));
            }
        }

        viewer.ZoomStep = settings.KeyboardZoomStepPercent / 100.0;
        viewer.ScalingQuality = settings.ScalingQuality;
    }

    private static NotSupportedException Unsupported(int index, GoldenInputStep step) =>
        new($"G-INPUT bước {index} chưa được replay hỗ trợ: {step}");

    private static GoldenCheckpoint Checkpoint(int afterStep, InputHarness h) => new(
        afterStep, h.Viewer.Zoom, h.Viewer.IsFit, h.Viewport.HorizontalOffset, h.Viewport.VerticalOffset,
        h.Viewport.ExtentWidth, h.Viewport.ExtentHeight, h.Viewport.ViewportWidth, h.Viewport.ViewportHeight, h.Viewer.DisplayZoomPercent);

    /// <summary>So một checkpoint: zoom 0,001; độ dài 0,5 DIP; IsFit và DisplayZoomPercent tuyệt đối (NO-WPF-EXEC-PLAN 7.3).</summary>
    public static IEnumerable<string> Diff(GoldenCheckpoint expected, GoldenCheckpoint actual)
    {
        if (!(Math.Abs(expected.Zoom - actual.Zoom) <= 0.001))
        {
            yield return $"Zoom {expected.Zoom} != {actual.Zoom}";
        }

        if (expected.IsFit != actual.IsFit)
        {
            yield return $"IsFit {expected.IsFit} != {actual.IsFit}";
        }

        if (expected.DisplayZoomPercent != actual.DisplayZoomPercent)
        {
            yield return $"DisplayZoomPercent {expected.DisplayZoomPercent} != {actual.DisplayZoomPercent}";
        }

        foreach (var (field, e, a) in new[]
        {
            ("HorizontalOffset", expected.HorizontalOffset, actual.HorizontalOffset),
            ("VerticalOffset", expected.VerticalOffset, actual.VerticalOffset),
            ("ExtentWidth", expected.ExtentWidth, actual.ExtentWidth),
            ("ExtentHeight", expected.ExtentHeight, actual.ExtentHeight),
            ("ViewportWidth", expected.ViewportWidth, actual.ViewportWidth),
            ("ViewportHeight", expected.ViewportHeight, actual.ViewportHeight),
        })
        {
            if (!(Math.Abs(e - a) <= 0.5))
            {
                yield return $"{field} {e} != {a}";
            }
        }
    }
}
