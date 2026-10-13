using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.SettingsD2D.D2DTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE D2D Settings - T4/T5: nhập liệu (ô số, combobox) cho CẢ HAI phương án (c2 tự vẽ; c1 control Win32 thật đặt chồng), bàn phím
/// (Tab/Shift+Tab/mũi tên/Space/focus), cây trợ năng, và vòng tròn presenter (Load -> sửa -> Collect -> TrySave) với đúng
/// <c>SettingsPresenter</c>/<c>SettingsFormState</c> hiện có.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsD2DInputTests(ITestOutputHelper output)
{
    private static AppSettings Baseline() => new()
    {
        ShowInfoOverlay = true,
        ToolbarAutoHide = true,
        InfoOverlayAutoHide = false,
        ShowExifInfo = false,
        ToolbarOpacityPercent = 70,
    };

    private static D2DOffscreenHost NewHost(AppSettings? settings = null, uint dpi = 96, FakeD2DClipboard? clipboard = null)
    {
        var scale = dpi / 96.0;
        return new D2DOffscreenHost((int)(640 * scale), (int)(560 * scale), scale, dark: true, hardware: false, settings ?? Baseline(), clipboard: clipboard);
    }

    private static (int X, int Y) CenterOf(D2DSettingsView view, PxRect contentRect)
        => (contentRect.X + (contentRect.Width / 2), contentRect.Y + (contentRect.Height / 2) + view.Viewport.Y - view.OffsetPx);

    private static D2DFieldWidget Field(D2DSettingsView v, string id) => (D2DFieldWidget)v.Find(id)!;

    private static void Type(D2DSettingsView v, string text)
    {
        foreach (var c in text)
        {
            v.Char(c);
        }
    }

    // ---- c2: ô số tự vẽ ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SelfDrawnNumberBox_RejectsLettersAcceptsDigits_AndReportsChanges()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var field = Field(v, "ToolbarAutoHideDelay");
        var changes = new List<string>();
        v.Changed += w => changes.Add(w.Id);
        v.SetFocus(field);
        field.Box.SelectAll();
        Type(v, "2a5x0");
        Assert.Equal("250", field.Box.Text);
        Assert.Equal(2, field.Box.RejectedCount);
        Assert.All(changes, id => Assert.Equal("ToolbarAutoHideDelay", id));
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void SelfDrawnNumberBox_CaretSelectionBackspaceDeleteHomeEnd()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var box = Field(v, "ToolbarAutoHideDelay").Box;
        v.SetFocus(Field(v, "ToolbarAutoHideDelay"));
        box.Text = "12345";
        v.KeyDown(VkHome, false, false, false);
        Assert.Equal(0, box.Caret);
        v.KeyDown(VkRight, false, false, false);
        v.KeyDown(VkRight, false, true, false); // Shift+Right chọn "2"
        Assert.Equal("2", box.SelectedText);
        v.KeyDown(0x2E, false, false, false); // Delete xoá phần chọn
        Assert.Equal("1345", box.Text);
        v.KeyDown(VkEnd, false, false, false);
        v.KeyDown(0x08, false, false, false); // Backspace
        Assert.Equal("134", box.Text);
        v.KeyDown('A', true, false, false);
        Assert.Equal("134", box.SelectedText);
        Type(v, "9");
        Assert.Equal("9", box.Text);
    }

    [Fact]
    public void SelfDrawnNumberBox_CopyCutPasteUndoRedo()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var clipboard = new FakeD2DClipboard();
        using var host = NewHost(clipboard: clipboard);
        var v = host.View;
        var field = Field(v, "ToolbarAutoHideDelay");
        var box = field.Box;
        v.SetFocus(field);
        box.Text = "1500";
        v.KeyDown('A', true, false, false);
        v.KeyDown('C', true, false, false);
        Assert.Equal("1500", clipboard.Text);
        v.KeyDown('X', true, false, false);
        Assert.Equal(string.Empty, box.Text);
        clipboard.Text = "30x";
        v.KeyDown('V', true, false, false);
        Assert.Equal(string.Empty, box.Text); // dán có chữ: bị từ chối nguyên đoạn như ES_NUMBER
        clipboard.Text = "300";
        v.KeyDown('V', true, false, false);
        Assert.Equal("300", box.Text);
        v.KeyDown('Z', true, false, false);
        Assert.Equal(string.Empty, box.Text);
        v.KeyDown('Y', true, false, false);
        Assert.Equal("300", box.Text);
    }

    [Fact]
    public void SelfDrawnNumberBox_MouseClickPlacesCaret_DragSelects_DoubleClickSelectsAll()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var field = Field(v, "ToolbarAutoHideDelay");
        v.SetFocus(field);
        field.Box.Text = "123456";
        host.Render(read: false); // đo chữ + cuộn ngang
        var b = field.BoxBounds;
        var y = b.Y + (b.Height / 2) + v.Viewport.Y - v.OffsetPx;
        v.MouseDown(b.X + 6, y); // sát đầu chữ
        v.MouseUp(b.X + 6, y);
        Assert.Equal(0, field.Box.Caret);
        v.MouseDown(b.X + 6, y);
        v.MouseMove(b.X + 6 + (b.Width / 2), y, leftDown: true);
        v.MouseUp(b.X + 6 + (b.Width / 2), y);
        Assert.True(field.Box.HasSelection);
        Assert.Equal(0, field.Box.SelectionStart);
        v.MouseDown(b.X + 8, y, doubleClick: true);
        v.MouseUp(b.X + 8, y);
        Assert.Equal("123456", field.Box.SelectedText);
        Assert.Equal(D2DCursorKind.IBeam, v.CursorAt(b.X + 8, y));
    }

    [Fact]
    public void SelfDrawnFontSize_AllowsOneDecimalSeparator()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var field = Field(v, "InfoOverlayFontSize");
        v.SetFocus(field);
        field.Box.SelectAll();
        Type(v, "12.5.7");
        Assert.Equal("12.57", field.Box.Text);
    }

    // ---- c2: combobox tự vẽ -----------------------------------------------------------------------------------------------

    [Fact]
    public void SelfDrawnCombo_MouseOpenPickItem_KeyboardUpDownEscape()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var combo = (D2DComboWidget)v.Find("LoadingMode")!;
        var changed = new List<int>();
        v.Changed += w =>
        {
            if (w is D2DComboWidget c)
            {
                changed.Add(c.Selected);
            }
        };
        v.SetFocus(combo);
        host.Render(read: false);
        var start = combo.Selected;
        var (x, y) = CenterOf(v, combo.BoxBounds);
        v.MouseDown(x, y);
        v.MouseUp(x, y);
        Assert.True(v.PopupOpen);
        // Hàng thứ 3 của danh sách (chế độ "Original").
        var rowH = combo.BoxBounds.Height;
        var popup = v.PopupBounds!.Value;
        v.MouseDown(x, popup.Y + 1 + (2 * rowH) + (rowH / 2));
        Assert.False(v.PopupOpen);
        Assert.Equal(2, combo.Selected);
        Assert.Equal([2], changed);
        v.KeyDown(VkUp, false, false, false);
        Assert.Equal(1, combo.Selected);
        v.KeyDown(VkSpace, false, false, false);
        Assert.True(v.PopupOpen);
        v.KeyDown(VkDown, false, false, false);
        v.KeyDown(VkEscape, false, false, false);
        Assert.False(v.PopupOpen);
        Assert.Equal(1, combo.Selected); // Esc không đổi lựa chọn
        v.KeyDown(VkSpace, false, false, false);
        v.KeyDown(VkDown, false, false, false);
        v.KeyDown(VkReturn, false, false, false);
        Assert.Equal(2, combo.Selected);
        Assert.NotEqual(start, combo.Selected);
    }

    // ---- bàn phím -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Keyboard_TabOrderFollowsDeclarationAndSkipsDisabled_ShiftTabWraps()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var expected = v.AllWidgets().Where(w => w.Focusable).Select(w => w.Id).ToList();
        Assert.DoesNotContain("InfoOverlayAutoHideDelay", expected); // ô cha tắt
        Assert.DoesNotContain("ExifFileName", expected);
        Assert.Equal(["ShowInfoOverlay", "ShowFileInfo", "ShowFolderInfo", "InfoOverlayFontSize", "ShowZoomIndicator", "ToolbarAutoHide",
            "ToolbarAutoHideDelay", "ToolbarOpacity", "InfoOverlayAutoHide"], expected.Take(9).ToList());
        Assert.Equal(["ResetPage", "Cancel", "Apply", "Save"], expected.TakeLast(4).ToList());
        var visited = new List<string>();
        for (var i = 0; i < expected.Count; i++)
        {
            v.KeyDown(VkTab, false, false, false);
            visited.Add(v.Focused!.Id);
        }

        Assert.Equal(expected, visited);
        v.KeyDown(VkTab, false, false, false); // vòng lại
        Assert.Equal(expected[0], v.Focused!.Id);
        v.KeyDown(VkTab, false, true, false); // Shift+Tab về cuối
        Assert.Equal("Save", v.Focused!.Id);
    }

    [Fact]
    public void Keyboard_SpaceTogglesCheck_ArrowsMoveFocus_FocusScrollsIntoView()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var overlay = (D2DCheckWidget)v.Find("ShowInfoOverlay")!;
        var file = (D2DCheckWidget)v.Find("ShowFileInfo")!;
        v.SetFocus(overlay);
        Assert.True(overlay.Checked);
        v.KeyDown(VkSpace, false, false, false);
        Assert.False(overlay.Checked);
        Assert.False(file.Enabled); // phụ thuộc: ô cha tắt -> ô con vô hiệu
        v.KeyDown(VkDown, false, false, false);
        Assert.Equal("InfoOverlayFontSize", v.Focused!.Id); // hai ô con bị vô hiệu nên bỏ qua
    }

    [Fact]
    public void Keyboard_FocusMovingBelowTheFoldScrollsTheTargetIntoView()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost(new AppSettings { ShowExifInfo = true, ShowInfoOverlay = true });
        var v = host.View;
        var target = v.Find("Title" + PhotoReview.Core.Model.TitleBarFields.ShutterSpeed)!;
        Assert.True(target.Bounds.Bottom > v.Viewport.Height, "target starts below the fold");
        Assert.Equal(0, v.OffsetPx);
        v.SetFocus(target);
        Assert.True(v.OffsetPx > 0);
        var bottom = target.Bounds.Bottom - v.OffsetPx;
        Assert.InRange(bottom, 0, v.Viewport.Height);
    }

    [Fact]
    public void Slider_KeysMouseAndValueText()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var v = host.View;
        var slider = (D2DSliderWidget)v.Find("ToolbarOpacity")!;
        v.SetFocus(slider);
        v.KeyDown(VkEnd, false, false, false);
        Assert.Equal(slider.Max, slider.Value);
        v.KeyDown(VkLeft, false, false, false);
        Assert.Equal(slider.Max - 1, slider.Value);
        v.KeyDown(VkHome, false, false, false);
        Assert.Equal(slider.Min, slider.Value);
        host.Render(read: false);
        var (x, y) = CenterOf(v, slider.TrackBounds);
        v.MouseDown(x, y);
        v.MouseUp(x, y);
        Assert.InRange(slider.Value, slider.Min + ((slider.Max - slider.Min) * 4 / 10), slider.Min + ((slider.Max - slider.Min) * 6 / 10));
        Assert.Equal($"{slider.Value} %", slider.ValueText(slider.Value));
    }

    // ---- trợ năng + presenter ---------------------------------------------------------------------------------------------

    [Fact]
    public void AccessibleNodes_HaveNameRoleAndState_ForEveryControl()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var host = NewHost();
        var nodes = host.View.AccessibleNodes();
        Assert.All(nodes, n => Assert.False(string.IsNullOrWhiteSpace(n.Name), $"{n.AutomationId} has no name"));
        var overlay = nodes.Single(n => n.AutomationId == "ShowInfoOverlay");
        Assert.Equal(D2DRole.CheckBox, overlay.Role);
        Assert.True(overlay.IsChecked);
        Assert.Equal(Tr.SettingsShowInfoOverlayLabel, overlay.Name);
        var delay = nodes.Single(n => n.AutomationId == "ToolbarAutoHideDelay");
        Assert.Equal(D2DRole.Edit, delay.Role);
        Assert.Equal(host.View.Find("ToolbarAutoHideDelay") is D2DFieldWidget f ? f.Box.Text : null, delay.Value);
        Assert.Equal(D2DRole.ComboBox, nodes.Single(n => n.AutomationId == "LoadingMode").Role);
        Assert.Equal(D2DRole.Slider, nodes.Single(n => n.AutomationId == "ToolbarOpacity").Role);
        Assert.False(nodes.Single(n => n.AutomationId == "InfoOverlayAutoHideDelay").IsEnabled);
        Assert.Equal(D2DRole.Button, nodes.Single(n => n.AutomationId == "Save").Role);
        output.WriteLine($"{nodes.Count} accessible nodes; roles: " + string.Join(", ", nodes.GroupBy(n => n.Role).Select(g => $"{g.Key}={g.Count()}")));
    }

    [Fact]
    public void Presenter_RoundTrip_ValidationFocusAndSave_UseTheExistingSettingsLogic()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var dir = Path.Combine(Path.GetTempPath(), "d2d-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = SettingsUi.SettingsDialogKit.NewStore(dir);
            var settings = Baseline();
            var presenter = new SettingsPresenter(settings, store);
            var state = presenter.LoadFields();
            using var host = new D2DOffscreenHost(640, 560, 1.0, dark: true, hardware: false, settings);
            host.Page.Load(state);
            var round = new SettingsFormState();
            host.Page.Collect(round);
            Assert.Equal(state.ShowInfoOverlay, round.ShowInfoOverlay);
            Assert.Equal(state.InfoOverlayFontSizeText, round.InfoOverlayFontSizeText);
            Assert.Equal(state.ToolbarAutoHideDelayText, round.ToolbarAutoHideDelayText);
            Assert.Equal(state.ToolbarOpacityValue, round.ToolbarOpacityValue);
            Assert.Equal(state.LoadingModeIndex, round.LoadingModeIndex);
            Assert.Equal(state.ExifInfoFields, round.ExifInfoFields);
            Assert.Equal(state.TitleBarFields, round.TitleBarFields);

            // Người dùng gõ giá trị sai vào ô trễ -> presenter từ chối và chỉ ô cần focus.
            var v = host.View;
            var delay = Field(v, "InfoOverlayAutoHideDelay");
            ((D2DCheckWidget)v.Find("InfoOverlayAutoHide")!).Checked = true;
            host.Page.UpdateDependents();
            v.SetFocus(delay);
            delay.Box.Text = "9999999";
            host.Page.Collect(state);
            var refused = presenter.TrySave(state);
            Assert.Equal(SettingsSaveOutcome.Invalid, refused.Outcome);
            Assert.Equal(SettingsFocusField.InfoOverlayAutoHideDelay, refused.Focus);

            delay.Box.Text = "1500";
            ((D2DComboWidget)v.Find("LoadingMode")!).Selected = 1;
            ((D2DSliderWidget)v.Find("ToolbarOpacity")!).Value = 55;
            host.Page.Collect(state);
            var saved = presenter.TrySave(state);
            Assert.Equal(SettingsSaveOutcome.Saved, saved.Outcome);
            Assert.Equal(1500, presenter.Settings.InfoOverlayAutoHideDelayMs);
            Assert.Equal(55, presenter.Settings.ToolbarOpacityPercent);
            Assert.True(presenter.Settings.InfoOverlayAutoHide);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- c1: control Win32 thật đặt chồng ----------------------------------------------------------------------------------

    [Fact]
    public void Hosted_RealEditRejectsLettersViaES_NUMBER_AndFeedsTheView()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var options = new D2DSettingsWindowOptions(Dark: true, EditMode: D2DEditMode.HostedWin32);
        WithWindow(options, (window, page) =>
        {
            var edit = window.HostedHandles.First(h => h.Id == "ToolbarAutoHideDelay").Hwnd;
            var widget = Field(window.View, "ToolbarAutoHideDelay");
            Assert.True(User32.IsWindowVisible(edit));
            User32.SendMessage(edit, DialogConstantsForTest.EmSetSel, 0, -1);
            User32.SendMessage(edit, WindowMessages.WmChar, 'a', 1); // ES_NUMBER: chữ bị từ chối
            User32.SendMessage(edit, WindowMessages.WmChar, '8', 1);
            User32.SendMessage(edit, WindowMessages.WmChar, '0', 1);
            Pump();
            Assert.Equal("80", widget.Box.Text);
            var state = new SettingsFormState();
            page.Collect(state);
            Assert.Equal("80", state.ToolbarAutoHideDelayText);
            return 0;
        });
    }

    [Fact]
    public void Hosted_RealComboChangesSelection_ByKeyboardAndReachesTheForm()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var options = new D2DSettingsWindowOptions(Dark: true, EditMode: D2DEditMode.HostedWin32);
        WithWindow(options, (window, page) =>
        {
            var combo = window.HostedHandles.First(h => h.Id == "ScalingQuality").Hwnd;
            var widget = (D2DComboWidget)window.View.Find("ScalingQuality")!;
            var before = widget.Selected;
            User32.SetFocus(combo);
            User32.SendMessage(combo, 0x014E /* CB_SETCURSEL */, before == 0 ? 1 : 0, 0);
            // CBN_SELCHANGE như khi người dùng chọn: gửi tới cửa sổ chủ (WM_COMMAND, lParam = HWND combobox).
            var id = (nint)((1 << 16) | 0);
            User32.SendMessage(window.Hwnd, WindowMessages.WmCommand, id, combo);
            Pump();
            Assert.NotEqual(before, widget.Selected);
            var state = new SettingsFormState();
            page.Collect(state);
            Assert.Equal(widget.Selected, state.ScalingQualityIndex);
            return 0;
        });
    }

    [Fact]
    public void Hosted_TabFromViewWidgetMovesRealFocusIntoTheEdit_AndTabLeavesIt()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var options = new D2DSettingsWindowOptions(Dark: true, EditMode: D2DEditMode.HostedWin32);
        WithWindow(options, (window, page) =>
        {
            var v = window.View;
            var edit = window.HostedHandles.First(h => h.Id == "InfoOverlayFontSize").Hwnd;
            v.SetFocus(v.Find("ShowFolderInfo"));
            User32.SetFocus(window.Hwnd);
            User32.SendMessage(window.Hwnd, WindowMessages.WmKeyDown, VkTab, 1);
            Pump();
            Assert.Equal("InfoOverlayFontSize", v.Focused!.Id);
            Assert.Equal(edit, User32.GetFocus());
            // Tab được gõ BÊN TRONG ô thật: subclass chuyển cho view -> focus sang phần tử kế.
            User32.SendMessage(edit, WindowMessages.WmKeyDown, VkTab, 1);
            Pump();
            Assert.Equal("ShowZoomIndicator", v.Focused!.Id);
            Assert.Equal(window.Hwnd, User32.GetFocus());
            return 0;
        });
    }

    private static class DialogConstantsForTest
    {
        public const uint EmSetSel = 0x00B1;
    }
}
