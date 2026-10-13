using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1: hộp thoại zoom tuỳ chọn Win32 gốc (parity với <c>ClickZoomCustomDialogTests</c> của bản WPF). Cửa sổ THẬT trên thread STA
/// riêng; người dùng được mô phỏng bằng SendMessage/IsDialogMessage đồng bộ trong callback <c>ready</c>.
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class ZoomPromptDialogTests
{
    private static string InvalidMessage => Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);

    private static (int Result, ZoomPromptDialog Dialog) Show(int current, Action<ZoomPromptDialog, nint> ready, INativeDialogChrome? chrome = null)
        => OnSta(() =>
        {
            var dialog = new ZoomPromptDialog(current, chrome);
            var result = ShowOnOwner(dialog, (d, owner) => ready((ZoomPromptDialog)d, owner));
            return (result, dialog);
        });

    [Fact]
    public void Opens_WithCurrentValueSelected_FocusOnTheBox_OwnerDisabledWhileOpen_AndReEnabledAfter()
    {
        nint ownerAfter = 0;
        var (result, dialog) = OnSta(() =>
        {
            var owner = CreateOwner();
            try
            {
                var d = new ZoomPromptDialog(175);
                var r = d.ShowModal(owner, shown =>
                {
                    Assert.False(User32.IsWindowEnabled(owner), "modal: owner must be disabled while the dialog is open");
                    Assert.True(User32.IsWindowVisible(shown.Hwnd));
                    Assert.Equal("175", TextOf(shown, ZoomPromptDialog.EditId));
                    Assert.Equal(Item(shown, ZoomPromptDialog.EditId), User32.GetFocus());
                    Assert.Equal(Tr.DialogClickZoomCustomTitle, TextOf(shown.Hwnd));
                    Assert.Equal(Tr.DialogClickZoomCustomLabel, TextOf(shown, ZoomPromptDialog.LabelId));
                    Assert.Equal(Tr.CommonApply, TextOf(shown, ZoomPromptDialog.ApplyId));
                    Assert.Equal(Tr.CommonCancel, TextOf(shown, ZoomPromptDialog.CancelId));
                    Assert.False(IsVisible(shown, ZoomPromptDialog.ErrorId));
                    var selection = new int[2];
                    unsafe
                    {
                        fixed (int* start = &selection[0])
                        fixed (int* end = &selection[1])
                        {
                            User32.SendMessage(Item(shown, ZoomPromptDialog.EditId), DialogConstants.EmGetSel, (nint)start, (nint)end);
                        }
                    }

                    Assert.Equal([0, 3], selection);
                    shown.Close();
                });
                Assert.True(User32.IsWindowEnabled(owner), "owner must be enabled again once the dialog closed");
                Assert.Equal(0, d.Hwnd); // window destroyed
                ownerAfter = owner;
                return (r, d);
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Null(dialog.Value);
        Assert.NotEqual(0, ownerAfter);
    }

    [Fact]
    public void Apply_WithAValidValue_ReturnsOkAndTheValue()
    {
        var (result, dialog) = Show(100, (d, _) =>
        {
            SetText(d, ZoomPromptDialog.EditId, "250");
            Command(d, ZoomPromptDialog.ApplyId);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(250, dialog.Value);
        Assert.Null(dialog.ErrorMessage);
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("800", true)]
    [InlineData(" 120 ", true)]
    [InlineData("9", false)]
    [InlineData("801", false)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("12.5", false)]
    public void Apply_ValidatesLikeTheSettingsBox_BoundariesAndGarbage(string text, bool accepted)
    {
        var (result, dialog) = Show(100, (d, _) =>
        {
            SetText(d, ZoomPromptDialog.EditId, text);
            Command(d, ZoomPromptDialog.ApplyId);
            if (!d.IsClosed)
            {
                Command(d, ZoomPromptDialog.CancelId);
            }
        });

        if (accepted)
        {
            Assert.Equal(NativeDialog.IdOk, result);
            Assert.Equal(int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture), dialog.Value);
        }
        else
        {
            Assert.Equal(NativeDialog.IdCancel, result);
            Assert.Null(dialog.Value);
            Assert.Equal(InvalidMessage, dialog.ErrorMessage);
        }
    }

    [Fact]
    public void InvalidValue_ShowsTheMessageUnderTheBox_KeepsTheDialogOpen_GrowsIt_AndRefocusesTheBox()
    {
        var (result, dialog) = Show(100, (d, _) =>
        {
            var before = Height(ClientRect(d.Hwnd));
            SetText(d, ZoomPromptDialog.EditId, "9999");
            Command(d, ZoomPromptDialog.ApplyId);

            Assert.False(d.IsClosed, "an invalid value must keep the dialog open");
            Assert.True(IsVisible(d, ZoomPromptDialog.ErrorId));
            Assert.Equal(InvalidMessage, TextOf(d, ZoomPromptDialog.ErrorId));
            Assert.True(Height(ClientRect(d.Hwnd)) > before, "the dialog must grow to show the message");
            Assert.Equal(Item(d, ZoomPromptDialog.EditId), User32.GetFocus());
            // Nothing is clipped: the message fits its own box and lies inside the client area.
            var error = ChildRect(d, ZoomPromptDialog.ErrorId);
            var client = ClientRect(d.Hwnd);
            Assert.True(error.Bottom <= client.Bottom && error.Right <= client.Right);
            Assert.True(NeededTextHeight(Item(d, ZoomPromptDialog.ErrorId)) <= Height(error));

            SetText(d, ZoomPromptDialog.EditId, "200");
            Command(d, ZoomPromptDialog.ApplyId);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(200, dialog.Value);
    }

    [Fact]
    public void Enter_InTheBox_Applies_AndEscape_Cancels_ThroughTheDialogManager()
    {
        var (enter, enterDialog) = Show(100, (d, _) =>
        {
            SetText(d, ZoomPromptDialog.EditId, "300");
            Assert.True(Key(d, Item(d, ZoomPromptDialog.EditId), VkReturn));
            Drain(d);
        });
        var (escape, escapeDialog) = Show(100, (d, _) =>
        {
            SetText(d, ZoomPromptDialog.EditId, "300");
            Assert.True(Key(d, Item(d, ZoomPromptDialog.EditId), VkEscape));
            Drain(d);
        });

        Assert.Equal(NativeDialog.IdOk, enter);
        Assert.Equal(300, enterDialog.Value);
        Assert.Equal(NativeDialog.IdCancel, escape);
        Assert.Null(escapeDialog.Value);
    }

    [Theory]
    [InlineData("wm_close")]
    [InlineData("alt_f4")]
    [InlineData("cancel_button")]
    [InlineData("outside_close")]
    public void EveryWayToCloseWithoutApplying_ReturnsCancel(string how)
    {
        var (result, dialog) = Show(100, (d, _) =>
        {
            SetText(d, ZoomPromptDialog.EditId, "400"); // a pending valid value must not leak out
            switch (how)
            {
                case "wm_close":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                case "alt_f4":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmSysCommand, (nint)ScClose, 0); // what Alt+F4 / the X button send
                    break;
                case "cancel_button":
                    Command(d, ZoomPromptDialog.CancelId);
                    break;
                default:
                    d.Close();
                    break;
            }

            Assert.True(d.IsClosed);
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Null(dialog.Value);
    }

    [Fact]
    public void WmQuit_ClosesTheDialogAsCancel_AndIsReposted_NotSwallowed()
    {
        var (result, dialog) = OnSta(() =>
        {
            var d = new ZoomPromptDialog(100);
            var r = ShowOnOwner(d, (_, _) => User32.PostQuitMessage(7));
            Assert.True(HasQuitPending(out var code), "the nested loop must repost WM_QUIT for the outer loop");
            Assert.Equal(7, code);
            return (r, d);
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Null(dialog.Value);
    }

    [Fact]
    public void WithoutAnOwner_OrADeadOwner_StillOpensAndCloses()
    {
        var (noOwner, _) = OnSta(() =>
        {
            var d = new ZoomPromptDialog(100);
            return (d.ShowModal(0, shown =>
            {
                Assert.True(User32.IsWindowVisible(shown.Hwnd));
                Command(shown, ZoomPromptDialog.ApplyId);
            }), d);
        });
        var (deadOwner, _) = OnSta(() =>
        {
            var owner = CreateOwner();
            DestroyOwner(owner);
            var d = new ZoomPromptDialog(100);
            return (d.ShowModal(owner, shown => Command(shown, ZoomPromptDialog.CancelId)), d);
        });

        Assert.Equal(NativeDialog.IdOk, noOwner);
        Assert.Equal(NativeDialog.IdCancel, deadOwner);
    }

    [Fact]
    public void ADialogInstance_CanBeShownOnlyOnce()
    {
        OnSta(() =>
        {
            var d = new ZoomPromptDialog(100);
            d.ShowModal(0, shown => shown.Close());
            Assert.Throws<InvalidOperationException>(() => d.ShowModal(0));
            return 0;
        });
    }

    [Fact]
    public void Dialog_IsCenteredOnTheOwner_AndInsideTheWorkArea()
    {
        var offset = OnSta(() =>
        {
            var d = new ZoomPromptDialog(100);
            var owner = CreateOwner();
            try
            {
                var o = WindowRect(owner);
                var delta = (0, 0);
                d.ShowModal(owner, shown =>
                {
                    var w = WindowRect(shown.Hwnd);
                    delta = ((w.Left + w.Right) / 2 - (o.Left + o.Right) / 2, (w.Top + w.Bottom) / 2 - (o.Top + o.Bottom) / 2);
                    shown.Close();
                });
                return delta;
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.InRange(offset.Item1, -2, 2);
        Assert.InRange(offset.Item2, -2, 2);
    }

    [Fact]
    public void Chrome_IsAppliedOnce_ToTheDialogAndEveryControl_WithTheirClasses()
    {
        var chrome = new RecordingChrome();

        var (_, dialog) = Show(100, (d, _) => d.Close(), chrome);

        var call = Assert.Single(chrome.Calls);
        Assert.NotEqual(0, call.Dialog);
        Assert.Equal(
            [DialogControlClass.Static, DialogControlClass.Edit, DialogControlClass.Static, DialogControlClass.Button, DialogControlClass.Button],
            call.Controls.Select(c => c.Class));
        Assert.All(call.Controls, c => Assert.NotEqual(0, c.Hwnd));
        Assert.NotNull(dialog);
    }

    [Fact]
    public void DarkColors_AreReturnedForDialogStaticsEditsAndTheErrorLine()
    {
        OnSta(() =>
        {
            ShowOnOwner(new ZoomPromptDialog(100), (d, _) =>
            {
                unsafe
                {
                    var hdc = User32.GetDC(d.Hwnd);
                    try
                    {
                        var dlgBrush = User32.SendMessage(d.Hwnd, DialogConstants.WmCtlColorDlg, hdc, d.Hwnd);
                        Assert.NotEqual(0, dlgBrush);
                        Assert.Equal(DialogColors.Window, BrushColor(dlgBrush));

                        var staticBrush = User32.SendMessage(d.Hwnd, DialogConstants.WmCtlColorStatic, hdc, Item(d, ZoomPromptDialog.LabelId));
                        Assert.Equal(DialogColors.Window, BrushColor(staticBrush));
                        Assert.Equal(DialogColors.Text, Gdi32.GetTextColor(hdc));
                        Assert.Equal(DialogColors.Window, Gdi32.GetBkColor(hdc));

                        User32.SendMessage(d.Hwnd, DialogConstants.WmCtlColorStatic, hdc, Item(d, ZoomPromptDialog.ErrorId));
                        Assert.Equal(DialogColors.Warning, Gdi32.GetTextColor(hdc));

                        var editBrush = User32.SendMessage(d.Hwnd, DialogConstants.WmCtlColorEdit, hdc, Item(d, ZoomPromptDialog.EditId));
                        Assert.Equal(DialogColors.Input, BrushColor(editBrush));
                        Assert.Equal(DialogColors.TextStrong, Gdi32.GetTextColor(hdc));
                        Assert.Equal(DialogColors.Input, Gdi32.GetBkColor(hdc));
                    }
                    finally
                    {
                        _ = User32.ReleaseDC(d.Hwnd, hdc);
                    }
                }

                d.Close();
            });
            return 0;
        });
    }

    private static unsafe uint BrushColor(nint brush)
    {
        byte* logBrush = stackalloc byte[16];
        _ = Gdi32.GetObject(brush, 16, logBrush);
        return *(uint*)(logBrush + 4); // LOGBRUSH.lbColor
    }

    [Fact]
    public void DpiChange_RescalesFontAndLayout_AndBackAgain()
    {
        OnSta(() =>
        {
            ShowOnOwner(new ZoomPromptDialog(100), (d, _) =>
            {
                var dpi = d.Dpi;
                var font = FontHeight(d.BodyFont);
                var edit = ChildRect(d, ZoomPromptDialog.EditId);
                var client = ClientRect(d.Hwnd);
                var window = WindowRect(d.Hwnd);

                // The system moved the dialog to a monitor at 150 %: suggested rectangle = current size x 1.5.
                var bigDpi = dpi * 3 / 2;
                ChangeDpi(d, bigDpi, window, 3, 2);

                Assert.Equal(bigDpi, d.Dpi);
                Assert.InRange(Math.Abs(FontHeight(d.BodyFont)) * 1.0 / Math.Abs(font), 1.4, 1.6);
                var editBig = ChildRect(d, ZoomPromptDialog.EditId);
                Assert.InRange(Width(editBig) * 1.0 / Width(edit), 1.4, 1.6);
                Assert.InRange(Width(ClientRect(d.Hwnd)) * 1.0 / Width(client), 1.4, 1.6);
                Assert.True(Width(ChildRect(d, ZoomPromptDialog.ApplyId)) > 0 && ChildRect(d, ZoomPromptDialog.ApplyId).Right <= Width(ClientRect(d.Hwnd)));

                ChangeDpi(d, dpi, WindowRect(d.Hwnd), 2, 3);
                Assert.Equal(dpi, d.Dpi);
                Assert.InRange(Math.Abs(FontHeight(d.BodyFont) - font), 0, 1);
                Assert.InRange(Math.Abs(Width(ChildRect(d, ZoomPromptDialog.EditId)) - Width(edit)), 0, 2);
                d.Close();
            });
            return 0;
        });
    }

    private static unsafe void ChangeDpi(NativeDialog dialog, uint dpi, Rect current, int numerator, int denominator)
    {
        var suggested = new Rect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Left + Width(current) * numerator / denominator,
            Bottom = current.Top + Height(current) * numerator / denominator,
        };
        User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
    }

    [Fact]
    public void TabOrder_IsBoxThenCancelThenApply_AndApplyIsTheDefaultButton()
    {
        OnSta(() =>
        {
            ShowOnOwner(new ZoomPromptDialog(100), (d, _) =>
            {
                var edit = Item(d, ZoomPromptDialog.EditId);
                var cancel = Item(d, ZoomPromptDialog.CancelId);
                var apply = Item(d, ZoomPromptDialog.ApplyId);
                Assert.Equal(edit, User32.GetFocus());
                Assert.Equal(WindowMessages.IdOk, (int)(User32.SendMessage(d.Hwnd, DialogConstants.DmGetDefId, 0, 0) & 0xFFFF));
                Assert.NotEqual(0, (int)(User32.GetWindowLongPtr(apply, WindowMessages.GwlStyle) & DialogConstants.BsDefPushButton));
                // Z-order == creation order == Tab order.
                const uint GwHwndNext = 2;
                var order = new List<nint>();
                for (var h = User32.GetWindow(d.Hwnd, 5 /* GW_CHILD */); h != 0; h = User32.GetWindow(h, GwHwndNext))
                {
                    order.Add(h);
                }

                Assert.True(order.IndexOf(edit) < order.IndexOf(cancel) && order.IndexOf(cancel) < order.IndexOf(apply));
                d.Close();
            });
            return 0;
        });
    }

    [Fact]
    public void VietnameseStrings_AreNotClipped_AndTheButtonsFit()
    {
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        OnSta(() =>
        {
            ShowOnOwner(new ZoomPromptDialog(100), (d, _) =>
            {
                var client = ClientRect(d.Hwnd);
                foreach (var id in new[] { ZoomPromptDialog.LabelId, ZoomPromptDialog.EditId, ZoomPromptDialog.CancelId, ZoomPromptDialog.ApplyId })
                {
                    var rect = ChildRect(d, id);
                    Assert.True(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= client.Right && rect.Bottom <= client.Bottom, $"control {id} is clipped");
                }

                Assert.True(NeededTextHeight(Item(d, ZoomPromptDialog.LabelId)) <= Height(ChildRect(d, ZoomPromptDialog.LabelId)));
                // Buttons do not overlap.
                Assert.True(ChildRect(d, ZoomPromptDialog.CancelId).Right <= ChildRect(d, ZoomPromptDialog.ApplyId).Left);
                SetText(d, ZoomPromptDialog.EditId, "x");
                Command(d, ZoomPromptDialog.ApplyId);
                var error = ChildRect(d, ZoomPromptDialog.ErrorId);
                Assert.True(NeededTextHeight(Item(d, ZoomPromptDialog.ErrorId)) <= Height(error));
                Assert.True(error.Bottom <= ClientRect(d.Hwnd).Bottom);
                Assert.True(ChildRect(d, ZoomPromptDialog.ApplyId).Top >= error.Bottom);
                d.Close();
            });
            return 0;
        });
    }

    [Fact]
    public void RepeatedOpenAndClose_LeaksNoGdiOrUserObjects()
    {
        OnSta(() =>
        {
            // Warm-up: one-off process caches (uxtheme dark mode, fonts) must not count as a leak.
            ShowOnOwner(new ZoomPromptDialog(100), (d, _) => Command(d, ZoomPromptDialog.ApplyId));
            var gdi = GdiObjects();
            var user = UserObjects();

            for (var i = 0; i < 25; i++)
            {
                ShowOnOwner(new ZoomPromptDialog(100), (d, _) =>
                {
                    SetText(d, ZoomPromptDialog.EditId, i % 2 == 0 ? "bad" : "150");
                    Command(d, ZoomPromptDialog.ApplyId); // error path (bigger window) then close either way
                    d.Close();
                });
            }

            Assert.True(GdiObjects() <= gdi, "GDI objects leaked");
            Assert.True(UserObjects() <= user, "USER objects leaked");
            return 0;
        });
    }

    [Fact]
    public void AnExceptionInTheDialogCallback_ReachesTheCaller_AfterTheWindowAndOwnerState_AreRestored()
    {
        OnSta(() =>
        {
            var owner = CreateOwner();
            try
            {
                var d = new ThrowingDialog();
                Assert.Throws<InvalidOperationException>(() => d.ShowModal(owner, shown => Command(shown, NativeDialog.IdOk)));
                Assert.True(User32.IsWindowEnabled(owner), "owner must be re-enabled after a failing callback");
                Assert.Equal(0, d.Hwnd);
            }
            finally
            {
                DestroyOwner(owner);
            }

            return 0;
        });
    }

    private sealed class ThrowingDialog : NativeDialog
    {
        protected override string Title => "boom";

        protected override IReadOnlyList<DialogItem> Items { get; } =
        [
            new(DialogControlClass.Button, IdOk, "OK", DialogStyles.Child | DialogConstants.BsDefPushButton),
        ];

        protected override (int Width, int Height) PreferredClientSize() => (200, 100);

        protected override void LayoutControls(int clientWidth, int clientHeight) => Place(IdOk, 10, 10, 80, 24);

        protected override bool OnOk() => throw new InvalidOperationException("boom");
    }
}
