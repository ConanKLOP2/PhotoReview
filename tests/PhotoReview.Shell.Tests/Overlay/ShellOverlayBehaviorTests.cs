using PhotoReview.App.Input;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-23: hành vi của bộ overlay HUD (đồng hồ khung + Stopwatch giả: 1 tick = 1 ms, không phụ thuộc màn hình/thời gian thật):
/// auto-hide của toolbar và của overlay thông tin theo đúng <c>MainWindow.xaml.cs</c> (fade vào 150 ms / ra 200 ms, hot zone
/// 24 DIP, độ trễ theo cài đặt, click xuyên khi ẩn), hover/nhấn/click, bàn phím, menu Tools, tooltip, hiện/ẩn theo model.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ShellOverlayBehaviorTests
{
    private static OverlayRig NewRig(Action<AppSettings>? tweak = null, bool images = true)
    {
        var rig = new OverlayRig();
        tweak?.Invoke(rig.Settings);
        if (images)
        {
            rig.Update(m => m with { HasImages = true });
        }

        return rig;
    }

    // Toolbar auto-hide ------------------------------------------------------------------------------------------------

    [Fact]
    public void Toolbar_StartsVisibleAndClickable()
    {
        using OverlayRig rig = NewRig();
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Fact]
    public void Toolbar_HidesExactlyAfterTheConfiguredDelay_ThenFadesOutOver200Ms()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);

        rig.Advance(1499);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);

        rig.Advance(1); // t = 1500: hết giờ -> bắt đầu fade, click xuyên ngay
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
        Assert.True(rig.Overlay.Animator.IsAnimating);

        rig.Advance(1); // khung đầu của tween: opacity = giá trị đầu
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity, 0.001);
        rig.Advance(100);
        Assert.Equal(0.5f, rig.Overlay.Toolbar.Opacity, 0.02);
        rig.Advance(100);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);
        Assert.False(rig.Overlay.Animator.IsAnimating);
    }

    [Fact]
    public void Toolbar_FadeIn_Takes150Ms_WhenTheMouseReturnsToTheHotZone()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);

        rig.MoveTo(new PointD(20, 20));
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
        rig.Advance(1); // khung đầu
        rig.Advance(75);
        Assert.Equal(0.5f, rig.Overlay.Toolbar.Opacity, 0.02);
        rig.Advance(75);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Toolbar_HotZone_IsTheToolbarBoxInflatedBy24OnEverySide()
    {
        using OverlayRig rig = NewRig();
        RectD box = rig.ToolbarBounds;
        double right = box.X + box.Width + ShellOverlay.HotZoneMargin;
        double bottom = box.Y + box.Height + ShellOverlay.HotZoneMargin;

        rig.MoveTo(new PointD(right, bottom)); // cạnh: tính là trong
        rig.Advance(5000);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);

        rig.MoveTo(new PointD(right + 0.5, 20)); // ngoài
        rig.Advance(1500 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);

        rig.MoveTo(new PointD(20, bottom + 0.5)); // ngoài theo chiều dọc: vẫn ẩn
        rig.Advance(300);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);

        rig.MoveTo(new PointD(box.X - ShellOverlay.HotZoneMargin, box.Y - ShellOverlay.HotZoneMargin)); // góc trên-trái: trong
        rig.Advance(151);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Toolbar_CountdownDoesNotRestartOnLaterMovesOutsideTheHotZone()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1000);
        rig.MoveTo(new PointD(700, 500)); // vẫn ngoài hot zone
        rig.Advance(499);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
        rig.Advance(1);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(0, 1)] // WPF: Math.Max(1, delay)
    [InlineData(99999, 10000)] // kẹp về MaxToolbarAutoHideDelayMs
    public void Toolbar_DelayComesFromSettingsClampedToItsRange(int configured, int effective)
    {
        using OverlayRig rig = NewRig(s => s.ToolbarAutoHideDelayMs = configured);
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(effective - 1);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
        rig.Advance(1);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Fact]
    public void Toolbar_NeverHidesWhenAutoHideIsOff()
    {
        using OverlayRig rig = NewRig(s => s.ToolbarAutoHide = false);
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(20_000);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Toolbar_StaysVisibleWithoutAFolder()
    {
        using OverlayRig rig = NewRig(images: false);
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(20_000);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Toolbar_ClosingTheFolderBringsItBack()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);

        rig.Update(m => m with { HasImages = false });
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Toolbar_VisibleOpacityFollowsTheSettingAndNeverGoesBelowTheMinimum()
    {
        using OverlayRig rig = NewRig(s => s.ToolbarOpacityPercent = 60);
        rig.Advance(1 + 150);
        Assert.Equal(0.6f, rig.Overlay.Toolbar.Opacity, 0.001);

        rig.Settings.ToolbarOpacityPercent = 5; // kẹp về 20 %
        rig.Overlay.SettingsChanged();
        rig.Advance(1 + 150);
        Assert.Equal(0.2f, rig.Overlay.Toolbar.Opacity, 0.001);
    }

    [Fact]
    public void Toolbar_SettingsChangeToAutoHideOffBringsItBackAtOnce()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500 + 1 + 200);
        rig.Settings.ToolbarAutoHide = false;
        rig.Overlay.SettingsChanged();
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Fact]
    public void Toolbar_DoesNotHideWhileTheToolsMenuIsOpen_AndCountsDownAfterItCloses()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1000);
        rig.Menus!.WhileOpen = () =>
        {
            Assert.True(rig.Overlay.IsToolsMenuOpen);
            Assert.True(rig.Overlay.Toolbar.Tools.IsChecked);
        };
        rig.Menus.Choose = null;

        ClickButton(rig, rig.Overlay.Toolbar.Tools); // menu modal: Show chạy đồng bộ rồi trả null
        Assert.False(rig.Overlay.IsToolsMenuOpen);
        Assert.False(rig.Overlay.Toolbar.Tools.IsChecked);

        rig.Advance(1499);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible); // đếm lại từ lúc menu đóng
        rig.Advance(1);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    // Overlay thông tin ------------------------------------------------------------------------------------------------

    [Fact]
    public void InfoOverlay_HidesAfterTheIdleDelayAndBecomesClickThrough()
    {
        using OverlayRig rig = NewRig();
        rig.Advance(2999);
        Assert.True(rig.Overlay.InfoLayer.IsHitTestVisible);
        rig.Advance(1);
        Assert.False(rig.Overlay.InfoLayer.IsHitTestVisible);
        rig.Advance(1 + 200);
        Assert.Equal(0f, rig.Overlay.InfoLayer.Opacity);
    }

    [Fact]
    public void InfoOverlay_ActivityRestartsTheCountdown()
    {
        using OverlayRig rig = NewRig();
        rig.Advance(2000);
        rig.MoveTo(OverlayRig.FarAway); // hoạt động
        rig.Advance(2999);
        Assert.True(rig.Overlay.InfoLayer.IsHitTestVisible);
        rig.Advance(1);
        Assert.False(rig.Overlay.InfoLayer.IsHitTestVisible);
    }

    [Fact]
    public void InfoOverlay_NavigationAndKeysAreActivity_AndTheyBringItBack()
    {
        using OverlayRig rig = NewRig();
        rig.Advance(3000 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.InfoLayer.Opacity);

        rig.Overlay.NoteActivity(); // đổi ảnh
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);

        rig.Advance(3000 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.InfoLayer.Opacity);
        Assert.False(rig.Overlay.KeyDown(0x41, shift: false, isAutoRepeat: false)); // phím thường: ảnh xử lý, overlay chỉ ghi nhận
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);
    }

    [Fact]
    public void InfoOverlay_AttentionMessageKeepsItVisible_AndTheDelayStartsWhenTheMessageEnds()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { StatusNeedsAttention = true, StatusText = "Đang tải..." });
        rig.Advance(10_000);
        Assert.True(rig.Overlay.InfoLayer.IsHitTestVisible);

        rig.Update(m => m with { StatusNeedsAttention = false, StatusText = string.Empty });
        rig.Advance(2999);
        Assert.True(rig.Overlay.InfoLayer.IsHitTestVisible); // đếm từ lúc thông điệp hết, không từ hoạt động cuối
        rig.Advance(1);
        Assert.False(rig.Overlay.InfoLayer.IsHitTestVisible);
    }

    [Fact]
    public void InfoOverlay_CompareAndInactiveWindowAndNoFolderForceItVisible()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { CompareVisible = true });
        rig.Advance(10_000);
        Assert.True(rig.Overlay.InfoLayer.IsHitTestVisible);
        rig.Update(m => m with { CompareVisible = false });
        rig.Advance(3000 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.InfoLayer.Opacity);

        rig.WindowActive = false; // hộp thoại/ứng dụng khác giữ focus
        rig.Overlay.WindowDeactivated();
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);
        rig.Advance(20_000);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);

        rig.WindowActive = true;
        rig.Overlay.WindowActivated();
        rig.Advance(3000 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.InfoLayer.Opacity);

        rig.Update(m => m with { HasImages = false });
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);
    }

    [Fact]
    public void InfoOverlay_AutoHideOffNeverHides_AndTheToolbarDelayIsIndependent()
    {
        using OverlayRig rig = NewRig(s => s.InfoOverlayAutoHide = false);
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(20_000);
        Assert.Equal(1f, rig.Overlay.InfoLayer.Opacity);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible); // toolbar vẫn ẩn theo độ trễ riêng
    }

    // Hover / nhấn / click ---------------------------------------------------------------------------------------------

    private static void ClickButton(OverlayRig rig, ButtonElement button)
    {
        PointD p = OverlayRig.Center(button);
        Assert.True(rig.Overlay.PointerDown(p));
        Assert.True(rig.Overlay.PointerUp(p));
    }

    [Fact]
    public void Buttons_ClickInvokesTheMatchingCommand()
    {
        using OverlayRig rig = NewRig();
        ClickButton(rig, rig.Overlay.Toolbar.OpenFolder);
        ClickButton(rig, rig.Overlay.Toolbar.Settings);
        ClickButton(rig, rig.Overlay.Toolbar.Fit);
        Assert.Equal(["OpenFolder", "Settings", "Fit"], rig.Commands.Calls);
    }

    [Fact]
    public void Buttons_ReleasingOutsideTheButtonDoesNotClick()
    {
        using OverlayRig rig = NewRig();
        PointD down = OverlayRig.Center(rig.Overlay.Toolbar.Settings);
        Assert.True(rig.Overlay.PointerDown(down));
        Assert.True(rig.Overlay.Toolbar.Settings.IsPressed);
        Assert.True(rig.Overlay.PointerUp(OverlayRig.Center(rig.Overlay.Toolbar.Fit)));
        Assert.False(rig.Overlay.Toolbar.Settings.IsPressed);
        Assert.Empty(rig.Commands.Calls);
    }

    [Fact]
    public void Buttons_HoverFollowsThePointer_AndLeavingClearsIt()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.Center(rig.Overlay.Toolbar.Fit));
        Assert.True(rig.Overlay.Toolbar.Fit.IsHot);
        Assert.False(rig.Overlay.Toolbar.Settings.IsHot);

        rig.MoveTo(OverlayRig.Center(rig.Overlay.Toolbar.Settings));
        Assert.False(rig.Overlay.Toolbar.Fit.IsHot);
        Assert.True(rig.Overlay.Toolbar.Settings.IsHot);

        rig.Overlay.PointerLeft();
        Assert.False(rig.Overlay.Toolbar.Settings.IsHot);
    }

    [Fact]
    public void ClickThrough_HiddenToolbarAndHiddenInfoLetTheImageHaveTheClick()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { StatusPanelVisible = true, FileInfoVisible = true, StatusText = "a.jpg" });
        rig.Overlay.Arrange(new SizeD(1000, 700), 1);
        PointD toolbarPoint = OverlayRig.Center(rig.Overlay.Toolbar.Fit);
        PointD statusPoint = OverlayRig.Center(rig.Overlay.Status);
        Assert.True(rig.Overlay.HitTest(toolbarPoint));
        Assert.True(rig.Overlay.HitTest(statusPoint));

        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(4000);
        Assert.False(rig.Overlay.HitTest(toolbarPoint));
        Assert.False(rig.Overlay.HitTest(statusPoint));
        Assert.False(rig.Overlay.PointerDown(toolbarPoint));
        Assert.False(rig.Overlay.Wheel(toolbarPoint));
        Assert.Empty(rig.Commands.Calls);
    }

    [Fact]
    public void Panels_AbsorbTheClickButEmptySpaceDoesNot()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { ZoomIndicatorVisible = true, ZoomIndicatorText = "100%" });
        rig.Overlay.Arrange(new SizeD(1000, 700), 1);
        PointD zoom = OverlayRig.Center(rig.Overlay.ZoomIndicator);
        Assert.True(rig.Overlay.PointerDown(zoom));
        Assert.True(rig.Overlay.IsPointerDown);
        Assert.Null(rig.Overlay.PressedButton);
        Assert.True(rig.Overlay.PointerUp(zoom)); // nhả cũng bị nuốt
        Assert.False(rig.Overlay.IsPointerDown);

        Assert.False(rig.Overlay.PointerDown(new PointD(500, 350)));
        Assert.False(rig.Overlay.PointerUp(new PointD(500, 350)));
    }

    [Fact]
    public void PointerCancel_DropsThePressWithoutClicking()
    {
        using OverlayRig rig = NewRig();
        PointD p = OverlayRig.Center(rig.Overlay.Toolbar.Fit);
        Assert.True(rig.Overlay.PointerDown(p));
        rig.Overlay.PointerCancel();
        Assert.False(rig.Overlay.IsPointerDown);
        Assert.False(rig.Overlay.Toolbar.Fit.IsPressed);
        Assert.False(rig.Overlay.PointerUp(p));
        Assert.Empty(rig.Commands.Calls);
    }

    [Fact]
    public void FailedAsyncCommand_IsReportedWithItsName()
    {
        using OverlayRig rig = NewRig();
        rig.Commands.Fit = () => Task.FromException(new InvalidOperationException("boom"));
        ClickButton(rig, rig.Overlay.Toolbar.Fit);
        Assert.Single(rig.Failures); // task đã hoàn thành: continuation chạy đồng bộ
        Assert.Equal("boom", rig.Failures[0].Exception.Message);
        Assert.Equal(rig.Overlay.Toolbar.Fit.Content, rig.Failures[0].Name);
    }

    [Fact]
    public void CancelledCommand_IsNotAFailure()
    {
        using OverlayRig rig = NewRig();
        rig.Commands.OpenFolder = () => Task.FromCanceled(new CancellationToken(true));
        ClickButton(rig, rig.Overlay.Toolbar.OpenFolder);
        Assert.Empty(rig.Failures);
    }

    [Fact]
    public void SynchronouslyThrowingCommand_IsReportedToo()
    {
        using OverlayRig rig = NewRig();
        rig.Commands.OpenFolder = () => throw new InvalidOperationException("sync");
        ClickButton(rig, rig.Overlay.Toolbar.OpenFolder);
        Assert.Single(rig.Failures);
    }

    // Bàn phím ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Keyboard_TabWalksTheButtonsAndLeavesAfterTheLast()
    {
        using OverlayRig rig = NewRig();
        const int tab = 0x09;
        Assert.False(rig.Overlay.HasKeyboardFocus);
        Assert.True(rig.Overlay.KeyDown(tab, false, false));
        Assert.Same(rig.Overlay.Toolbar.OpenFolder, rig.Overlay.Toolbar.FocusedButton);
        Assert.True(rig.Overlay.Toolbar.OpenFolder.IsFocused);
        rig.Overlay.KeyDown(tab, false, false);
        rig.Overlay.KeyDown(tab, false, false);
        rig.Overlay.KeyDown(tab, false, false);
        Assert.Same(rig.Overlay.Toolbar.Tools, rig.Overlay.Toolbar.FocusedButton);
        rig.Overlay.KeyDown(tab, false, false);
        Assert.False(rig.Overlay.HasKeyboardFocus);
        Assert.All(rig.Overlay.Toolbar.Buttons, b => Assert.False(b.IsFocused));
    }

    [Fact]
    public void Keyboard_ShiftTabStartsFromTheLastButton()
    {
        using OverlayRig rig = NewRig();
        rig.Overlay.KeyDown(0x09, shift: true, isAutoRepeat: false);
        Assert.Same(rig.Overlay.Toolbar.Tools, rig.Overlay.Toolbar.FocusedButton);
        rig.Overlay.KeyDown(0x09, shift: true, isAutoRepeat: false);
        Assert.Same(rig.Overlay.Toolbar.Fit, rig.Overlay.Toolbar.FocusedButton);
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x0D)]
    public void Keyboard_SpaceAndEnterActivateTheFocusedButton_AutoRepeatIgnored(int key)
    {
        using OverlayRig rig = NewRig();
        rig.Overlay.KeyDown(0x09, false, false);
        rig.Overlay.KeyDown(0x09, false, false); // Settings
        Assert.True(rig.Overlay.KeyDown(key, false, isAutoRepeat: false));
        Assert.True(rig.Overlay.KeyDown(key, false, isAutoRepeat: true));
        Assert.Equal(["Settings"], rig.Commands.Calls);
    }

    [Fact]
    public void Keyboard_SpaceWithoutFocusIsLeftToTheImage()
    {
        using OverlayRig rig = NewRig();
        Assert.False(rig.Overlay.KeyDown(0x20, false, false));
        Assert.False(rig.Overlay.KeyDown(0x0D, false, false));
        Assert.Empty(rig.Commands.Calls);
    }

    [Fact]
    public void Keyboard_FocusInsideKeepsAHiddenToolbarVisible_AndBringsItBack()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500 + 1 + 200);
        Assert.Equal(0f, rig.Overlay.Toolbar.Opacity);

        rig.Overlay.KeyDown(0x09, false, false); // WPF: Tab vào toolbar ẩn -> GotKeyboardFocus -> hiện
        rig.Advance(1 + 150);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
        rig.Advance(20_000);
        Assert.Equal(1f, rig.Overlay.Toolbar.Opacity);
    }

    [Fact]
    public void Keyboard_ClickingTheImageTakesTheFocusBack()
    {
        using OverlayRig rig = NewRig();
        rig.Overlay.KeyDown(0x09, false, false);
        Assert.True(rig.Overlay.HasKeyboardFocus);
        Assert.False(rig.Overlay.PointerDown(new PointD(500, 350)));
        Assert.False(rig.Overlay.HasKeyboardFocus);
    }

    // Menu Tools ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToolsButton_ShowsTheToolsMenuBelowTheButton_InScreenDips()
    {
        using OverlayRig rig = NewRig();
        rig.Menus!.Choose = null;
        ClickButton(rig, rig.Overlay.Toolbar.Tools);

        Assert.Equal(1, rig.Menus.ShowCount);
        RectD bar = rig.Overlay.Toolbar.Tools.Bounds;
        Assert.Equal(rig.ClientOrigin.X + bar.X, rig.Menus.LastPoint.X, 3);
        Assert.Equal(rig.ClientOrigin.Y + bar.Y + bar.Height, rig.Menus.LastPoint.Y, 3);
        Assert.Equal(6, rig.Menus.LastItems!.Count);
        Assert.Empty(rig.Commands.Calls);
    }

    [Theory]
    [InlineData("Tools.Recovery", "Recovery")]
    [InlineData("Tools.Diagnostics", "Diagnostics")]
    [InlineData("Tools.Benchmark", "Benchmark")]
    [InlineData("Tools.ClearCache", "ClearCache")]
    [InlineData("Tools.RemoveNumberedDuplicates", "RemoveNumbered")]
    [InlineData("Tools.RemoveOriginalDuplicates", "RemoveOriginal")]
    public void ToolsMenu_ChosenItemRunsItsCommand(string id, string expected)
    {
        using OverlayRig rig = NewRig();
        rig.Menus!.Choose = id;
        ClickButton(rig, rig.Overlay.Toolbar.Tools);
        Assert.Equal([expected], rig.Commands.Calls);
    }

    [Fact]
    public void ToolsMenu_DuplicateRemovalIsDisabledAndNoOpWithoutImages()
    {
        using OverlayRig rig = NewRig(images: false);
        rig.Menus!.Choose = "Tools.RemoveNumberedDuplicates";
        ClickButton(rig, rig.Overlay.Toolbar.Tools);
        Assert.Empty(rig.Commands.Calls);
        Assert.Contains(rig.Menus.LastItems!, i => i.Id == "Tools.RemoveNumberedDuplicates" && !i.IsEnabled);
        Assert.Contains(rig.Menus.LastItems!, i => i.Id == "Tools.Recovery" && i.IsEnabled);
    }

    [Fact]
    public void ToolsMenu_WithoutAHostTheButtonDoesNothing()
    {
        using var rig = new OverlayRig(menus: false);
        rig.Update(m => m with { HasImages = true });
        ClickButton(rig, rig.Overlay.Toolbar.Tools);
        Assert.False(rig.Overlay.IsToolsMenuOpen);
        Assert.Empty(rig.Commands.Calls);
    }

    [Fact]
    public void ToolsMenu_ReentrantClickIsIgnoredWhileOpen()
    {
        using OverlayRig rig = NewRig();
        rig.Menus!.WhileOpen = () => ClickButton(rig, rig.Overlay.Toolbar.Tools);
        ClickButton(rig, rig.Overlay.Toolbar.Tools);
        Assert.Equal(1, rig.Menus.ShowCount);
    }

    // Tooltip -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tooltips_ToolbarButtonsGetTheirLocalizedTextAtTheirBoxes()
    {
        using OverlayRig rig = NewRig();
        rig.Overlay.Render(new RecordingDrawContext(), new SizeD(1000, 700), 1.5);

        FakeTooltips tips = rig.Tooltips!;
        Assert.Equal(1.5, tips.Dpi);
        Assert.Equal(["OpenFolderButton", "SettingsButton", "FitButton", "ToolsButton"], tips.Tools.Select(t => t.Id));
        foreach (ButtonElement button in rig.Overlay.Toolbar.Buttons)
        {
            OverlayTooltip tool = tips.Tools.Single(t => t.Id == button.Id);
            Assert.Equal(button.ToolTip, tool.Text);
            Assert.Equal(button.Bounds, tool.Bounds);
            Assert.NotEqual(string.Empty, tool.Text);
        }
    }

    [Fact]
    public void Tooltips_HiddenOverlaysHaveNone_ExifAndBadgeComeFromTheModel()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with
        {
            StatusPanelVisible = true,
            ExifVisible = true,
            ExifText = "Canon  85 mm",
            CapturePairVisible = true,
            CapturePairText = "JPEG+RAW",
            CapturePairToolTip = "Nhấn F8",
        });
        rig.Overlay.Render(new RecordingDrawContext(), new SizeD(1000, 700), 1);
        Assert.Contains(rig.Tooltips!.Tools, t => t.Id == "ExifInfoText" && t.Text == "Canon  85 mm");
        Assert.Contains(rig.Tooltips.Tools, t => t.Id == "CapturePairBadge" && t.Text == "Nhấn F8");

        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(4000);
        rig.Overlay.Render(new RecordingDrawContext(), new SizeD(1000, 700), 1);
        Assert.Empty(rig.Tooltips.Tools);
    }

    // Hiện/ẩn và dữ liệu --------------------------------------------------------------------------------------------------

    [Fact]
    public void Model_DrivesVisibilityTextAndFontOfEveryPanel()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with
        {
            StatusPanelVisible = true,
            FileInfoVisible = true,
            StatusText = "IMG_1.JPG",
            FontSize = 14,
            ExifFontSize = 13,
            ExifVisible = true,
            ExifText = "ISO 100",
            FolderInfoVisible = true,
            FolderInfoText = "📁 Trip",
            ZoomIndicatorVisible = true,
            ZoomIndicatorText = "150%",
            CapturePairVisible = true,
            CapturePairText = "JPEG+RAW",
        });

        Assert.True(rig.Overlay.Status.IsVisible);
        Assert.Equal("IMG_1.JPG", rig.Overlay.Status.Status.Text);
        Assert.Equal(14, rig.Overlay.Status.Status.Style.FontSizeDip);
        Assert.Equal(13, rig.Overlay.Status.Exif.Style.FontSizeDip);
        Assert.Equal("ISO 100", rig.Overlay.Status.Exif.Text);
        Assert.Equal("📁 Trip", rig.Overlay.FolderInfo.Text.Text);
        Assert.Equal("150%", rig.Overlay.ZoomIndicator.Text.Text);
        Assert.Equal("JPEG+RAW", rig.Overlay.CapturePair.Text.Text);
        Assert.True(rig.Overlay.CapturePair.IsVisible);

        rig.Update(m => m with { StatusPanelVisible = false, FolderInfoVisible = false, ZoomIndicatorVisible = false, CapturePairVisible = false });
        Assert.False(rig.Overlay.Status.IsVisible);
        Assert.False(rig.Overlay.FolderInfo.IsVisible);
        Assert.False(rig.Overlay.ZoomIndicator.IsVisible);
        Assert.False(rig.Overlay.CapturePair.IsVisible);
    }

    [Fact]
    public void StatusPanel_SkippedRowAndWarningFollowTheModel_ViewListButtonClicks()
    {
        using OverlayRig rig = NewRig();
        Assert.False(rig.Overlay.Status.SkippedRow.IsVisible);
        rig.Update(m => m with { StatusPanelVisible = true, HasSkippedEntries = true, SkippedWarningText = "Bỏ qua 3 tệp" });
        rig.Overlay.Arrange(new SizeD(1000, 700), 1);
        Assert.True(rig.Overlay.Status.SkippedRow.IsVisible);
        Assert.Equal("Bỏ qua 3 tệp", rig.Overlay.Status.SkippedWarning.Text);
        ClickButton(rig, rig.Overlay.Status.ViewList);
        Assert.Equal(["Skipped"], rig.Commands.Calls);
    }

    [Fact]
    public void Render_DrawsInfoLayerBeforeTheToolbar_SoTheToolbarIsOnTop()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { ZoomIndicatorVisible = true, ZoomIndicatorText = "100%" });
        var dc = new RecordingDrawContext();
        rig.Overlay.Render(dc, new SizeD(1000, 700), 1);

        int zoom = dc.Texts.FindIndex(t => t.Text == "100%");
        int fit = dc.Texts.FindIndex(t => t.Text == rig.Overlay.Toolbar.Fit.Content);
        Assert.True(zoom >= 0 && fit > zoom);
        Assert.Equal(0, dc.OpacityDepth);
        Assert.Equal(0, dc.ClipDepth);
    }

    [Fact]
    public void Render_FadedOutToolbarIsNotDrawn()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500 + 1 + 200);
        var dc = new RecordingDrawContext();
        rig.Overlay.Render(dc, new SizeD(1000, 700), 1);
        Assert.DoesNotContain(dc.Texts, t => t.Text == rig.Overlay.Toolbar.Fit.Content);
    }

    [Fact]
    public void Dispose_StopsTimersAndAnimationsAndReleasesTextLayouts()
    {
        var text = new FakeTextRenderer();
        var rig = new OverlayRig(text);
        rig.Update(m => m with { HasImages = true });
        rig.Overlay.Render(new RecordingDrawContext(), new SizeD(1000, 700), 1);
        Assert.True(text.Live > 0);
        rig.MoveTo(OverlayRig.FarAway);
        Assert.True(rig.Clock.SubscriberCount > 0);

        rig.Dispose();
        Assert.Equal(0, rig.Clock.SubscriberCount);
        Assert.Equal(0, text.Live);
        Assert.True(rig.Tooltips!.Disposed);
        rig.Advance(5000); // không còn gì nghe đồng hồ
        rig.Dispose(); // idempotent
    }

    [Fact]
    public void AfterDispose_InputAndUpdatesAreIgnored()
    {
        var rig = new OverlayRig();
        rig.Update(m => m with { HasImages = true });
        PointD p = OverlayRig.Center(rig.Overlay.Toolbar.Fit);
        rig.Dispose();
        Assert.False(rig.Overlay.PointerDown(p));
        Assert.False(rig.Overlay.KeyDown(0x09, false, false));
        rig.Overlay.Update(new OverlayModel());
        rig.Overlay.SettingsChanged();
        Assert.Empty(rig.Commands.Calls);
    }
}
