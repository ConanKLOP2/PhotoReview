using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.SettingsD2D.D2DTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE D2D Settings - T1: ĐÚNG PIXEL khi cuộn trang Display. Kịch bản 15 bước GIỐNG <c>SettingsScrollPaintTests</c> (lăn chuột, kéo
/// thumb, trang/dòng, nhảy cuối/đầu). Sau MỖI bước:
/// <list type="bullet">
/// <item>ảnh THỰC = khung của host đã dùng suốt kịch bản (cùng bề mặt, có lịch sử các khung trước);</item>
/// <item>ảnh CHUẨN = host mới tinh, đặt thẳng tới offset đó, vẽ một lần.</item>
/// </list>
/// Phải bằng nhau TỪNG pixel (dung sai 0). Thêm: cắt từ ảnh "cao bằng nội dung" (không cuộn) phải bằng ảnh đã cuộn - kiểm độc lập việc
/// dịch chuyển; và cửa sổ thật (thông điệp thật tới HWND, swap chain flip-model) đi đúng offset và Present thành công.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsD2DScrollPixelTests(ITestOutputHelper output)
{
    private enum Op
    {
        Initial,
        WheelDown3,
        WheelDown5,
        ThumbHalf,
        End,
        WheelUp1,
        Home,
        PageDown,
        PageDown2,
        ThumbDrag,
        ThumbNear,
        LineDown,
        LineUp,
        ThumbDragBack,
        HomeAgain,
    }

    public static TheoryData<uint, bool, bool> OffscreenCases()
    {
        var data = new TheoryData<uint, bool, bool>();
        foreach (var dpi in new uint[] { 96, 144 })
        {
            foreach (var dark in new[] { true, false })
            {
                foreach (var hardware in new[] { true, false })
                {
                    data.Add(dpi, dark, hardware);
                }
            }
        }

        return data;
    }

    private static (int W, int H) Size(uint dpi) => ((int)Math.Round(640 * dpi / 96.0), (int)Math.Round(560 * dpi / 96.0));

    [Theory]
    [MemberData(nameof(OffscreenCases))]
    public void Scrolling_EveryStep_EqualsAFreshRenderAtThatOffset(uint dpi, bool dark, bool hardware)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (w, h) = Size(dpi);
        var scale = dpi / 96.0;
        using var host = new D2DOffscreenHost(w, h, scale, dark, hardware);
        var tag = $"{dpi}dpi-{(dark ? "dark" : "classic")}-{(host.IsWarp ? "warp" : "gpu")}";
        Assert.True(host.View.MaxOffsetPx > 0, "page must be scrollable at this size");
        var failures = new List<string>();
        var ops = Enum.GetValues<Op>();
        var directory = OutputDirectory();
        var lastOffset = -1;
        foreach (var op in ops)
        {
            Apply(host.View, op, () => host.Render(read: false));
            var actual = host.Render();
            Assert.True(actual.DistinctColors() > 4, $"{tag}: capture is blank");
            var offset = host.View.OffsetPx;

            using var fresh = new D2DOffscreenHost(w, h, scale, dark, hardware);
            fresh.View.ScrollTo(offset);
            Assert.Equal(offset, fresh.View.OffsetPx);
            var expected = fresh.Render();
            var diff = actual.DiffCount(expected);
            if (diff != 0)
            {
                failures.Add($"{op} (offset {offset}): {diff} pixels differ");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, $"{tag}-{op}-actual.png"), EncodePng(actual));
                File.WriteAllBytes(Path.Combine(directory, $"{tag}-{op}-expected.png"), EncodePng(expected));
            }

            lastOffset = offset;
        }

        Assert.True(lastOffset == 0, "scenario ends at the top");
        Assert.True(failures.Count == 0, $"{tag}: {failures.Count}/{ops.Length} steps differ:\n" + string.Join('\n', failures));
        output.WriteLine($"{tag}: 15/15 steps, 0 differing pixels (adapter: {host.Adapter})");
    }

    [Theory]
    [InlineData(96u, true)]
    [InlineData(144u, true)]
    [InlineData(144u, false)]
    public void ScrolledFrame_EqualsTheCropOfAnUnscrolledTallFrame(uint dpi, bool dark)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (w, h) = Size(dpi);
        var scale = dpi / 96.0;
        using var scrolled = new D2DOffscreenHost(w, h, scale, dark, hardware: false);
        var content = scrolled.View.ContentHeightPx;
        var viewportH = scrolled.View.Viewport.Height;
        var viewportTop = scrolled.View.Viewport.Y;
        var viewportRight = scrolled.View.Viewport.Right;
        var headerAndFooter = h - viewportH;
        // Host cao: viewport = toàn bộ nội dung nên offset = 0 và mọi dòng của nội dung hiện ra một lần.
        using var tall = new D2DOffscreenHost(w, content + headerAndFooter, scale, dark, hardware: false);
        Assert.Equal(0, tall.View.MaxOffsetPx);
        var tallShot = tall.Render();
        var failures = new List<string>();
        foreach (var offset in new[] { 0, 1, 17, scrolled.View.MaxOffsetPx / 3, scrolled.View.MaxOffsetPx / 2, scrolled.View.MaxOffsetPx - 1, scrolled.View.MaxOffsetPx })
        {
            scrolled.View.ScrollTo(offset);
            var shot = scrolled.Render();
            var actualOffset = scrolled.View.OffsetPx;
            var count = 0;
            for (var y = 0; y < viewportH; y++)
            {
                for (var x = 0; x < viewportRight; x++)
                {
                    var a = (((viewportTop + y) * shot.Width) + x) * 4;
                    var e = (((viewportTop + actualOffset + y) * tallShot.Width) + x) * 4;
                    if (shot.Bgra[a] != tallShot.Bgra[e] || shot.Bgra[a + 1] != tallShot.Bgra[e + 1] || shot.Bgra[a + 2] != tallShot.Bgra[e + 2])
                    {
                        count++;
                    }
                }
            }

            if (count != 0)
            {
                failures.Add($"offset {actualOffset}: {count} pixels differ from the crop");
                var dir = OutputDirectory();
                Directory.CreateDirectory(dir);
                var cropBytes = new byte[shot.Bgra.Length];
                for (var y = 0; y < viewportH; y++)
                {
                    Array.Copy(tallShot.Bgra, (((viewportTop + actualOffset + y) * tallShot.Width) * 4), cropBytes, ((viewportTop + y) * shot.Width) * 4, shot.Width * 4);
                }

                File.WriteAllBytes(Path.Combine(dir, $"crop-{dpi}-{actualOffset}-actual.png"), EncodePng(shot));
                File.WriteAllBytes(Path.Combine(dir, $"crop-{dpi}-{actualOffset}-crop.png"), EncodePng(new D2DShot(shot.Width, shot.Height, cropBytes)));
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Theory]
    [InlineData(96u, false)]
    [InlineData(144u, false)]
    [InlineData(96u, true)]
    [InlineData(144u, true)]
    public void RealWindow_SameMessages_ReachTheSameOffsetsAndPresent(uint dpi, bool hosted)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var mode = hosted ? D2DEditMode.HostedWin32 : D2DEditMode.SelfDrawn;
        var options = new D2DSettingsHostOptions(Dark: true, EditMode: mode, DpiOverride: dpi);
        var report = WithWindow(options, (window, page) =>
        {
            var view = window.View;
            var hwnd = window.Hwnd;
            var (w, h) = (view.ClientWidth, view.ClientHeight);
            using var replay = new D2DOffscreenHost(w, h, window.Scale, dark: true, hardware: true, mode: mode);
            Assert.Equal(replay.View.MaxOffsetPx, view.MaxOffsetPx);
            var lines = new List<string>();
            var presentedBefore = window.Presented;
            foreach (var op in Enum.GetValues<Op>())
            {
                SendToWindow(window, op);
                Pump();
                window.UpdateNow();
                Apply(replay.View, op, null);
                Assert.Equal(replay.View.OffsetPx, view.OffsetPx);
                // Desktop ẩn (hoặc cửa sổ bị che): DXGI báo Occluded - hợp lệ; chỉ Failed là lỗi.
            Assert.NotEqual(PhotoReview.Shell.Rendering.PresentResult.Failed, window.LastPresentResult);
                if (mode == D2DEditMode.HostedWin32)
                {
                    AssertHostedGeometry(window);
                }
            }

            Assert.True(window.Presented > presentedBefore, "frames were presented");
            // Ảnh: PrintWindow (nếu DWM trả được nội dung swap chain) so với ảnh chuẩn GPU offscreen cùng offset.
            view.ScrollTo(view.MaxOffsetPx / 2);
            window.RenderNow();
            Pump();
            var shot = PrintWindowShot(hwnd, w, h);
            replay.View.ScrollTo(view.OffsetPx);
            var expected = replay.Render();
            lines.Add(shot is null
                ? "PrintWindow: not available on this desktop"
                : $"PrintWindow vs GPU offscreen at offset {view.OffsetPx}: {shot.DiffCount(expected, replay.View.Viewport.Right, replay.View.Viewport.Y, replay.View.Viewport.Bottom)} differing viewport pixels (colors {shot.DistinctColors()})");
            return (lines, window.Surface.IsWarp, window.Surface.AdapterDescription);
        });
        output.WriteLine($"{dpi} dpi {mode}: 15 steps same offsets, presented OK; warp={report.IsWarp}; adapter={report.AdapterDescription}");
        foreach (var line in report.lines)
        {
            output.WriteLine(line);
        }
    }

    // ---- kịch bản ---------------------------------------------------------------------------------------------------------

    private static string OutputDirectory()
        => Environment.GetEnvironmentVariable("PHOTOREVIEW_D2D_OUT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "settings-d2d-scroll");

    /// <summary>Cùng thao tác, đi qua API chuột/phím của view (giống thông điệp cửa sổ thật). <paramref name="between"/> = vẽ khung trung gian khi kéo thumb.</summary>
    private static void Apply(D2DSettingsView view, Op op, Action? between)
    {
        var max = view.MaxOffsetPx;
        switch (op)
        {
            case Op.Initial:
                break;
            case Op.WheelDown3:
                view.Wheel(-3);
                break;
            case Op.WheelDown5:
                view.Wheel(-5);
                break;
            case Op.ThumbHalf:
                DragThumb(view, max / 2, null);
                break;
            case Op.End:
                view.ScrollTo(view.MaxOffsetPx); // SB_BOTTOM
                break;
            case Op.WheelUp1:
                view.Wheel(1);
                break;
            case Op.Home:
                view.ScrollTo(0);
                break;
            case Op.PageDown or Op.PageDown2:
                view.ScrollTo(view.OffsetPx + Math.Max(view.LineStepPx, view.Viewport.Height - view.LineStepPx));
                break;
            case Op.ThumbDrag:
                DragThumb(view, max * 9 / 11, between, max * 3 / 11, max * 5 / 11, max * 7 / 11, max * 8 / 11);
                break;
            case Op.ThumbNear:
                DragThumb(view, (max * 9 / 11) + 7, null);
                break;
            case Op.LineDown:
                view.ScrollTo(view.OffsetPx + view.LineStepPx);
                break;
            case Op.LineUp:
                view.ScrollTo(view.OffsetPx - view.LineStepPx);
                break;
            case Op.ThumbDragBack:
                DragThumb(view, max / 5, null);
                break;
            case Op.HomeAgain:
                view.ScrollTo(0);
                break;
        }
    }

    private static void DragThumb(D2DSettingsView view, int target, Action? between, params int[] via)
    {
        var (thumb, visible) = view.ThumbBounds;
        Assert.True(visible);
        var x = thumb.X + 1;
        var grab = thumb.Height / 2;
        view.MouseDown(x, thumb.Y + grab);
        foreach (var offset in via)
        {
            view.MouseMove(x, view.ThumbTopFor(offset) + grab, leftDown: true);
            between?.Invoke();
        }

        var y = view.ThumbTopFor(target) + grab;
        view.MouseMove(x, y, leftDown: true);
        view.MouseUp(x, y);
    }

    private static void SendToWindow(D2DSettingsHost window, Op op)
    {
        var view = window.View;
        var hwnd = window.Hwnd;
        var max = view.MaxOffsetPx;
        switch (op)
        {
            case Op.Initial:
                break;
            case Op.WheelDown3:
                Wheel(hwnd, -3);
                break;
            case Op.WheelDown5:
                Wheel(hwnd, -5);
                break;
            case Op.ThumbHalf:
                DragThumbMessages(window, max / 2);
                break;
            case Op.End:
                User32.SendMessage(hwnd, 0x0115, 7, 0);
                break;
            case Op.WheelUp1:
                Wheel(hwnd, 1);
                break;
            case Op.Home:
                User32.SendMessage(hwnd, 0x0115, 6, 0);
                break;
            case Op.PageDown or Op.PageDown2:
                User32.SendMessage(hwnd, 0x0115, 3, 0);
                break;
            case Op.ThumbDrag:
                DragThumbMessages(window, max * 9 / 11, max * 3 / 11, max * 5 / 11, max * 7 / 11, max * 8 / 11);
                break;
            case Op.ThumbNear:
                DragThumbMessages(window, (max * 9 / 11) + 7);
                break;
            case Op.LineDown:
                User32.SendMessage(hwnd, 0x0115, 1, 0);
                break;
            case Op.LineUp:
                User32.SendMessage(hwnd, 0x0115, 0, 0);
                break;
            case Op.ThumbDragBack:
                DragThumbMessages(window, max / 5);
                break;
            case Op.HomeAgain:
                User32.SendMessage(hwnd, 0x0115, 6, 0);
                break;
        }
    }

    private static void DragThumbMessages(D2DSettingsHost window, int target, params int[] via)
    {
        var view = window.View;
        var hwnd = window.Hwnd;
        var (thumb, _) = view.ThumbBounds;
        var x = thumb.X + 1;
        var grab = thumb.Height / 2;
        User32.SendMessage(hwnd, WindowMessages.WmLButtonDown, 1, MakeLParam(x, thumb.Y + grab));
        foreach (var offset in via)
        {
            User32.SendMessage(hwnd, WindowMessages.WmMouseMove, 1, MakeLParam(x, view.ThumbTopFor(offset) + grab));
            window.RenderNow();
        }

        var y = view.ThumbTopFor(target) + grab;
        User32.SendMessage(hwnd, WindowMessages.WmMouseMove, 1, MakeLParam(x, y));
        User32.SendMessage(hwnd, WindowMessages.WmLButtonUp, 0, MakeLParam(x, y));
    }

    private static unsafe void AssertHostedGeometry(D2DSettingsHost window)
    {
        var areas = window.View.HostedAreas().ToList();
        var handles = window.HostedHandles;
        Assert.Equal(areas.Count, handles.Count);
        foreach (var (widget, client) in areas)
        {
            var hwnd = handles.First(x => x.Id == widget.Id).Hwnd;
            var visible = client.Intersect(window.View.Viewport);
            var shown = User32.IsWindowVisible(hwnd);
            Assert.Equal(visible.Width > 0 && visible.Height > 0, shown);
            if (!shown)
            {
                continue;
            }

            Rect rect = default;
            User32.GetWindowRect(hwnd, &rect);
            var p = new Point { X = rect.Left, Y = rect.Top };
            User32.ScreenToClient(window.Hwnd, &p);
            Assert.Equal(client.X, p.X);
            Assert.Equal(client.Y, p.Y);
            Assert.Equal(client.Width, rect.Right - rect.Left);
        }
    }
}
