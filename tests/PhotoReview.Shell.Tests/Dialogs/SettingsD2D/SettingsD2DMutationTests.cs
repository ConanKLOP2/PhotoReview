using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.SettingsD2D.D2DTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>Region của cửa sổ con (đọc lại SetWindowRgn); chỉ dùng cho test đột biến của Settings D2D.</summary>
internal static unsafe partial class D2DMutNative
{
    [LibraryImport("user32.dll", EntryPoint = "GetWindowRgn")]
    private static partial int GetWindowRgn(nint hwnd, nint region);

    [LibraryImport("gdi32.dll", EntryPoint = "GetRgnBox")]
    private static partial int GetRgnBox(nint region, Rect* rect);

    /// <summary>Hộp bao của region cửa sổ (toạ độ cửa sổ); <c>HasRegion</c> = false khi cửa sổ không có region (không bị cắt).</summary>
    public static (bool HasRegion, int Left, int Top, int Right, int Bottom) WindowRegionBox(nint hwnd)
    {
        var region = D2DNative.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(hwnd, region) == 0) // ERROR
            {
                return (false, 0, 0, 0, 0);
            }

            Rect box = default;
            _ = GetRgnBox(region, &box);
            return (true, box.Left, box.Top, box.Right, box.Bottom);
        }
        finally
        {
            _ = Gdi32.DeleteObject(region);
        }
    }
}

/// <summary>
/// SPIKE D2D Settings - đột biến sống sót của đợt kiểm tra đột biến thủ công (M1, M3, M10-M14, M16, M17; M2, M4-M9, M15 đã bị test hiện có bắt): mỗi test dưới đây đỏ khi
/// đúng một đột biến tương ứng được áp vào mã nguồn spike và xanh trên mã gốc. Dùng host offscreen (logic/bố cục) và cửa sổ thật
/// (chế độ lai) giống các test hiện có; không chờ theo thời gian.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsD2DMutationTests
{
    private static AppSettings Baseline() => new()
    {
        ShowInfoOverlay = true,
        ToolbarAutoHide = true,
        InfoOverlayAutoHide = false,
        ShowExifInfo = false,
        ToolbarOpacityPercent = 70,
    };

    private static D2DOffscreenHost NewHost(int width = 640, int height = 560, AppSettings? settings = null)
        => new(width, height, 1.0, dark: true, hardware: false, settings ?? Baseline());

    private static D2DFieldWidget Field(D2DSettingsView v, string id) => (D2DFieldWidget)v.Find(id)!;

    private static (int X, int Y) CenterOf(D2DSettingsView view, PxRect contentRect)
        => (contentRect.X + (contentRect.Width / 2), contentRect.Y + (contentRect.Height / 2) + view.Viewport.Y - view.OffsetPx);

    // ---- M1: ScrollTo kẹp trên ----------------------------------------------------------------------------------------------

    [Fact]
    public void ScrollTo_NeverExceedsMaxOffset_ForDirectCallWheelAndEndKey()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var max = v.MaxOffsetPx;
        Assert.True(max > 0);
        v.ScrollTo(max + 500);
        Assert.Equal(max, v.OffsetPx);
        v.ScrollTo(-50);
        Assert.Equal(0, v.OffsetPx);
        v.Wheel(-1000);
        Assert.Equal(max, v.OffsetPx);
        v.ScrollTo(0);
        v.KeyDown(VkEnd, false, false, false);
        Assert.Equal(max, v.OffsetPx);
        v.KeyDown(VkNext, false, false, false); // PageDown ở cuối: không vượt
        Assert.Equal(max, v.OffsetPx);
        host.Render(read: false);
        Assert.Equal(max, v.OffsetPx);
    }

    // ---- M3: EnsureVisible cuộn lên khi phần tử nằm trên khung nhìn -----------------------------------------------------------

    [Fact]
    public void Focus_ScrollsDownToAlignTheBottom_AndScrollsUpToAlignTheTop()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var margin = 8; // S(8) ở 96 DPI
        var below = v.Find("Title" + TitleBarFields.ShutterSpeed)!;
        Assert.True(below.Bounds.Bottom + margin > v.Viewport.Height, "target starts below the fold");
        v.SetFocus(below);
        Assert.Equal(Math.Min(below.Bounds.Bottom + margin - v.Viewport.Height, v.MaxOffsetPx), v.OffsetPx);

        var above = v.Find("ShowInfoOverlay")!;
        v.ScrollTo(v.MaxOffsetPx);
        Assert.True(above.Bounds.Y - margin < v.OffsetPx, "target is above the viewport");
        v.SetFocus(above);
        Assert.Equal(Math.Max(0, above.Bounds.Y - margin), v.OffsetPx);
    }

    // ---- M10: danh sách combobox lật lên trên khi không đủ chỗ dưới ---------------------------------------------------------------

    [Fact]
    public void ComboPopup_OpensBelowWhenThereIsRoom_AndFlipsAboveNearTheBottomEdge()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost(width: 640, height: 360);
        var v = host.View;
        var combo = (D2DComboWidget)v.Find("LoadingMode")!;
        v.SetFocus(combo);

        // Hộp ở sát đỉnh khung nhìn: còn nhiều chỗ -> danh sách mở xuống dưới, liền ngay dưới hộp.
        v.ScrollTo(combo.BoxBounds.Y - 4);
        var boxTop = combo.BoxBounds.Y + v.Viewport.Y - v.OffsetPx;
        v.KeyDown(VkSpace, false, false, false);
        Assert.True(v.PopupOpen);
        var below = v.PopupBounds!.Value;
        Assert.Equal(boxTop + combo.BoxBounds.Height, below.Y);
        v.KeyDown(VkEscape, false, false, false);
        Assert.False(v.PopupOpen);

        // Hộp ở sát đáy khung nhìn: không đủ chỗ dưới -> lật lên, nằm hoàn toàn phía trên hộp và trong cửa sổ.
        v.ScrollTo(combo.BoxBounds.Bottom - v.Viewport.Height);
        boxTop = combo.BoxBounds.Y + v.Viewport.Y - v.OffsetPx;
        v.KeyDown(VkSpace, false, false, false);
        Assert.True(v.PopupOpen);
        var above = v.PopupBounds!.Value;
        Assert.True(combo.BoxBounds.Bottom + v.Viewport.Y - v.OffsetPx + above.Height > v.ClientHeight, "precondition: does not fit below");
        Assert.True(above.Bottom <= boxTop, $"popup {above} must end above the box (top {boxTop})");
        Assert.True(above.Y >= 0);
    }

    // ---- M11: thumb thanh cuộn có chiều cao tối thiểu -------------------------------------------------------------------------------

    [Fact]
    public void ScrollThumb_HasAMinimumHeight_WhenTheContentIsMuchTallerThanTheViewport()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost(width: 640, height: 220);
        var v = host.View;
        var track = v.Viewport.Height;
        var proportional = (int)((long)track * v.Viewport.Height / v.ContentHeightPx);
        Assert.True(proportional < 24, $"precondition: proportional thumb ({proportional}) is below the minimum");
        var (thumb, visible) = v.ThumbBounds;
        Assert.True(visible);
        Assert.Equal(24, thumb.Height);
        v.ScrollTo(v.MaxOffsetPx);
        Assert.Equal(24, v.ThumbBounds.Thumb.Height);
        Assert.True(v.ThumbBounds.Thumb.Bottom <= v.Viewport.Y + track);
    }

    // ---- M12: bấm lên thanh trượt đặt TÂM thumb dưới con trỏ -----------------------------------------------------------------------

    [Fact]
    public void SliderClick_CentresTheThumbUnderTheCursor_SoTheLeftEdgeGivesTheMinimum()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var slider = (D2DSliderWidget)v.Find("ToolbarOpacity")!;
        v.SetFocus(slider);
        var thumbW = 10; // S(10) ở 96 DPI
        var y = CenterOf(v, slider.TrackBounds).Y;
        Assert.True(v.Viewport.Contains(slider.TrackBounds.X, y));
        var left = slider.TrackBounds.X + (thumbW / 2);
        v.MouseDown(left, y);
        v.MouseUp(left, y);
        Assert.Equal(slider.Min, slider.Value);
        var right = slider.TrackBounds.Right - (thumbW / 2);
        v.MouseDown(right, y);
        v.MouseUp(right, y);
        Assert.Equal(slider.Max, slider.Value);
        var mid = slider.TrackBounds.X + (slider.TrackBounds.Width / 2);
        v.MouseDown(mid, y);
        v.MouseUp(mid, y);
        Assert.InRange(slider.Value, slider.Min + ((slider.Max - slider.Min) * 48 / 100), slider.Min + ((slider.Max - slider.Min) * 52 / 100));
    }

    // ---- M13: ô trễ của thanh công cụ theo tick "tự ẩn" --------------------------------------------------------------------------

    [Fact]
    public void ToolbarDelay_FollowsTheAutoHideTick_OnLoadAndOnToggle()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using (var off = NewHost(settings: new AppSettings { ShowInfoOverlay = true, ToolbarAutoHide = false }))
        {
            var delayOff = Field(off.View, "ToolbarAutoHideDelay");
            Assert.False(delayOff.Enabled);
            Assert.False(delayOff.Focusable);
            Assert.DoesNotContain(delayOff, off.View.FocusOrder());
        }

        using var host = NewHost();
        var v = host.View;
        var delay = Field(v, "ToolbarAutoHideDelay");
        var auto = (D2DCheckWidget)v.Find("ToolbarAutoHide")!;
        Assert.True(delay.Enabled);
        v.SetFocus(auto);
        v.KeyDown(VkSpace, false, false, false);
        Assert.False(auto.Checked);
        Assert.False(delay.Enabled);
        Assert.DoesNotContain(delay, v.FocusOrder());
        v.KeyDown(VkSpace, false, false, false);
        Assert.True(auto.Checked);
        Assert.True(delay.Enabled);
        Assert.Contains(delay, v.FocusOrder());
    }

    // ---- M14: Collect không đảo "thông tin tệp" / "thông tin thư mục" -----------------------------------------------------------

    [Fact]
    public void Collect_KeepsFileInfoAndFolderInfoApart()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var file = (D2DCheckWidget)host.View.Find("ShowFileInfo")!;
        var folder = (D2DCheckWidget)host.View.Find("ShowFolderInfo")!;
        file.Checked = true;
        folder.Checked = false;
        var state = new SettingsFormState();
        host.Page.Collect(state);
        Assert.True(state.ShowFileInfo);
        Assert.False(state.ShowFolderInfo);

        file.Checked = false;
        folder.Checked = true;
        host.Page.Collect(state);
        Assert.False(state.ShowFileInfo);
        Assert.True(state.ShowFolderInfo);
    }

    // ---- cửa sổ thật, chế độ lai (M16, M17) ---------------------------------------------------------------------------------------

    [Fact]
    public void Hosted_EditIsClippedToTheViewport_WhenItStraddlesTheTopEdge()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var options = new D2DSettingsHostOptions(Dark: true, EditMode: D2DEditMode.HostedWin32);
        WithWindow(options, (window, page) =>
        {
            var v = window.View;
            var field = Field(v, "InfoOverlayFontSize");
            var hwnd = window.HostedHandles.First(h => h.Id == field.Id).Hwnd;
            var hidden = 3;
            v.ScrollTo(field.BoxBounds.Y + hidden);
            Assert.Equal(field.BoxBounds.Y + hidden, v.OffsetPx);
            window.SyncHosted();
            var client = field.BoxBounds.Offset(0, v.Viewport.Y - v.OffsetPx);
            Assert.Equal(v.Viewport.Y - hidden, client.Y);
            Assert.True(User32.IsWindowVisible(hwnd));
            var region = D2DMutNative.WindowRegionBox(hwnd);
            Assert.True(region.HasRegion, "the real edit must be clipped by a window region");
            Assert.Equal((0, hidden, client.Width, client.Height), (region.Left, region.Top, region.Right, region.Bottom));
            return 0;
        });
    }

    [Fact]
    public void Hosted_EditIsDisabledWithItsParentTick_ThroughRealMouseMessages()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var options = new D2DSettingsHostOptions(Dark: true, EditMode: D2DEditMode.HostedWin32);
        WithWindow(options, (window, page) =>
        {
            var v = window.View;
            v.ScrollTo(0);
            window.SyncHosted();
            var parent = (D2DCheckWidget)v.Find("ToolbarAutoHide")!;
            var hwnd = window.HostedHandles.First(h => h.Id == "ToolbarAutoHideDelay").Hwnd;
            Assert.True(parent.Checked);
            Assert.True(User32.IsWindowEnabled(hwnd));
            var (x, y) = CenterOf(v, parent.Bounds);
            Assert.True(v.Viewport.Contains(x, y), "the tick is inside the viewport");
            Click(window.Hwnd, x, y);
            Pump();
            Assert.False(parent.Checked);
            Assert.False(User32.IsWindowEnabled(hwnd), "real edit must be disabled when the parent tick is off");
            Click(window.Hwnd, x, y);
            Pump();
            Assert.True(parent.Checked);
            Assert.True(User32.IsWindowEnabled(hwnd), "real edit must be enabled again");
            return 0;
        });
    }
}
