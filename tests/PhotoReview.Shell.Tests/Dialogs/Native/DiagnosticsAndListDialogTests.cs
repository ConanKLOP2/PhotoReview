using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1: Diagnostics (15 cặp nhãn/giá trị + Sao chép), SkippedFiles và BatchReview Win32 gốc. Parity với
/// <c>DiagnosticsPresenter</c>/<c>SkippedFilesPresenter</c>/<c>BatchReviewPresenter</c> (cùng văn bản) và với các cửa sổ WPF
/// (nút nào đóng/xác nhận, Enter/Esc, kéo giãn).
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class DiagnosticsAndListDialogTests
{
    private static DiagnosticsDisplay SampleDisplay() => DiagnosticsPresenter.Build(new ReviewMetrics().Snapshot());

    private static DiagnosticsDisplay LongDisplay()
    {
        var longText = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"IMG_{i:0000}.jpg x{i * 7}"));
        return SampleDisplay() with { SourceOpens = longText, PresentHistogram = longText, ExplorerOrder = longText };
    }

    // ---- Diagnostics -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnostics_ShowsTheFifteenLabelValuePairs_InTheOrderOfTheWpfWindow()
    {
        var display = SampleDisplay();
        OnSta(() =>
        {
            ShowOnOwner(new DiagnosticsDialog(display), (d, _) =>
            {
                Assert.Equal(Tr.DiagnosticsTitle, TextOf(d.Hwnd));
                Assert.Equal(Tr.DiagnosticsHeader, TextOf(d, DiagnosticsDialog.HeaderId));
                Assert.Equal(Tr.DiagnosticsPrivacyNote, TextOf(d, DiagnosticsDialog.NoteId));
                for (var row = 0; row < DiagnosticsDialog.RowCount; row++)
                {
                    Assert.Equal(DiagnosticsDialog.LabelText(row), TextOf(d, DiagnosticsDialog.FirstLabelId + row));
                    Assert.Equal(DiagnosticsDialog.ValueText(display, row), TextOf(d, DiagnosticsDialog.FirstValueId + row));
                }

                // Spot-check the pairing against the presenter (cache hits / hit rate / cross-thread presents).
                Assert.Equal(Tr.DiagnosticsLabelCacheHits, TextOf(d, DiagnosticsDialog.FirstLabelId));
                Assert.Equal(display.CacheHits, TextOf(d, DiagnosticsDialog.FirstValueId));
                Assert.Equal(Tr.DiagnosticsLabelCacheHitRate, TextOf(d, DiagnosticsDialog.FirstLabelId + 2));
                Assert.Equal(display.HitRate, TextOf(d, DiagnosticsDialog.FirstValueId + 2));
                Assert.Equal(Tr.DiagnosticsLabelCrossThreadPresents, TextOf(d, DiagnosticsDialog.FirstLabelId + 14));
                Assert.Equal(display.CrossThreadPresents, TextOf(d, DiagnosticsDialog.FirstValueId + 14));
                d.Close();
            });
            return 0;
        });
    }

    [Theory]
    [InlineData("close_button")]
    [InlineData("escape")]
    [InlineData("enter")]
    [InlineData("wm_close")]
    [InlineData("alt_f4")]
    public void Diagnostics_EveryCloseRoute_ClosesAndReturnsCancel(string how)
    {
        var result = OnSta(() => ShowOnOwner(new DiagnosticsDialog(SampleDisplay()), (d, _) =>
        {
            switch (how)
            {
                case "close_button":
                    Command(d, DiagnosticsDialog.CloseId);
                    break;
                case "escape":
                    Assert.True(Key(d, Item(d, DiagnosticsDialog.CloseId), VkEscape));
                    Drain(d);
                    break;
                case "enter":
                    Assert.True(Key(d, Item(d, DiagnosticsDialog.CloseId), VkReturn));
                    Drain(d);
                    break;
                case "wm_close":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                default:
                    User32.SendMessage(d.Hwnd, WindowMessages.WmSysCommand, (nint)ScClose, 0);
                    break;
            }

            Assert.True(d.IsClosed);
        }));

        Assert.Equal(NativeDialog.IdCancel, result);
    }

    [Fact]
    public void Diagnostics_CopyButton_PutsTheWholeTableOnTheClipboard_AndKeepsTheWindowOpen()
    {
        var display = SampleDisplay();
        var copied = new List<string>();
        var (count, closed) = OnSta(() =>
        {
            var dialog = new DiagnosticsDialog(display, text =>
            {
                copied.Add(text);
                return true;
            });
            var stillOpen = false;
            ShowOnOwner(dialog, (d, _) =>
            {
                Command(d, DiagnosticsDialog.CopyId);
                stillOpen = !d.IsClosed;
                d.Close();
            });
            return (dialog.CopyCount, !stillOpen);
        });

        Assert.False(closed);
        Assert.Equal(1, count);
        var text = Assert.Single(copied);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(DiagnosticsDialog.RowCount, lines.Length);
        Assert.Equal(DiagnosticsDialog.LabelText(0) + "\t" + display.CacheHits, lines[0]);
        Assert.Equal(DiagnosticsDialog.LabelText(14) + "\t" + display.CrossThreadPresents, lines[14]);
    }

    [Fact]
    public void Diagnostics_LongVietnameseValues_WrapInsteadOfBeingClipped_AndNothingOverlaps()
    {
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        OnSta(() =>
        {
            ShowOnOwner(new DiagnosticsDialog(LongDisplay()), (d, _) =>
            {
                var client = ClientRect(d.Hwnd);
                var previousBottom = 0;
                for (var row = 0; row < DiagnosticsDialog.RowCount; row++)
                {
                    var label = ChildRect(d, DiagnosticsDialog.FirstLabelId + row);
                    var value = ChildRect(d, DiagnosticsDialog.FirstValueId + row);
                    Assert.True(NeededTextHeight(Item(d, DiagnosticsDialog.FirstValueId + row)) <= Height(value), $"row {row} value clipped");
                    Assert.True(NeededTextHeight(Item(d, DiagnosticsDialog.FirstLabelId + row)) <= Height(label), $"row {row} label clipped");
                    Assert.True(label.Top >= previousBottom, $"row {row} overlaps the previous row");
                    Assert.True(value.Right <= client.Right && value.Bottom <= client.Bottom);
                    Assert.True(label.Right <= value.Left);
                    previousBottom = Math.Max(label.Bottom, value.Bottom);
                }

                var close = ChildRect(d, DiagnosticsDialog.CloseId);
                Assert.True(close.Top >= previousBottom && close.Bottom <= client.Bottom && close.Right <= client.Right);
                // The long rows made the window taller than the plain one.
                d.Close();
            });
            return 0;
        });
    }

    // ---- SkippedFiles / BatchReview ----------------------------------------------------------------------------------------

    private static readonly SkippedEntry[] Skipped =
    [
        new(@"C:\photos\a.jpg", "Access to the path is denied."),
        new(@"C:\photos\b.jpg", "The process cannot access the file because it is being used by another process."),
        new(@"C:\photos\sub", "Directory listing interrupted", SkippedKind.ListingInterrupted),
    ];

    private static string[] ListLines(NativeDialog dialog)
    {
        var list = Item(dialog, ListDialog.ListId);
        var count = (int)User32.SendMessage(list, DialogConstants.LbGetCount, 0, 0);
        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = (int)User32.SendMessage(list, DialogConstants.LbGetTextLen, i, 0);
            var buffer = new char[length + 1];
            unsafe
            {
                fixed (char* p = buffer)
                {
                    User32.SendMessage(list, DialogConstants.LbGetText, i, (nint)p);
                }
            }

            lines[i] = new string(buffer, 0, length);
        }

        return lines;
    }

    [Fact]
    public void SkippedFiles_ListsOneLinePerEntry_WithTheIntroInTheWarningColor_AndOnlyACloseButton()
    {
        OnSta(() =>
        {
            var dialog = new ListDialog(ListDialogContent.ForSkippedFiles(Skipped));
            ShowOnOwner(dialog, (d, _) =>
            {
                Assert.Equal(SkippedFilesPresenter.Title, TextOf(d.Hwnd));
                Assert.Equal(SkippedFilesPresenter.Intro, TextOf(d, ListDialog.IntroId));
                Assert.Equal(SkippedFilesPresenter.BuildLines(Skipped), ListLines(d));
                Assert.Equal(SkippedFilesPresenter.CloseText, TextOf(d, ListDialog.CancelId));
                Assert.Equal(0, Item(d, ListDialog.ConfirmId)); // no confirm button
                Assert.Equal(0, Item(d, ListDialog.HeadingId)); // no heading in this window
                Command(d, ListDialog.CancelId);
                Assert.True(d.IsClosed);
            });
            return 0;
        });
    }

    [Fact]
    public void SkippedFiles_EnterAndEscapeBothClose_AndAFailedOkNeverReportsConfirmed()
    {
        var results = OnSta(() => new[]
        {
            ShowOnOwner(new ListDialog(ListDialogContent.ForSkippedFiles(Skipped)), (d, _) =>
            {
                Assert.True(Key(d, Item(d, ListDialog.ListId), VkReturn));
                Drain(d);
            }),
            ShowOnOwner(new ListDialog(ListDialogContent.ForSkippedFiles(Skipped)), (d, _) =>
            {
                Assert.True(Key(d, Item(d, ListDialog.ListId), VkEscape));
                Drain(d);
            }),
        });

        Assert.All(results, r => Assert.Equal(NativeDialog.IdCancel, r));
    }

    private static readonly BatchReviewItem[] Batch = [new(@"C:\photos\a.jpg", 1234), new(@"C:\photos\gone.jpg", null)];

    [Fact]
    public void BatchReview_ShowsHeadingSummaryAndLines_AndConfirmReturnsOk()
    {
        var (result, lines) = OnSta(() =>
        {
            string[] seen = [];
            var r = ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) =>
            {
                Assert.Equal(Tr.BatchReviewTitle, TextOf(d.Hwnd));
                Assert.Equal(Tr.BatchReviewTitle, TextOf(d, ListDialog.HeadingId));
                Assert.Equal(BatchReviewPresenter.Summary(Batch.Length), TextOf(d, ListDialog.IntroId));
                Assert.Equal(Tr.BatchReviewConfirm, TextOf(d, ListDialog.ConfirmId));
                Assert.Equal(Tr.CommonCancel, TextOf(d, ListDialog.CancelId));
                seen = ListLines(d);
                Command(d, ListDialog.ConfirmId);
            });
            return (r, seen);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(BatchReviewPresenter.BuildLines(Batch), lines);
    }

    [Theory]
    [InlineData("cancel_button")]
    [InlineData("escape")]
    [InlineData("wm_close")]
    [InlineData("alt_f4")]
    [InlineData("outside_close")]
    public void BatchReview_EveryWayExceptConfirm_ReturnsCancel_SoNothingIsMoved(string how)
    {
        var result = OnSta(() => ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) =>
        {
            switch (how)
            {
                case "cancel_button":
                    Command(d, ListDialog.CancelId);
                    break;
                case "escape":
                    Assert.True(Key(d, Item(d, ListDialog.ListId), VkEscape));
                    Drain(d);
                    break;
                case "wm_close":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                case "alt_f4":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmSysCommand, (nint)ScClose, 0);
                    break;
                default:
                    d.Close();
                    break;
            }

            Assert.True(d.IsClosed);
        }));

        Assert.Equal(NativeDialog.IdCancel, result);
    }

    [Fact]
    public void BatchReview_EnterActivatesTheDefaultConfirmButton_LikeTheWpfWindow()
    {
        var result = OnSta(() => ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) =>
        {
            Assert.Equal(WindowMessages.IdOk, (int)(User32.SendMessage(d.Hwnd, DialogConstants.DmGetDefId, 0, 0) & 0xFFFF));
            Assert.True(Key(d, Item(d, ListDialog.ListId), VkReturn));
            Drain(d);
        }));

        Assert.Equal(NativeDialog.IdOk, result);
    }

    [Fact]
    public void List_IsResizable_TheListFollowsTheWindow_AndTheButtonsStayAtTheBottomRight()
    {
        OnSta(() =>
        {
            ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) =>
            {
                var list = ChildRect(d, ListDialog.ListId);
                var confirm = ChildRect(d, ListDialog.ConfirmId);
                var clientBefore = ClientRect(d.Hwnd);
                var window = WindowRect(d.Hwnd);

                User32.SetWindowPos(d.Hwnd, 0, window.Left, window.Top, Width(window) + 200, Height(window) + 120,
                    WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);

                var clientAfter = ClientRect(d.Hwnd);
                Assert.True(Width(clientAfter) > Width(clientBefore) && Height(clientAfter) > Height(clientBefore));
                var listAfter = ChildRect(d, ListDialog.ListId);
                var confirmAfter = ChildRect(d, ListDialog.ConfirmId);
                Assert.Equal(Width(list) + (Width(clientAfter) - Width(clientBefore)), Width(listAfter));
                Assert.Equal(Height(list) + (Height(clientAfter) - Height(clientBefore)), Height(listAfter));
                Assert.Equal(Width(clientBefore) - confirm.Right, Width(clientAfter) - confirmAfter.Right);
                Assert.Equal(Height(clientBefore) - confirm.Bottom, Height(clientAfter) - confirmAfter.Bottom);

                // Shrinking below the minimum is refused by WM_GETMINMAXINFO: the confirm button never leaves the client area.
                User32.SetWindowPos(d.Hwnd, 0, window.Left, window.Top, 100, 100, WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
                var clientTiny = ClientRect(d.Hwnd);
                Assert.True(Width(clientTiny) >= ChildRect(d, ListDialog.ConfirmId).Right);
                d.Close();
            });
            return 0;
        });
    }

    [Fact]
    public void List_WithFiftyThousandLines_AddsAllOfThem_AndScrollsHorizontallyForTheLongest()
    {
        var entries = Enumerable.Range(0, 50_000).Select(i => new SkippedEntry($@"C:\photos\{i:000000}.jpg", "locked")).ToArray();
        var longest = new SkippedEntry(@"C:\" + new string('x', 400) + ".jpg", "locked");
        OnSta(() =>
        {
            ShowOnOwner(new ListDialog(ListDialogContent.ForSkippedFiles([.. entries, longest])), (d, _) =>
            {
                var list = Item(d, ListDialog.ListId);
                Assert.Equal(50_001, (int)User32.SendMessage(list, DialogConstants.LbGetCount, 0, 0));
                var style = (uint)User32.GetWindowLongPtr(list, WindowMessages.GwlStyle);
                Assert.NotEqual(0u, style & DialogConstants.WsHScroll);
                d.Close();
            });
            return 0;
        });
    }

    [Fact]
    public void List_Vietnamese_HeadingIntroAndButtonsAreNotClipped()
    {
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        OnSta(() =>
        {
            ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) =>
            {
                var client = ClientRect(d.Hwnd);
                foreach (var id in new[] { ListDialog.HeadingId, ListDialog.IntroId, ListDialog.ListId, ListDialog.CancelId, ListDialog.ConfirmId })
                {
                    var rect = ChildRect(d, id);
                    Assert.True(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= client.Right && rect.Bottom <= client.Bottom, $"control {id} clipped");
                }

                Assert.True(NeededTextHeight(Item(d, ListDialog.IntroId)) <= Height(ChildRect(d, ListDialog.IntroId)));
                Assert.True(NeededTextHeight(Item(d, ListDialog.HeadingId)) <= Height(ChildRect(d, ListDialog.HeadingId)));
                Assert.True(ChildRect(d, ListDialog.CancelId).Right <= ChildRect(d, ListDialog.ConfirmId).Left);
                d.Close();
            });
            return 0;
        });
    }

    [Fact]
    public void Chrome_IsAppliedOnceToEachListAndDiagnosticsDialog()
    {
        var list = new RecordingChrome();
        var diagnostics = new RecordingChrome();
        OnSta(() =>
        {
            ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch), list), (d, _) => d.Close());
            ShowOnOwner(new DiagnosticsDialog(SampleDisplay(), null, diagnostics), (d, _) => d.Close());
            return 0;
        });

        var listCall = Assert.Single(list.Calls);
        Assert.Contains(listCall.Controls, c => c.Class == DialogControlClass.ListBox);
        Assert.Equal(2, listCall.Controls.Count(c => c.Class == DialogControlClass.Button));
        var diagnosticsCall = Assert.Single(diagnostics.Calls);
        Assert.Equal(2, diagnosticsCall.Controls.Count(c => c.Class == DialogControlClass.Button));
        Assert.Equal(2 + (2 * DiagnosticsDialog.RowCount) + 2, diagnosticsCall.Controls.Length);
    }

    [Fact]
    public void RepeatedOpenAndClose_OfEveryDialog_LeaksNoGdiOrUserObjects()
    {
        OnSta(() =>
        {
            void Cycle()
            {
                ShowOnOwner(new DiagnosticsDialog(SampleDisplay()), (d, _) => Command(d, DiagnosticsDialog.CloseId));
                ShowOnOwner(new ListDialog(ListDialogContent.ForSkippedFiles(Skipped)), (d, _) => Command(d, ListDialog.CancelId));
                ShowOnOwner(new ListDialog(ListDialogContent.ForBatchReview(Batch)), (d, _) => Command(d, ListDialog.ConfirmId));
            }

            Cycle();
            var gdi = GdiObjects();
            var user = UserObjects();
            for (var i = 0; i < 15; i++)
            {
                Cycle();
            }

            Assert.True(GdiObjects() <= gdi, "GDI objects leaked");
            Assert.True(UserObjects() <= user, "USER objects leaked");
            return 0;
        });
    }

    [Fact]
    public void ListAndDiagnostics_DpiChange_RescalesTheFontsIncludingTheHeaderFont()
    {
        OnSta(() =>
        {
            ShowOnOwner(new DiagnosticsDialog(SampleDisplay()), (d, _) =>
            {
                var bodyBefore = FontHeight(d.BodyFont);
                var headerFont = User32.SendMessage(Item(d, DiagnosticsDialog.HeaderId), DialogConstants.WmGetFont, 0, 0);
                var headerBefore = FontHeight(headerFont);
                Assert.True(Math.Abs(headerBefore) > Math.Abs(bodyBefore), "header font must be larger than the body font");
                var window = WindowRect(d.Hwnd);
                var big = d.Dpi * 2;
                var suggested = new Rect { Left = window.Left, Top = window.Top, Right = window.Left + Width(window) * 2, Bottom = window.Top + Height(window) * 2 };
                unsafe
                {
                    User32.SendMessage(d.Hwnd, WindowMessages.WmDpiChanged, (nint)((big << 16) | big), (nint)(&suggested));
                }

                Assert.Equal(big, d.Dpi);
                Assert.InRange(Math.Abs(FontHeight(d.BodyFont)) * 1.0 / Math.Abs(bodyBefore), 1.8, 2.2);
                var headerAfter = User32.SendMessage(Item(d, DiagnosticsDialog.HeaderId), DialogConstants.WmGetFont, 0, 0);
                Assert.InRange(Math.Abs(FontHeight(headerAfter)) * 1.0 / Math.Abs(headerBefore), 1.8, 2.2);
                d.Close();
            });
            return 0;
        });
    }
}
