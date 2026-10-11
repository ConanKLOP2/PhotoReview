using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>
/// WP-22: đường phím của shell (WM_KEYDOWN/WM_SYSKEYDOWN -> pan mũi tên -> Esc -> Space/Enter của nút -> ShortcutRouter -> lệnh)
/// với controller con trỏ, ViewportController, ShortcutRouter THẬT và ba handler thật, trên cửa sổ giả (không HWND). Khớp thứ tự
/// của <c>MainWindow.Window_KeyDown</c>.
/// </summary>
public sealed class KeyboardInputTests
{
    private static int Vk(KeyId key) => KeyIdMapping.ToVirtualKey(key);

    // ---- ánh xạ phím -> lệnh theo cài đặt (ShortcutKeyName + KeyIdMapping) ----

    [Theory]
    [InlineData(KeyId.Right, ReviewCommandType.Next)]
    [InlineData(KeyId.Left, ReviewCommandType.Previous)]
    [InlineData(KeyId.Delete, ReviewCommandType.Recycle)]
    [InlineData(KeyId.Space, ReviewCommandType.Skip)]
    [InlineData(KeyId.F11, ReviewCommandType.Fullscreen)]
    [InlineData(KeyId.C, ReviewCommandType.ToggleCompare)]
    [InlineData(KeyId.Home, ReviewCommandType.FirstImage)]
    [InlineData(KeyId.End, ReviewCommandType.LastImage)]
    [InlineData(KeyId.PageDown, ReviewCommandType.NextFolder)]
    [InlineData(KeyId.PageUp, ReviewCommandType.PreviousFolder)]
    [InlineData(KeyId.I, ReviewCommandType.ToggleInfoOverlay)]
    [InlineData(KeyId.K, ReviewCommandType.ToggleKeepZoom)]
    [InlineData(KeyId.D3, ReviewCommandType.CustomZoom)]
    [InlineData(KeyId.R, ReviewCommandType.Refresh)]
    public void DefaultShortcuts_AreResolvedFromTheVirtualKey_AndReachTheHost(KeyId key, ReviewCommandType expected)
    {
        using var rig = new InputRig();
        rig.Host.CurrentHasComparePair = true;

        var (handled, _) = rig.KeyDown(Vk(key));

        Assert.True(handled);
        Assert.Equal([new ReviewCommand(expected)], rig.Host.Commands);
    }

    [Fact]
    public void ViewportCommands_RunOnTheControllerNotTheHost()
    {
        using var rig = new InputRig();
        rig.Fit();
        Assert.True(rig.Viewer.IsFit);

        rig.KeyDown(Vk(KeyId.D1)); // ZoomActualSize
        Assert.False(rig.Viewer.IsFit);
        Assert.Equal(1.0, rig.Viewer.Zoom, 9);
        rig.KeyDown(Vk(KeyId.F)); // ToggleFit
        rig.Drain();

        Assert.True(rig.Viewer.IsFit);
        Assert.Empty(rig.Host.Commands);
    }

    [Fact]
    public void CustomShortcutFromSettings_IsHonoured_AndRebuildSwapsTheTable()
    {
        using var rig = new InputRig(configure: s => s.Shortcuts.Skip = "J");
        Assert.True(rig.KeyDown(Vk(KeyId.J)).Handled);
        Assert.Equal([ReviewCommand.Skip], rig.Host.Commands);
        rig.Host.Commands.Clear();
        Assert.False(rig.KeyDown(Vk(KeyId.Space)).Handled); // Space không còn là Skip

        rig.Settings.Shortcuts.Skip = "Q";
        rig.Input.RebuildShortcuts(rig.Settings);
        Assert.True(rig.KeyDown(Vk(KeyId.Q)).Handled);
        Assert.False(rig.KeyDown(Vk(KeyId.J)).Handled);
        Assert.Equal([ReviewCommand.Skip], rig.Host.Commands);
    }

    [Fact]
    public void ActionProfileShortcut_RunsThatProfileIndex()
    {
        using var rig = new InputRig();
        var (index, key) = rig.Settings.Actions.Select((a, i) => (i, a.Shortcut)).First(t => !string.IsNullOrEmpty(t.Shortcut));
        Assert.True(ShortcutKeyNameParse(key, out var keyId));

        rig.KeyDown(Vk(keyId));

        Assert.Equal([ReviewCommand.Action(index)], rig.Host.Commands);
    }

    private static bool ShortcutKeyNameParse(string name, out KeyId key) => PhotoReview.App.Services.ShortcutKeyName.TryParse(name, out key);

    [Fact]
    public void UnboundKey_IsNotHandled_SoWindowsKeepsIt()
    {
        using var rig = new InputRig();
        var (handled, _) = rig.KeyDown(Vk(KeyId.F9));
        Assert.False(handled);
        Assert.Empty(rig.Host.Commands);
    }

    [Fact]
    public void NoImages_StillResolvesCommands_ButTheRouterDecides()
    {
        using var rig = new InputRig();
        rig.Host.HasImages = false;
        // Fullscreen/OpenFolder/Close không cần ảnh; Next thì router trả null khi không có ảnh.
        Assert.True(rig.KeyDown(Vk(KeyId.F11)).Handled);
        Assert.False(rig.KeyDown(Vk(KeyId.Right)).Handled);
        Assert.Equal([ReviewCommand.Fullscreen], rig.Host.Commands);
    }

    // ---- modifier ----

    [Fact]
    public void Modifiers_AreReadFromTheEnvironment_ShiftForcesThePicker_CtrlRequiredForUndo()
    {
        using var rig = new InputRig();

        rig.Modifiers = KeyModifiers.Shift;
        rig.KeyDown(Vk(KeyId.M));
        rig.Modifiers = KeyModifiers.None;
        rig.KeyDown(Vk(KeyId.M));
        rig.KeyDown(Vk(KeyId.Z)); // Undo cần Ctrl
        rig.Modifiers = KeyModifiers.Control;
        rig.KeyDown(Vk(KeyId.Z));

        Assert.Equal([ReviewCommand.MoveToFolder(true), ReviewCommand.MoveToFolder(false), ReviewCommand.Undo], rig.Host.Commands);
    }

    // ---- auto-repeat ----

    [Fact]
    public void AutoRepeat_IsDroppedForFileActions_ButKeptForNavigationAndZoom()
    {
        using var rig = new InputRig();

        var recycle = rig.KeyDown(Vk(KeyId.Delete), repeat: true);
        var next = rig.KeyDown(Vk(KeyId.Right), repeat: true);
        var fullscreen = rig.KeyDown(Vk(KeyId.F11), repeat: true);
        var info = rig.KeyDown(Vk(KeyId.I), repeat: true);

        Assert.True(recycle.Handled); // phím vẫn bị nuốt
        Assert.True(next.Handled);
        Assert.True(fullscreen.Handled);
        Assert.True(info.Handled);
        Assert.Equal([ReviewCommand.Next], rig.Host.Commands);
    }

    [Fact]
    public void FirstPress_OfAFileAction_Runs()
    {
        using var rig = new InputRig();
        rig.KeyDown(Vk(KeyId.Delete));
        rig.KeyDown(Vk(KeyId.Delete), repeat: true);
        Assert.Equal([ReviewCommand.Recycle], rig.Host.Commands);
    }

    // ---- Esc ----

    [Fact]
    public void Escape_ExitsFullscreen_ElseClosesTheWindow()
    {
        using var rig = new InputRig();
        rig.KeyDown(Vk(KeyId.Escape));
        rig.Host.IsFullscreen = true;
        rig.KeyDown(Vk(KeyId.Escape));

        Assert.Equal([ReviewCommand.Close, ReviewCommand.ExitFullscreen], rig.Host.Commands);
    }

    [Fact]
    public void Escape_ClosesTheToolsPopupFirst_ThenCancelsADuplicateCheck_BeforeAnythingElse()
    {
        using var rig = new InputRig();
        var popup = true;
        rig.Input.EscapeInterceptor = () =>
        {
            var was = popup;
            popup = false;
            return was;
        };
        rig.Host.DuplicateCheckRunning = true;
        rig.Host.IsFullscreen = true;

        Assert.True(rig.KeyDown(Vk(KeyId.Escape)).Handled); // đóng popup, không đụng duplicate check
        Assert.True(rig.Host.DuplicateCheckRunning);
        Assert.True(rig.KeyDown(Vk(KeyId.Escape)).Handled); // huỷ băm
        Assert.False(rig.Host.DuplicateCheckRunning);
        Assert.Empty(rig.Host.Commands);
        Assert.True(rig.KeyDown(Vk(KeyId.Escape)).Handled); // thoát fullscreen
        Assert.Equal([ReviewCommand.ExitFullscreen], rig.Host.Commands);
    }

    [Fact]
    public void EscapeInterceptors_AreNotConsultedForOtherKeys()
    {
        using var rig = new InputRig();
        var calls = 0;
        rig.Input.EscapeInterceptor = () =>
        {
            calls++;
            return true;
        };
        rig.Host.DuplicateCheckRunning = true;

        rig.KeyDown(Vk(KeyId.Right));

        Assert.Equal(0, calls);
        Assert.True(rig.Host.DuplicateCheckRunning);
        Assert.Equal([ReviewCommand.Next], rig.Host.Commands);
    }

    // ---- focus: Space/Enter thuộc nút ----

    [Theory]
    [InlineData(KeyId.Space)]
    [InlineData(KeyId.Enter)]
    public void SpaceAndEnter_BelongToAFocusedButton_ButOtherKeysStillWork(KeyId key)
    {
        using var rig = new InputRig();
        rig.Input.ActivationKeyOwned = () => true;

        Assert.False(rig.KeyDown(Vk(key)).Handled);
        Assert.True(rig.KeyDown(Vk(KeyId.Right)).Handled);
        rig.Input.ActivationKeyOwned = () => false;
        rig.KeyDown(Vk(KeyId.Space));

        Assert.Equal([ReviewCommand.Next, ReviewCommand.Skip], rig.Host.Commands);
    }

    // ---- phím hệ thống ----

    [Fact]
    public void SysKeyDown_ResolvesThroughTheSystemKey_AndAltF4IsLeftToWindows()
    {
        using var rig = new InputRig(configure: s => s.Shortcuts.Fullscreen = "F10");
        rig.Modifiers = KeyModifiers.Alt;

        Assert.False(rig.KeyDown(Vk(KeyId.F4), system: true).Handled); // Alt+F4: DefWindowProc đóng cửa sổ
        Assert.True(rig.KeyDown(Vk(KeyId.F10), system: true).Handled);
        Assert.Equal([ReviewCommand.Fullscreen], rig.Host.Commands);
    }

    [Fact]
    public void SysKeyDown_WithAnArrow_DoesNotPan()
    {
        using var rig = new InputRig();
        rig.KeyDown(Vk(KeyId.D1));
        rig.Modifiers = KeyModifiers.Alt;
        var before = rig.Viewport.HorizontalOffset;

        rig.KeyDown(Vk(KeyId.Right), system: true);

        Assert.Equal(before, rig.Viewport.HorizontalOffset);
    }

    [Fact]
    public void KeyUpAndChar_OfAHandledKeyAreSwallowed_ButNotOfAnUnhandledOne()
    {
        using var rig = new InputRig();

        rig.KeyDown(Vk(KeyId.F9)); // không xử lý
        Assert.False(rig.Send(WindowMessages.WmChar, 'x', 0).Handled);
        Assert.False(rig.Send(WindowMessages.WmKeyUp, Vk(KeyId.F9), 0).Handled);

        rig.KeyDown(Vk(KeyId.Right));
        Assert.True(rig.Send(WindowMessages.WmChar, 'x', 0).Handled);
        Assert.True(rig.Send(WindowMessages.WmSysChar, 'x', 0).Handled);
        Assert.True(rig.Send(WindowMessages.WmKeyUp, Vk(KeyId.Right), 0).Handled);
        Assert.False(rig.Send(WindowMessages.WmKeyUp, Vk(KeyId.Right), 0).Handled); // chỉ một lần
        Assert.False(rig.Send(WindowMessages.WmKeyUp, Vk(KeyId.Left), 0).Handled);
    }

    [Theory]
    [InlineData(0x10, 0x2A, 0xA0)] // Shift trái
    [InlineData(0x10, 0x36, 0xA1)] // Shift phải (scan code 0x36)
    [InlineData(0x11, 0x36, 0x11)] // không phải Shift: giữ nguyên
    [InlineData(0x41, 0x1E, 0x41)]
    public void ShiftSide_IsTakenFromTheScanCode(int virtualKey, int scanCode, int expected) =>
        Assert.Equal(expected, KeyboardMessageHandler.ResolveVirtualKey(virtualKey, (nint)((long)scanCode << 16)));

    [Fact]
    public void RightShift_MapsToTheRightShiftKeyId()
    {
        using var rig = new InputRig(configure: s => s.Shortcuts.Skip = "RightShift");
        // KeyId.RightShift không đặt được làm phím tắt (ShortcutKeyName chặn): bảng rỗng, phím không được xử lý.
        Assert.False(rig.KeyDown(0x10, scanCode: 0x36).Handled);
        Assert.Empty(rig.Host.Commands);
    }

    // ---- pan bằng mũi tên ----

    [Theory]
    [InlineData(10, 59.0)]    // 10 % x min(990, 590)
    [InlineData(25, 147.5)]
    [InlineData(100, 590.0)]
    public void ArrowPan_StepIsAPercentOfTheShorterViewportSide_SameOnBothAxes(int percent, double step)
    {
        using var rig = new InputRig(configure: s =>
        {
            s.ArrowPanStepPercent = percent;
            s.KineticPanEnabled = false;
        });
        rig.KeyDown(Vk(KeyId.D1));
        rig.Drain();
        Assert.Equal((990.0, 590.0), (rig.Viewport.ViewportWidth, rig.Viewport.ViewportHeight));

        var (x0, y0) = (rig.Viewport.HorizontalOffset, rig.Viewport.VerticalOffset);

        Assert.True(rig.KeyDown(Vk(KeyId.Right)).Handled);
        Assert.Equal(x0 + step, rig.Viewport.HorizontalOffset, 6);
        Assert.True(rig.KeyDown(Vk(KeyId.Down)).Handled);
        Assert.Equal(y0 + step, rig.Viewport.VerticalOffset, 6);
        Assert.True(rig.KeyDown(Vk(KeyId.Left)).Handled);
        Assert.Equal(x0, rig.Viewport.HorizontalOffset, 6);
        Assert.True(rig.KeyDown(Vk(KeyId.Up)).Handled);
        Assert.Equal(y0, rig.Viewport.VerticalOffset, 6);
        Assert.Empty(rig.Host.Commands);
    }

    [Fact]
    public void ArrowKeys_AtFit_NavigateInsteadOfPanning()
    {
        using var rig = new InputRig();
        rig.Fit();
        rig.KeyDown(Vk(KeyId.Right));
        rig.KeyDown(Vk(KeyId.Left));
        Assert.Equal([ReviewCommand.Next, ReviewCommand.Previous], rig.Host.Commands);
    }

    [Fact]
    public void ArrowKey_WithAModifier_OrWhileComparing_DoesNotPan()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = false);
        rig.KeyDown(Vk(KeyId.D1));
        rig.Drain();
        var x0 = rig.Viewport.HorizontalOffset;

        rig.Modifiers = KeyModifiers.Control;
        rig.KeyDown(Vk(KeyId.Right));
        rig.Modifiers = KeyModifiers.None;
        rig.Host.IsCompareVisible = true;
        rig.KeyDown(Vk(KeyId.Right));

        Assert.Equal(x0, rig.Viewport.HorizontalOffset);
    }

    [Fact]
    public void KineticArrowPan_StartsAGlide_AndAnyOtherKeyStopsIt()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = true);
        rig.KeyDown(Vk(KeyId.D1));
        rig.Drain();
        var x0 = rig.Viewport.HorizontalOffset;

        rig.KeyDown(Vk(KeyId.Right));
        Assert.Equal(1, rig.Clock.SubscriberCount); // một hook khung của đà

        rig.Timestamp = 0;
        rig.Clock.Tick(0);
        rig.Timestamp += 160_000;
        rig.Clock.Tick(16);
        rig.Drain();
        Assert.True(rig.Viewport.HorizontalOffset > x0);

        rig.KeyDown(Vk(KeyId.F9)); // phím bất kỳ dừng đà
        Assert.Equal(0, rig.Clock.SubscriberCount);
    }

    [Fact]
    public void ZoomChange_AndDeactivation_StopTheGlide()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = true);
        rig.KeyDown(Vk(KeyId.D1));
        rig.Drain();
        rig.KeyDown(Vk(KeyId.Right));
        Assert.Equal(1, rig.Clock.SubscriberCount);

        rig.Window.RaiseDeactivated();
        Assert.Equal(0, rig.Clock.SubscriberCount);

        rig.KeyDown(Vk(KeyId.Right));
        Assert.Equal(1, rig.Clock.SubscriberCount);
        rig.Viewer.SetZoom(2.0);
        Assert.Equal(0, rig.Clock.SubscriberCount);
    }

    // ---- lỗi lệnh ----

    [Fact]
    public void ACommandThatThrows_IsReportedNotCrashed()
    {
        using var rig = new InputRig();
        rig.Host.ExecuteFailure = new InvalidOperationException("boom");

        var (handled, _) = rig.KeyDown(Vk(KeyId.Delete));

        Assert.True(handled);
        Assert.Equal(["failed:Recycle:boom"], rig.Host.Log);
    }

    [Fact]
    public void ACommandThatIsCancelled_IsNotAFailure()
    {
        using var rig = new InputRig();
        rig.Host.ExecuteFailure = new OperationCanceledException();
        rig.KeyDown(Vk(KeyId.Delete));
        Assert.Empty(rig.Host.Log);
    }
}
