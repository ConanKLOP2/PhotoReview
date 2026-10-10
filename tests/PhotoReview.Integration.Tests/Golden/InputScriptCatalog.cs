using System.Globalization;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10: the G-INPUT script catalog (NO-WPF-EXEC-PLAN section 7.3: wheel at 5 points, Ctrl+wheel, FitWidth anchors, click-zoom on/off,
/// double-click Fit, drag pan + kinetic with fixed timestamps, arrow keys at zoom/edge, touchpad pan/swipe, each middle-click action,
/// KeepZoom over 3 images, RAW size swap, DPI change mid-way, resize while FitWidth). Steps only; <c>Expected</c> is filled by the
/// recorder from the WPF window.
/// </summary>
internal static class InputScriptCatalog
{
    private const string Default = "{}";

    // Standard geometry: 1280x720 client at 100 % DPI. 6000x4000 landscape Fit = 1080x720 (x 100..1180); 4000x6000 portrait Fit = 480x720 (x 400..880).
    private static readonly (double X, double Y)[] FivePoints = [(150, 80), (1130, 80), (150, 640), (1130, 640), (640, 360)];

    public static IReadOnlyList<GoldenInputScript> Build()
    {
        var scripts = new List<GoldenInputScript>();
        AddWheel(scripts);
        AddFitWidthHeight(scripts);
        AddClickZoom(scripts);
        AddPan(scripts);
        AddKinetic(scripts);
        AddArrows(scripts);
        AddKeyboardZoom(scripts);
        AddTouchpad(scripts);
        AddMiddleClick(scripts);
        AddNavigationAndInitialView(scripts);
        AddEnvironmentChanges(scripts);
        AddMenuZoom(scripts);

        var duplicate = scripts.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate golden script name {duplicate.Key}.");
        return scripts;
    }

    private static void AddWheel(List<GoldenInputScript> scripts)
    {
        // Wheel zoom at 5 points: 3 notches in, 2 out, from Fit (step = KeyboardZoomStepPercent, anchored at the cursor).
        foreach (var (x, y) in FivePoints)
        {
            var script = Begin($"wheel-zoom-in-out@{x:0},{y:0}", 1280, 720, 1.0, 6000, 4000, Default);
            script.Wheel(120, x, y).Wheel(120, x, y).Wheel(120, x, y).Wheel(-120, x, y).Wheel(-120, x, y);
            scripts.Add(script.Done());
        }

        var small = Begin("wheel-zoom-in@center-small-800x600", 1280, 720, 1.0, 800, 600, Default);
        small.Wheel(120, 640, 360).Wheel(120, 640, 360).Wheel(-120, 640, 360);
        scripts.Add(small.Done());

        var portrait = Begin("wheel-zoom-in@center-portrait", 1280, 720, 1.0, 4000, 6000, Default);
        portrait.Wheel(120, 640, 360).Wheel(120, 700, 200).Wheel(120, 600, 600);
        scripts.Add(portrait.Done());

        var ctrl = Begin("ctrl-wheel-zoom-in@center", 1280, 720, 1.0, 6000, 4000, Default);
        ctrl.Wheel(120, 640, 360, ctrl: true).Wheel(120, 640, 360, ctrl: true).Wheel(-120, 640, 360, ctrl: true);
        scripts.Add(ctrl.Done());

        var navigate = Begin("wheel-navigate-mode-ctrl-wheel-zooms", 1280, 720, 1.0, 6000, 4000, "{\"mouseWheelAction\":\"Navigate\"}");
        navigate.Wheel(-120, 640, 360).Wheel(120, 640, 360).Wheel(120, 640, 360, ctrl: true).Wheel(120, 640, 360, ctrl: true);
        scripts.Add(navigate.Done());

        var deep = Begin("wheel-zoom-in-to-step-limit", 1280, 720, 1.0, 6000, 4000, "{\"keyboardZoomStepPercent\":50}");
        for (var i = 0; i < 6; i++) deep.Wheel(120, 640, 360);
        deep.Wheel(-120, 640, 360);
        scripts.Add(deep.Done());

        var tick = Begin("wheel-zoom-out-below-step-floor", 1280, 720, 1.0, 12000, 1000, Default);
        tick.Wheel(-120, 640, 360).Wheel(120, 640, 360).Wheel(-120, 640, 360).Wheel(-120, 640, 360);
        scripts.Add(tick.Done());
    }

    private static void AddFitWidthHeight(List<GoldenInputScript> scripts)
    {
        foreach (var (image, w, h) in new[] { ("landscape", 6000, 4000), ("portrait", 4000, 6000) })
        {
            foreach (var anchor in new[] { "Centre", "TopThird", "BottomThird" })
            {
                var one = Begin($"fitwidth-{anchor}-{image}", 1280, 720, 1.0, w, h, $"{{\"fitWidthAnchor\":\"{anchor}\"}}");
                one.Command("FitWidth");
                scripts.Add(one.Done());
            }

            var two = Begin($"fitwidth2-BottomThird-then-fitwidth-{image}", 1280, 720, 1.0, w, h,
                "{\"fitWidthAnchor\":\"TopThird\",\"fitWidthAnchor2\":\"BottomThird\"}");
            two.Command("FitWidth2").Command("FitWidth").Command("FitHeight").Command("ToggleFit");
            scripts.Add(two.Done());

            var height = Begin($"fitheight-then-fit-{image}", 1280, 720, 1.0, w, h, Default);
            height.Command("FitHeight").Command("ToggleFit");
            scripts.Add(height.Done());
        }

        var viaKeys = Begin("keys-W-H-F-fitwidth-fitheight-fit", 1280, 720, 1.0, 6000, 4000, Default);
        viaKeys.Key("W").Key("H").Key("F");
        scripts.Add(viaKeys.Done());

        var wide = Begin("fitwidth-panorama-12000x1000", 1280, 720, 1.0, 12000, 1000, Default);
        wide.Command("FitWidth").Command("FitHeight");
        scripts.Add(wide.Done());

        var tall = Begin("fitheight-tall-1000x12000", 1280, 720, 1.0, 1000, 12000, Default);
        tall.Command("FitHeight").Command("FitWidth");
        scripts.Add(tall.Done());

        var dpi = Begin("fitwidth-at-dpi-150", 1280, 720, 1.5, 6000, 4000, Default);
        dpi.Command("FitWidth").Command("ZoomActualSize");
        scripts.Add(dpi.Done());
    }

    private static void AddClickZoom(List<GoldenInputScript> scripts)
    {
        foreach (var percent in new[] { 100, 200, 50 })
        {
            var script = Begin($"click-zoom-{percent}-then-click-back-to-fit", 1280, 720, 1.0, 6000, 4000,
                $"{{\"clickToZoomEnabled\":true,\"clickZoomPercent\":{percent}}}");
            script.Click(300, 200).Click(300, 200);
            scripts.Add(script.Done());
        }

        var off = Begin("click-zoom-disabled-click-does-nothing", 1280, 720, 1.0, 6000, 4000, "{\"clickToZoomEnabled\":false}");
        off.Click(300, 200);
        scripts.Add(off.Done());

        var drag = Begin("click-zoom-press-move-below-threshold-is-a-click", 1280, 720, 1.0, 6000, 4000, "{\"clickToZoomEnabled\":true}");
        drag.Press(500, 300).Move(502, 301, 16).Release(502, 301, 32);
        scripts.Add(drag.Done());

        var dbl = Begin("double-click-fits-from-zoom", 1280, 720, 1.0, 6000, 4000, "{\"clickToZoomEnabled\":true}");
        dbl.Command("ZoomActualSize").Press(640, 360, clicks: 1).Release(640, 360, 20).Press(640, 360, 40, clicks: 2).Release(640, 360, 60);
        scripts.Add(dbl.Done());

        var dblFit = Begin("double-click-at-fit-stays-fit", 1280, 720, 1.0, 6000, 4000, Default);
        dblFit.Press(640, 360, clicks: 2).Release(640, 360, 20);
        scripts.Add(dblFit.Done());

        var anchored = Begin("click-zoom-at-corner-anchors-cursor", 1280, 720, 1.0, 6000, 4000, "{\"clickToZoomEnabled\":true,\"clickZoomPercent\":200}");
        anchored.Click(1100, 650);
        scripts.Add(anchored.Done());

        var key = Begin("click-zoom-key-D2-toggle-at-pointer-anchor", 1280, 720, 1.0, 6000, 4000,
            "{\"keyboardZoomAnchor\":\"Pointer\",\"clickZoomPercent\":150,\"clickZoomKeyTogglesFit\":true}");
        key.Move(900, 500).Key("D2").Key("D2");
        scripts.Add(key.Done());
    }

    private static void AddPan(List<GoldenInputScript> scripts)
    {
        var drag = Begin("pan-drag-200pct-no-kinetic", 1280, 720, 1.0, 6000, 4000, Default);
        drag.Wheel(120, 640, 360).Wheel(120, 640, 360).Wheel(120, 640, 360).Wheel(120, 640, 360);
        drag.Press(800, 400).Move(780, 390, 16).Move(700, 350, 32).Move(500, 300, 48).Release(500, 300, 64);
        scripts.Add(drag.Done());

        var edge = Begin("pan-drag-clamps-at-edges", 1280, 720, 1.0, 6000, 4000, Default);
        edge.Command("ZoomActualSize");
        edge.Press(600, 400).Move(1200, 700, 16).Move(1270, 715, 32).Release(1270, 715, 48);
        scripts.Add(edge.Done());

        var fit = Begin("pan-drag-at-fit-does-nothing", 1280, 720, 1.0, 6000, 4000, Default);
        fit.Press(600, 400).Move(500, 300, 16).Move(300, 200, 32).Release(300, 200, 48);
        scripts.Add(fit.Done());

        var small = Begin("pan-drag-image-smaller-than-viewport", 1280, 720, 1.0, 800, 600, Default);
        small.Command("ZoomActualSize").Press(640, 360).Move(600, 300, 16).Move(500, 250, 32).Release(500, 250, 48);
        scripts.Add(small.Done());

        var horizontal = Begin("pan-drag-panorama-horizontal", 1280, 720, 1.0, 12000, 1000, Default);
        horizontal.Command("FitHeight").Press(900, 300).Move(700, 300, 16).Move(300, 310, 32).Release(300, 310, 48);
        scripts.Add(horizontal.Done());
    }

    private static void AddKinetic(List<GoldenInputScript> scripts)
    {
        const string Kinetic = "{\"kineticPanEnabled\":true,\"kineticGlideSmoothing\":\"Off\"}";

        // Fast flick left-up: ~1.5 DIP/ms at release, 16 ms frames until the glide dies.
        var flick = Begin("kinetic-flick-left-up-16ms-frames", 1280, 720, 1.0, 6000, 4000, Kinetic);
        flick.Command("ZoomActualSize");
        flick.Press(1000, 600).Move(960, 580, 16).Move(900, 550, 32).Move(820, 510, 48).Release(740, 470, 64).Frames(start: 64 + 16, count: 90, intervalMs: 16);
        scripts.Add(flick.Done());

        // Slow drag then release: below the start velocity -> no glide.
        var slow = Begin("kinetic-slow-release-no-glide", 1280, 720, 1.0, 6000, 4000, Kinetic);
        slow.Command("ZoomActualSize");
        slow.Press(800, 400).Move(798, 399, 100).Move(796, 398, 200).Release(795, 398, 300).Frames(start: 316, count: 10, intervalMs: 16);
        scripts.Add(slow.Done());

        // Down-right flick at 144 Hz style frames (7 ms).
        var fast = Begin("kinetic-flick-down-right-7ms-frames", 1280, 720, 1.0, 6000, 4000, Kinetic);
        fast.Command("ZoomActualSize");
        fast.Press(300, 200).Move(340, 230, 12).Move(400, 280, 24).Move(480, 340, 36).Release(560, 400, 48).Frames(start: 55, count: 160, intervalMs: 7);
        scripts.Add(fast.Done());

        // Irregular frame times (stutters) and a second press that stops the glide.
        var stutter = Begin("kinetic-irregular-frames-then-press-stops-glide", 1280, 720, 1.0, 6000, 4000, Kinetic);
        stutter.Command("ZoomActualSize");
        stutter.Press(1000, 500).Move(900, 480, 16).Move(780, 450, 32).Release(660, 420, 48);
        stutter.FrameAt(60).FrameAt(76).FrameAt(120).FrameAt(137).FrameAt(200).FrameAt(216).FrameAt(300);
        stutter.Press(600, 400, 310).Release(600, 400, 330);
        stutter.FrameAt(340).FrameAt(356);
        scripts.Add(stutter.Done());

        // Glide into an edge stops there (clamped).
        var edge = Begin("kinetic-glide-hits-edge", 1280, 720, 1.0, 6000, 4000, Kinetic);
        edge.Command("ZoomActualSize");
        edge.Press(200, 200).Move(500, 300, 16).Move(900, 400, 32).Release(1200, 500, 48).Frames(start: 64, count: 60, intervalMs: 16);
        scripts.Add(edge.Done());

        // Wheel zoom during a glide stops it (ZoomModeChanged + OnWheel StopKinetic).
        var wheel = Begin("kinetic-wheel-stops-glide", 1280, 720, 1.0, 6000, 4000, Kinetic);
        wheel.Command("ZoomActualSize");
        wheel.Press(1000, 600).Move(900, 540, 16).Move(800, 480, 32).Release(700, 420, 48).Frames(start: 64, count: 6, intervalMs: 16);
        wheel.Wheel(120, 640, 360).Frames(start: 300, count: 4, intervalMs: 16);
        scripts.Add(wheel.Done());
    }

    private static void AddArrows(List<GoldenInputScript> scripts)
    {
        var step = Begin("arrow-keys-pan-at-100pct-step10", 1280, 720, 1.0, 6000, 4000, Default);
        step.Command("ZoomActualSize").Key("Right").Key("Right").Key("Down").Key("Left").Key("Up").Key("Up");
        scripts.Add(step.Done());

        var edge = Begin("arrow-keys-at-edge-consumed-no-navigate", 1280, 720, 1.0, 6000, 4000, Default);
        edge.Command("ZoomActualSize").Command("ZoomActualSize").Key("Left").Key("Up").Key("Left", repeat: true);
        scripts.Add(edge.Done());

        var navigate = Begin("arrow-keys-at-edge-navigate-setting", 1280, 720, 1.0, 6000, 4000, "{\"arrowKeyNavigatesAtZoomEdge\":true}");
        navigate.Command("ZoomActualSize").Key("Left").Key("Left", repeat: true).Key("Right").Key("Up");
        scripts.Add(navigate.Done());

        var big = Begin("arrow-keys-step-25pct", 1280, 720, 1.0, 6000, 4000, "{\"arrowPanStepPercent\":25}");
        big.Command("ZoomActualSize").Key("Down").Key("Down").Key("Right");
        scripts.Add(big.Done());

        var fit = Begin("arrow-keys-at-fit-navigate", 1280, 720, 1.0, 6000, 4000, Default);
        fit.Key("Right").Key("Left").Key("Down");
        scripts.Add(fit.Done());

        var kinetic = Begin("arrow-keys-kinetic-impulse-frames", 1280, 720, 1.0, 6000, 4000, "{\"kineticPanEnabled\":true,\"kineticGlideSmoothing\":\"Off\"}");
        kinetic.Command("ZoomActualSize").Key("Right", repeat: false).Frames(start: 56, count: 40, intervalMs: 16);
        kinetic.Key("Down").Key("Down", repeat: true).Frames(start: 700, count: 60, intervalMs: 16);
        scripts.Add(kinetic.Done());
    }

    private static void AddKeyboardZoom(List<GoldenInputScript> scripts)
    {
        var centre = Begin("keys-add-subtract-viewport-centre-anchor", 1280, 720, 1.0, 6000, 4000, "{\"keyboardZoomAnchor\":\"ViewportCentre\"}");
        centre.Move(100, 100).Key("Add").Key("Add").Key("Add").Key("Subtract");
        scripts.Add(centre.Done());

        var pointer = Begin("keys-add-subtract-pointer-anchor", 1280, 720, 1.0, 6000, 4000, "{\"keyboardZoomAnchor\":\"Pointer\"}");
        pointer.Move(1000, 600).Key("Add").Key("Add").Key("Subtract").Key("D1");
        scripts.Add(pointer.Done());

        var actual = Begin("keys-D1-actual-size-then-F-fit", 1280, 720, 1.0, 6000, 4000, "{\"keyboardZoomAnchor\":\"Pointer\"}");
        actual.Move(300, 300).Key("D1").Key("F").Key("D1");
        scripts.Add(actual.Done());

        var step = Begin("keys-zoom-step-5pct", 1280, 720, 1.0, 6000, 4000, "{\"keyboardZoomStepPercent\":5}");
        step.Key("Add").Key("Add").Key("Add").Key("Subtract");
        scripts.Add(step.Done());

        var repeat = Begin("keys-zoom-in-auto-repeat", 1280, 720, 1.0, 6000, 4000, Default);
        repeat.Key("Add").Key("Add", repeat: true).Key("Add", repeat: true).Key("Add", repeat: true);
        scripts.Add(repeat.Done());

        var ctrl = Begin("keys-ctrl-add-is-not-zoom", 1280, 720, 1.0, 6000, 4000, Default);
        ctrl.Key("Add", modifiers: "Control").Key("Add");
        scripts.Add(ctrl.Done());
    }

    private static void AddTouchpad(List<GoldenInputScript> scripts)
    {
        var pan = Begin("touchpad-pan-zoomed-vertical-horizontal", 1280, 720, 1.0, 6000, 4000, Default);
        pan.Command("ZoomActualSize");
        pan.Wheel(-30, 640, 360, touchpad: true, dt: 8).Wheel(-45, 640, 360, touchpad: true, dt: 8).HWheel(60, 640, 360, touchpad: true, dt: 8)
            .Wheel(40, 640, 360, touchpad: true, dt: 8);
        scripts.Add(pan.Done());

        var swipe = Begin("touchpad-swipe-at-fit-navigates", 1280, 720, 1.0, 6000, 4000, "{\"touchpadSwipeDistancePerImage\":200}");
        swipe.Wheel(-60, 640, 360, touchpad: true, dt: 8).Wheel(-60, 640, 360, touchpad: true, dt: 8).Wheel(-90, 640, 360, touchpad: true, dt: 8)
            .Wheel(70, 640, 360, touchpad: true, dt: 8);
        scripts.Add(swipe.Done());

        var pinch = Begin("touchpad-pinch-ctrl-wheel-zooms", 1280, 720, 1.0, 6000, 4000, Default);
        pinch.Wheel(40, 640, 360, ctrl: true, touchpad: true, dt: 8).Wheel(40, 640, 360, ctrl: true, touchpad: true, dt: 8)
            .Wheel(-40, 640, 360, ctrl: true, touchpad: true, dt: 8);
        scripts.Add(pinch.Done());

        var off = Begin("touchpad-swipe-disabled-uses-mouse-path", 1280, 720, 1.0, 6000, 4000, "{\"touchpadSwipeEnabled\":false}");
        off.Wheel(-60, 640, 360, touchpad: true, dt: 8).Wheel(-60, 640, 360, touchpad: true, dt: 8).HWheel(60, 640, 360, touchpad: true, dt: 8);
        scripts.Add(off.Done());

        var sideways = Begin("touchpad-sideways-swipe-at-fit-ignored", 1280, 720, 1.0, 6000, 4000, Default);
        sideways.HWheel(120, 640, 360, touchpad: true, dt: 8).HWheel(-120, 640, 360, touchpad: true, dt: 8);
        scripts.Add(sideways.Done());
    }

    private static void AddMiddleClick(List<GoldenInputScript> scripts)
    {
        foreach (var action in new[] { "None", "ClickZoom", "ActualSize", "Fit", "FitWidth", "FitWidth2", "PreviousImage", "NextImage" })
        {
            var script = Begin($"middle-click-{action}", 1280, 720, 1.0, 6000, 4000,
                $"{{\"middleClickAction\":\"{action}\",\"fitWidthAnchor\":\"TopThird\",\"clickZoomPercent\":200}}");
            script.MiddlePress(640, 360).MiddlePress(640, 360);
            scripts.Add(script.Done());
        }
    }

    private static void AddNavigationAndInitialView(List<GoldenInputScript> scripts)
    {
        foreach (var keep in new[] { true, false })
        {
            var script = Begin($"three-images-keep-zoom-{(keep ? "on" : "off")}", 1280, 720, 1.0, 6000, 4000,
                $"{{\"keepZoomAcrossImages\":{(keep ? "true" : "false")}}}");
            script.Wheel(120, 640, 360).Wheel(120, 640, 360).Command("LoadImage", 4000, 6000).Command("LoadImage", 6000, 4000).Wheel(120, 640, 360);
            scripts.Add(script.Done());
        }

        foreach (var (mode, extra) in new[]
        {
            ("FitWidth", "\"fitWidthAnchor\":\"TopThird\""),
            ("FitHeight", "\"fitWidthAnchor\":\"Centre\""),
            ("Percent100", "\"fitWidthAnchor\":\"Centre\""),
            ("Percent200", "\"fitWidthAnchor\":\"Centre\""),
            ("ClickZoomLevel", "\"clickZoomPercent\":150"),
            ("Fit", "\"fitWidthAnchor\":\"Centre\""),
        })
        {
            var script = Begin($"initial-view-{mode}-on-next-images", 1280, 720, 1.0, 6000, 4000, $"{{\"initialViewMode\":\"{mode}\",{extra}}}");
            script.Command("LoadImage", 4000, 6000).Command("LoadImage", 800, 600).Command("LoadImage", 12000, 1000);
            scripts.Add(script.Done());
        }
    }

    private static void AddEnvironmentChanges(List<GoldenInputScript> scripts)
    {
        var swap = Begin("swap-raw-size-at-100pct", 1280, 720, 1.0, 6000, 4000, Default);
        swap.Command("ZoomActualSize").Wheel(120, 900, 500).Command("SwapSourceSize", 6016, 4016).Command("SwapSourceSize", 5984, 3992);
        scripts.Add(swap.Done());

        var swapFit = Begin("swap-raw-size-at-fit", 1280, 720, 1.0, 6000, 4000, Default);
        swapFit.Command("SwapSourceSize", 6016, 4016);
        scripts.Add(swapFit.Done());

        var swapWidth = Begin("swap-raw-size-keeps-fitwidth", 1280, 720, 1.0, 6000, 4000, Default);
        swapWidth.Command("FitWidth").Command("SwapSourceSize", 6240, 4160).Command("SwapSourceSize", 5900, 3900);
        scripts.Add(swapWidth.Done());

        var dpiZoom = Begin("dpi-change-at-100pct-125-then-200", 1280, 720, 1.0, 6000, 4000, Default);
        dpiZoom.Command("ZoomActualSize").Command("SetDpi", 1.25, 0).Command("SetDpi", 2.0, 0).Command("SetDpi", 1.0, 0);
        scripts.Add(dpiZoom.Done());

        var dpiFit = Begin("dpi-change-at-fit-150", 1280, 720, 1.0, 6000, 4000, Default);
        dpiFit.Command("SetDpi", 1.5, 0).Command("ZoomActualSize");
        scripts.Add(dpiFit.Done());

        var dpiWidth = Begin("dpi-change-during-fitwidth", 1280, 720, 1.0, 6000, 4000, Default);
        dpiWidth.Command("FitWidth").Command("SetDpi", 1.5, 0).Command("FitWidth");
        scripts.Add(dpiWidth.Done());

        var resizeWidth = Begin("resize-while-fitwidth", 1280, 720, 1.0, 6000, 4000, Default);
        resizeWidth.Command("FitWidth").Resize(1000, 600).Resize(1600, 900).Command("FitWidth");
        scripts.Add(resizeWidth.Done());

        var resizeFit = Begin("resize-at-fit", 1280, 720, 1.0, 6000, 4000, Default);
        resizeFit.Resize(1000, 600).Resize(1920, 1080).Resize(640, 480);
        scripts.Add(resizeFit.Done());

        var resizeZoom = Begin("resize-while-zoomed-200pct", 1280, 720, 1.0, 6000, 4000, Default);
        resizeZoom.Wheel(120, 640, 360).Wheel(120, 640, 360).Wheel(120, 640, 360).Wheel(120, 640, 360).Resize(900, 500).Resize(1280, 720);
        scripts.Add(resizeZoom.Done());

        var resizeHeight = Begin("resize-while-fitheight", 1280, 720, 1.0, 6000, 4000, Default);
        resizeHeight.Command("FitHeight").Resize(1280, 500).Resize(1280, 900).Command("FitHeight");
        scripts.Add(resizeHeight.Done());
    }

    private static void AddMenuZoom(List<GoldenInputScript> scripts)
    {
        var level = Begin("menu-zoom-to-level-and-presets", 1280, 720, 1.0, 6000, 4000, "{\"clickZoomPercent\":150}");
        level.Command("ZoomToLevelMenu").Command("SetClickZoomLevel", 70, 0).Command("SetClickZoomLevel", 400, 0).Command("ToggleFit");
        scripts.Add(level.Done());

        var custom = Begin("menu-set-click-level-10-and-800", 1280, 720, 1.0, 6000, 4000, Default);
        custom.Command("SetClickZoomLevel", 10, 0).Command("SetClickZoomLevel", 800, 0).Command("SetClickZoomLevel", 300, 0);
        scripts.Add(custom.Done());

        var big = Begin("hidpi-4k-fit-wheel-pan", 3840, 2160, 2.0, 6000, 4000, "{\"kineticPanEnabled\":false}");
        big.Wheel(120, 1920, 1080).Wheel(120, 1920, 1080).Press(1900, 1000).Move(1700, 900, 16).Move(1500, 800, 32).Release(1500, 800, 48);
        scripts.Add(big.Done());
    }

    private static ScriptBuilder Begin(string name, double clientWidth, double clientHeight, double dpi, int imageWidth, int imageHeight, string settingsJson) =>
        new(name, new GoldenSetup(clientWidth, clientHeight, dpi, imageWidth, imageHeight, settingsJson));

    /// <summary>
    /// Fluent builder. Wheel/key/command/resize steps advance a running clock by <c>dt</c> (default 120 ms between wheel notches, 40 ms
    /// otherwise). Explicit times of press/move/release/frame are relative to the last press made without one (the gesture origin).
    /// </summary>
    private sealed class ScriptBuilder(string name, GoldenSetup setup)
    {
        private readonly List<GoldenInputStep> _steps = [];
        private int _time;
        private int _origin;

        public GoldenInputScript Done() => new(name, setup, _steps, []);

        private ScriptBuilder Add(string kind, double x, double y, int delta, string? key, string? modifiers, int timestamp, string? command)
        {
            _time = Math.Max(_time, timestamp);
            _steps.Add(new GoldenInputStep(kind, x, y, delta, key, modifiers, timestamp, command));
            return this;
        }

        private int Tick(int dt) => _time += dt;

        public ScriptBuilder Wheel(int delta, double x, double y, bool ctrl = false, bool touchpad = false, int dt = 120) =>
            Add("wheel", x, y, delta, touchpad ? "Touchpad" : null, ctrl ? "Control" : null, Tick(dt), null);

        public ScriptBuilder HWheel(int delta, double x, double y, bool ctrl = false, bool touchpad = false, int dt = 120) =>
            Add("hwheel", x, y, delta, touchpad ? "Touchpad" : null, ctrl ? "Control" : null, Tick(dt), null);

        public ScriptBuilder Press(double x, double y, int? at = null, int clicks = 1)
        {
            if (at is null) _origin = Tick(40);
            return Add("press", x, y, clicks, "Left", null, at is { } relative ? _origin + relative : _origin, null);
        }

        public ScriptBuilder MiddlePress(double x, double y, int clicks = 1) => Add("press", x, y, clicks, "Middle", null, Tick(40), null);

        public ScriptBuilder Move(double x, double y, int? at = null) =>
            Add("move", x, y, 0, null, null, at is { } relative ? _origin + relative : Tick(40), null);

        public ScriptBuilder Release(double x, double y, int at) => Add("release", x, y, 0, null, null, _origin + at, null);

        public ScriptBuilder Click(double x, double y) => Press(x, y).Release(x, y, 20);

        public ScriptBuilder Key(string key, string? modifiers = null, bool repeat = false) =>
            Add("key", 0, 0, 0, key, repeat ? (modifiers is null ? "Repeat" : modifiers + ",Repeat") : modifiers, Tick(40), null);

        public ScriptBuilder Command(string command, double x = 0, double y = 0) => Add("command", x, y, 0, null, null, Tick(40), command);

        public ScriptBuilder Resize(double width, double height) => Add("resize", width, height, 0, null, null, Tick(40), null);

        public ScriptBuilder FrameAt(int at) => Add("frame", 0, 0, 0, null, null, _origin + at, null);

        public ScriptBuilder Frames(int start, int count, double intervalMs)
        {
            var previous = (double)Math.Max(0, start - (int)intervalMs);
            for (var i = 0; i < count; i++)
            {
                var at = start + (int)Math.Round(i * intervalMs, MidpointRounding.AwayFromZero);
                Add("frame", 0, 0, (int)Math.Round(at - previous), null, null, _origin + at, null);
                previous = at;
            }
            return this;
        }
    }

    internal static string Describe(GoldenInputScript script) =>
        string.Create(CultureInfo.InvariantCulture, $"{script.Name} ({script.Steps.Count} steps)");
}
