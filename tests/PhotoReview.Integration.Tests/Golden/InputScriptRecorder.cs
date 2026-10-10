using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.App;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10 G-INPUT (<c>input-scripts.v1.json</c>): input scripts (wheel, drag-pan, kinetic glide, keys, middle click, DPI change, ...)
/// replayed on the REAL <c>PointerInputController</c>/<c>FitViewController</c>/<c>ViewerState</c> over the WPF ScrollViewer/Image, with a
/// checkpoint (zoom, Fit, offsets, extent, viewport, HUD percent) after every step.
/// <para>
/// Step vocabulary (<see cref="GoldenInputStep"/>): <c>wheel</c>/<c>hwheel</c> (Delta; Modifiers "Control"; Key "Touchpad" = OS device hint;
/// X,Y = pointer in ImageScroll DIP), <c>press</c> (Key "Left"|"Middle", Delta = click count, X,Y), <c>move</c>, <c>release</c>,
/// <c>key</c> (Key = <c>System.Windows.Input.Key</c> name; Modifiers = comma list of Control/Shift/Alt, plus "Repeat" for an auto-repeat),
/// <c>frame</c> (RenderingTime = TimestampMs; Delta = ms since the previous frame, informational), <c>resize</c> (X,Y = new client size),
/// <c>command</c> (Command = ReviewCommandType name, or SetClickZoomLevel (X = percent), ZoomToLevelMenu, SetDpi (X = scale),
/// LoadImage (X,Y = pixel size; the next image of the folder), SwapSourceSize (X,Y = pixel size; same image, new bitmap)).
/// Ctrl/Shift state is carried by the step (WPF reads the live keyboard, which a headless run cannot set).
/// </para>
/// </summary>
internal static class InputScriptRecorder
{
    internal const string Notes =
        "Recorded from the WPF MainWindow geometry (real ScrollViewer/Image/ViewerState) with the real PointerInputController and " +
        "FitViewController wired as MainWindow wires them; the render-frame callback and the mouse are scripted (fixed RenderingTime, " +
        "KineticGlideSmoothing=Off behaviour: no vblank timing). Setup: first image shown at Fit (default settings), then " +
        "SettingsOverridesJson applied. Checkpoint after every step (frames included). DragThreshold 4 DIP. Step vocabulary: see " +
        "InputScriptRecorder summary / docs/refactoring/decisions/NOWPF-WP10-GOLDEN-RECORDER.md. Tolerance for replay: 0.5 DIP, 0.001 zoom.";

    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    internal static async Task<IReadOnlyList<GoldenCheckpoint>> RunAsync(GoldenWpfView view, GoldenInputScript script)
    {
        var setup = script.Setup;
        view.ApplySettings("{}");
        view.SetClient(setup.ClientWidth, setup.ClientHeight);
        view.SetDpi(setup.DpiScale);
        await view.ShowImageAsync(setup.ImagePixelWidth, setup.ImagePixelHeight);
        await view.ApplyFitAsync();
        view.ApplySettings(setup.SettingsOverridesJson);

        var checkpoints = new List<GoldenCheckpoint>();
        for (var index = 0; index < script.Steps.Count; index++)
        {
            await ExecuteAsync(view, script.Steps[index]);
            checkpoints.Add(Checkpoint(view, index));
        }
        return checkpoints;
    }

    private static GoldenCheckpoint Checkpoint(GoldenWpfView view, int afterStep) => new(
        AfterStep: afterStep,
        Zoom: view.Viewer.Zoom,
        IsFit: view.Viewer.IsFit,
        HorizontalOffset: view.Scroll.HorizontalOffset,
        VerticalOffset: view.Scroll.VerticalOffset,
        ExtentWidth: view.Scroll.ExtentWidth,
        ExtentHeight: view.Scroll.ExtentHeight,
        ViewportWidth: view.Scroll.ViewportWidth,
        ViewportHeight: view.Scroll.ViewportHeight,
        DisplayZoomPercent: view.Viewer.DisplayZoomPercent);

    private static async Task ExecuteAsync(GoldenWpfView view, GoldenInputStep step)
    {
        var point = new Point(step.X, step.Y);
        switch (step.Kind)
        {
            case "wheel":
            case "hwheel":
                view.Mouse.Position = point;
                await view.Pointer.OnWheelAsync(
                    new WheelInput(step.Delta, step.Kind == "hwheel", HasModifier(step.Modifiers, "Control"), step.TimestampMs,
                        step.Key == "Touchpad" ? WheelDeviceHint.Touchpad : WheelDeviceHint.Unknown),
                    point);
                break;
            case "press":
                view.Mouse.Position = point;
                view.Pointer.OnWindowPreviewMouseDown(); // Window.PreviewMouseDown tunnels first, for every button
                if (step.Key == "Middle")
                {
                    // ImageScroll_PreviewMouseDown: acts on the first press of a multi-click only.
                    if (step.Delta <= 1 && MiddleClickResolver.Resolve(view.Window.Settings.MiddleClickAction) is { } middle)
                        await ExecuteCommandAsync(view, middle);
                }
                else
                {
                    view.Pointer.OnImagePress(MouseButton.Left, Math.Max(1, step.Delta), point, step.TimestampMs);
                    view.LeftButtonDown = true;
                }
                break;
            case "move":
                view.Mouse.Position = point;
                view.Pointer.OnImageMove(view.LeftButtonDown, point, step.TimestampMs);
                break;
            case "release":
                view.Mouse.Position = point;
                view.Pointer.OnImageRelease(point, step.TimestampMs);
                view.LeftButtonDown = false;
                break;
            case "frame":
                view.Surface.Ticks = (long)(step.TimestampMs * TicksPerMs);
                view.Surface.Frame(step.TimestampMs);
                break;
            case "resize":
                view.SetClient(step.X, step.Y);
                break;
            case "key":
                await KeyAsync(view, step);
                break;
            case "command":
                await CommandAsync(view, step);
                break;
            default:
                throw new InvalidOperationException($"Unknown golden input step kind '{step.Kind}'.");
        }
        await view.SettleAsync();
    }

    /// <summary>MainWindow.Window_KeyDown, the part that reaches the viewport (arrow pan, then the shortcut router).</summary>
    private static async Task KeyAsync(GoldenWpfView view, GoldenInputStep step)
    {
        var key = Enum.Parse<Key>(step.Key ?? throw new InvalidOperationException("key step without Key"));
        var modifiers = ModifiersOf(step.Modifiers);
        var repeat = HasModifier(step.Modifiers, "Repeat");
        var arrowConsumed = modifiers == ModifierKeys.None && view.Pointer.TryPanByArrow(key, repeat);
        if (arrowConsumed) return;
        view.Pointer.StopKinetic();
        var command = view.Router.TryResolve(key, Key.None, modifiers, isFullscreen: false, hasImage: true,
            hasComparePair: false, isCompareVisible: false, hasCapturePair: false);
        if (command is null) return;
        if (repeat && command.Value.Type.IgnoresAutoRepeat()) return;
        await ExecuteCommandAsync(view, command.Value);
    }

    private static async Task CommandAsync(GoldenWpfView view, GoldenInputStep step)
    {
        var name = step.Command ?? throw new InvalidOperationException("command step without Command");
        switch (name)
        {
            case "SetClickZoomLevel":
                await view.Pointer.SetClickZoomLevelAsync((int)step.X);
                return;
            case "ZoomToLevelMenu":
                await view.Pointer.SetClickZoomLevelAsync(view.Window.Settings.ClickZoomPercent);
                return;
            case "SetDpi":
                view.SetDpi(step.X);
                return;
            case "LoadImage":
                await view.ShowImageAsync((int)step.X, (int)step.Y);
                return;
            case "SwapSourceSize":
                view.Image.Source = GoldenWpfView.CreateBitmap(step.X, step.Y);
                view.Viewer.SwapSourceSize((int)step.X, (int)step.Y);
                view.Relayout();
                return;
        }
        await ExecuteCommandAsync(view, new ReviewCommand(Enum.Parse<ReviewCommandType>(name)));
    }

    /// <summary>MainWindow.ExecuteReviewCommandAsync restricted to what changes the viewport; other commands do nothing here.</summary>
    private static async Task ExecuteCommandAsync(GoldenWpfView view, ReviewCommand command)
    {
        switch (command.Type)
        {
            case ReviewCommandType.ZoomActualSize: await view.Pointer.ZoomActualSizeAsync(); break;
            case ReviewCommandType.ToggleFit: await view.Fit.ApplyFitAsync(); break;
            case ReviewCommandType.ZoomIn: await view.Pointer.ZoomInAsync(); break;
            case ReviewCommandType.ZoomOut: await view.Pointer.ZoomOutAsync(); break;
            case ReviewCommandType.ClickZoom: await view.Pointer.ToggleClickZoomAsync(); break;
            case ReviewCommandType.FitWidth or ReviewCommandType.FitWidth2:
                await view.Pointer.FitWidthAsync(MainWindowHelpers.FitWidthAnchorFor(view.Window.Settings, command.Type));
                break;
            case ReviewCommandType.FitHeight: await view.Pointer.FitHeightAsync(); break;
            case ReviewCommandType.Next: await view.NextImageAsync(); break;
            case ReviewCommandType.Previous: await view.PreviousImageAsync(); break;
        }
    }

    private static bool HasModifier(string? modifiers, string name) =>
        modifiers is not null && modifiers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);

    private static ModifierKeys ModifiersOf(string? modifiers)
    {
        var result = ModifierKeys.None;
        if (HasModifier(modifiers, "Control")) result |= ModifierKeys.Control;
        if (HasModifier(modifiers, "Shift")) result |= ModifierKeys.Shift;
        if (HasModifier(modifiers, "Alt")) result |= ModifierKeys.Alt;
        return result;
    }

    /// <summary>Records every catalog script on a fresh window each (no controller, glide or gesture state leaks between scripts).</summary>
    public static async Task<IReadOnlyList<GoldenInputScript>> RecordAsync(IReadOnlyList<GoldenInputScript> catalog)
    {
        var recorded = new List<GoldenInputScript>();
        await GoldenWpfHost.RunEachAsync(catalog, async (view, script) =>
            recorded.Add(script with { Expected = await RunAsync(view, script) }));
        return recorded;
    }

    internal static GoldenDocument<GoldenInputScript> Document(IReadOnlyList<GoldenInputScript> scripts) =>
        new("input-scripts", 1, "PhotoReview.Integration.Tests.Golden.InputScriptRecorder (WPF MainWindow)", Notes, scripts);
}
