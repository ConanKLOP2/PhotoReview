using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ2: các điều khiển mới của khung NativeDialog (combobox, listbox đa chọn owner-draw có huy hiệu, listview report, progress,
/// trackbar, edit nhiều dòng/số, checkbox/radio/group box, tooltip) qua <see cref="ControlGalleryDialog"/> - cửa sổ THẬT, người dùng
/// mô phỏng bằng thông điệp. Dark được kiểm bằng ảnh chụp (<c>NativeDialogSnapshotTests</c>); ở đây kiểm cờ dark và hành vi.
/// </summary>
[Trait("Category", "UI")]
public sealed unsafe class NativeControlsTests
{
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    private static T Run<T>(Func<ControlGalleryDialog, nint, T> body, INativeDialogChrome? chrome = null, bool visualStyles = false)
    {
        return OnSta(() =>
        {
            using var styles = visualStyles ? ActivateVisualStyles() : null; // comctl32 v6: PBM_GETBARCOLOR and friends
            T result = default!;
            var dialog = new ControlGalleryDialog(chrome);
            ShowOnOwner(dialog, (d, owner) =>
            {
                result = body((ControlGalleryDialog)d, owner);
                d.Close();
            });
            return result;
        });
    }

    private static string ClassOf(nint hwnd)
    {
        var buffer = stackalloc char[64];
        var length = User32.GetClassName(hwnd, buffer, 64);
        return new string(buffer, 0, length);
    }

    private static nint Notify(ControlGalleryDialog dialog, byte[] nmhdr)
    {
        fixed (byte* p = nmhdr)
        {
            return User32.SendMessage(dialog.Hwnd, ControlConstants.WmNotify, 0, (nint)p);
        }
    }

    /// <summary>NMLVCUSTOMDRAW (104 byte) cho hàng <paramref name="row"/> ở giai đoạn ITEMPREPAINT; trả struct sau khi dialog xử lý.</summary>
    private static (nint Result, uint TextBack, uint Text) CustomDrawRow(ControlGalleryDialog dialog, int row, int param)
    {
        var buffer = new byte[ControlConstants.NmLvCustomDrawSize];
        fixed (byte* p = buffer)
        {
            *(nint*)p = Item(dialog, ControlGalleryDialog.Report);
            *(nint*)(p + 8) = ControlGalleryDialog.Report;
            *(int*)(p + 16) = ControlConstants.NmCustomDraw;
            *(uint*)(p + 24) = ControlConstants.CddsItemPrePaint;
            *(nint*)(p + 56) = row;
            *(nint*)(p + 72) = param;
            var result = User32.SendMessage(dialog.Hwnd, ControlConstants.WmNotify, ControlGalleryDialog.Report, (nint)p);
            // WM_NOTIFY: the dialog procedure answers through DWLP_MSGRESULT (SendMessage returns that value).
            return (result, *(uint*)(p + 84), *(uint*)(p + 80));
        }
    }

    // ---- creation + dark flags ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryNewControlClass_IsCreatedFromTheTemplate()
    {
        var classes = Run((d, _) => new Dictionary<int, string>
        {
            [ControlGalleryDialog.Combo] = ClassOf(Item(d, ControlGalleryDialog.Combo)),
            [ControlGalleryDialog.BadgeList] = ClassOf(Item(d, ControlGalleryDialog.BadgeList)),
            [ControlGalleryDialog.Report] = ClassOf(Item(d, ControlGalleryDialog.Report)),
            [ControlGalleryDialog.Progress] = ClassOf(Item(d, ControlGalleryDialog.Progress)),
            [ControlGalleryDialog.Slider] = ClassOf(Item(d, ControlGalleryDialog.Slider)),
            [ControlGalleryDialog.Notes] = ClassOf(Item(d, ControlGalleryDialog.Notes)),
            [ControlGalleryDialog.Check] = ClassOf(Item(d, ControlGalleryDialog.Check)),
            [ControlGalleryDialog.Group] = ClassOf(Item(d, ControlGalleryDialog.Group)),
        });

        Assert.Equal("ComboBox", classes[ControlGalleryDialog.Combo]);
        Assert.Equal("ListBox", classes[ControlGalleryDialog.BadgeList]);
        Assert.Equal(ControlConstants.ListViewClass, classes[ControlGalleryDialog.Report]);
        Assert.Equal(ControlConstants.ProgressClass, classes[ControlGalleryDialog.Progress]);
        Assert.Equal(ControlConstants.TrackbarClass, classes[ControlGalleryDialog.Slider]);
        Assert.Equal("Edit", classes[ControlGalleryDialog.Notes]);
        Assert.Equal("Button", classes[ControlGalleryDialog.Check]);
        Assert.Equal("Button", classes[ControlGalleryDialog.Group]);
    }

    [Fact]
    public void Chrome_IsGivenEveryControlWithItsClass_ExactlyOnce()
    {
        var chrome = new RecordingChrome();
        Run((_, _) => 0, chrome);

        var call = Assert.Single(chrome.Calls);
        var classes = call.Controls.Select(c => c.Class).ToHashSet();
        Assert.Contains(DialogControlClass.ComboBox, classes);
        Assert.Contains(DialogControlClass.ListView, classes);
        Assert.Contains(DialogControlClass.Progress, classes);
        Assert.Contains(DialogControlClass.Trackbar, classes);
        Assert.Contains(DialogControlClass.ListBox, classes);
        Assert.Contains(DialogControlClass.Edit, classes);
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("Edit")]
    [InlineData("ListBox")]
    [InlineData("ScrollBar")]
    [InlineData("ComboBox")]
    [InlineData("ListView")]
    [InlineData("Trackbar")]
    [InlineData("Progress")]
    public void EveryThemedControlClass_DropsTheSystemTheme_NotADarkModeTheme(string controlClass)
        => Assert.Equal(DarkDialogChrome.ClassicTheme, DarkDialogChrome.ThemeFor(Enum.Parse<DialogControlClass>(controlClass)));

    [Fact]
    public void Static_KeepsItsTheme_AndEveryOtherControlIsClassic()
    {
        Assert.Null(DarkDialogChrome.ThemeFor(DialogControlClass.Static));
        var (radio, group, check) = Run((d, _) => (
            DarkDialogChrome.ThemeForControl(Item(d, ControlGalleryDialog.RadioA), DialogControlClass.Button),
            DarkDialogChrome.ThemeForControl(Item(d, ControlGalleryDialog.Group), DialogControlClass.Button),
            DarkDialogChrome.ThemeForControl(Item(d, ControlGalleryDialog.Check), DialogControlClass.Button)));

        Assert.Equal(DarkDialogChrome.ClassicTheme, radio);
        Assert.Equal(DarkDialogChrome.ClassicTheme, group);
        Assert.Equal(DarkDialogChrome.ClassicTheme, check);
    }

    [Fact]
    public void RealChrome_ClearsTheThemeOfEachControlIncludingTheComboDropDownAndTheListViewHeader_AndNeverUsesDarkModeThemes()
    {
        var calls = new List<(nint Hwnd, string Theme, string? Ids)>();
        var chrome = new DarkDialogChrome((hwnd, theme, ids) => calls.Add((hwnd, theme, ids)));

        var (combo, comboList, report, header, radio, group, check, progress, slider, edit) = Run((d, _) => (
            Item(d, ControlGalleryDialog.Combo), ComboBoxControl.ListWindow(Item(d, ControlGalleryDialog.Combo)),
            Item(d, ControlGalleryDialog.Report), ListViewControl.Header(Item(d, ControlGalleryDialog.Report)),
            Item(d, ControlGalleryDialog.RadioA), Item(d, ControlGalleryDialog.Group), Item(d, ControlGalleryDialog.Check),
            Item(d, ControlGalleryDialog.Progress), Item(d, ControlGalleryDialog.Slider), Item(d, ControlGalleryDialog.Count)),
            chrome);

        foreach (var hwnd in new[] { combo, comboList, report, header, radio, group, check, progress, slider, edit })
        {
            Assert.NotEqual(0, hwnd);
            var last = calls.Last(c => c.Hwnd == hwnd);
            Assert.Equal(string.Empty, last.Theme); // classic = sub-app "" AND ids "" (not the light default)
            Assert.Equal(string.Empty, last.Ids);
        }

        Assert.DoesNotContain(calls, c => c.Theme.StartsWith("DarkMode_", StringComparison.Ordinal));
    }

    // ---- combobox -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ComboBox_ListsItems_SelectsByIndex_AndHasADropDownWindow()
    {
        var (count, first, text, listWindow, dropped) = Run((d, _) =>
        {
            var combo = Item(d, ControlGalleryDialog.Combo);
            ComboBoxControl.Select(combo, 2);
            var selected = ComboBoxControl.SelectedIndex(combo);
            ComboBoxControl.Drop(combo, true);
            var isDropped = ComboBoxControl.IsDropped(combo);
            ComboBoxControl.Drop(combo, false);
            return (ComboBoxControl.Count(combo), selected, ComboBoxControl.TextAt(combo, selected), ComboBoxControl.ListWindow(combo),
                isDropped);
        });

        Assert.Equal(3, count);
        Assert.Equal(2, first);
        Assert.Equal("Recycle", text);
        Assert.NotEqual(0, listWindow);
        Assert.True(dropped);
    }

    [Fact]
    public void ComboBox_SelectionChange_ReachesTheDialogAsACommand()
    {
        var command = Run((d, _) =>
        {
            var combo = Item(d, ControlGalleryDialog.Combo);
            Command(d, ControlGalleryDialog.Combo, ControlConstants.CbnSelChange);
            return d.LastCommandId;
        });

        Assert.Equal(ControlGalleryDialog.Combo, command);
    }

    // ---- listbox đa chọn owner-draw ------------------------------------------------------------------------------------

    [Fact]
    public void BadgeList_AllowsSeveralSelectedRows_AndSelectAllNone()
    {
        var (afterToggle, afterAll, afterNone) = Run((d, _) =>
        {
            var list = Item(d, ControlGalleryDialog.BadgeList);
            ListBoxControl.SetSelected(list, 0, true);
            ListBoxControl.SetSelected(list, 3, true);
            var toggled = ListBoxControl.SelectedIndices(list).ToArray();
            ListBoxControl.SelectAll(list, true);
            var all = ListBoxControl.SelectedIndices(list).ToArray();
            ListBoxControl.SelectAll(list, false);
            return (toggled, all, ListBoxControl.SelectedIndices(list).ToArray());
        });

        Assert.Equal([0, 1, 3], afterToggle); // row 1 was selected by the dialog
        Assert.Equal([0, 1, 2, 3], afterAll);
        Assert.Empty(afterNone);
    }

    [Fact]
    public void BadgeList_DrawsSelectionColourAndABadge()
    {
        var (selectedPixel, plainPixel, badgePixelDiffers) = Run((d, _) =>
        {
            var list = Item(d, ControlGalleryDialog.BadgeList);
            var width = 300;
            var height = 24;
            var screen = User32.GetDC(0);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = Gdi32.SelectObject(memory, bitmap);
            try
            {
                uint Draw(int index, uint state)
                {
                    var item = new byte[ControlConstants.DrawItemSize];
                    fixed (byte* p = item)
                    {
                        *(int*)(p + 4) = ControlGalleryDialog.BadgeList;
                        *(int*)(p + 8) = index;
                        *(uint*)(p + 16) = state;
                        *(nint*)(p + 24) = list;
                        *(nint*)(p + 32) = memory;
                        *(Rect*)(p + 40) = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
                        User32.SendMessage(d.Hwnd, ControlConstants.WmDrawItem, ControlGalleryDialog.BadgeList, (nint)p);
                    }

                    return GetPixel(memory, 150, 2); // away from the text, left of the badge
                }

                var selected = Draw(0, ControlConstants.OdsSelected);
                var plain = Draw(0, 0);
                // The badge pill sits at the right edge: its border colour is not the plain row colour.
                var rightEdge = GetPixel(memory, width - 4 - 6, height / 2);
                return (selected, plain, rightEdge != plain);
            }
            finally
            {
                Gdi32.SelectObject(memory, old);
                Gdi32.DeleteObject(bitmap);
                DeleteDC(memory);
                _ = User32.ReleaseDC(0, screen);
            }
        });

        Assert.Equal(DialogColors.FromKey("Dark.Selection"), selectedPixel);
        Assert.Equal(DialogColors.Input, plainPixel);
        Assert.True(badgePixelDiffers);
    }

    [Fact]
    public void BadgeList_ItemTooltips_ComeFromTheProvider_PerRow()
    {
        var (tools, first, outOfRange) = Run((d, _) => (d.TooltipToolCount,
            d.ItemTooltipText(ControlGalleryDialog.BadgeList, 1), d.ItemTooltipText(ControlGalleryDialog.BadgeList, 99)));

        Assert.True(tools >= 2, "one tool for the push button and one for the list");
        Assert.Equal("Mode: Preview", first);
        Assert.Null(outOfRange);
    }

    // ---- listview report -----------------------------------------------------------------------------------------------

    [Fact]
    public void ListView_HasTheColumnsAndRows_WithCellText()
    {
        var (columns, rows, cell, rightAligned) = Run((d, _) =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            var header = ListViewControl.Header(report);
            return ((int)User32.SendMessage(header, 0x1200 /* HDM_GETITEMCOUNT */, 0, 0), ListViewControl.Count(report),
                ListViewControl.CellText(report, 1, 1), ColumnFormatOf(header, 5));
        });

        Assert.Equal(10, columns);
        Assert.Equal(4, rows);
        Assert.Equal("Preview Light", cell);
        Assert.Equal(ControlConstants.LvcfmtRight, rightAligned & 0x3);
    }

    private static int ColumnFormatOf(nint header, int column)
    {
        var item = new byte[ControlConstants.HdItemSize];
        fixed (byte* p = item)
        {
            *(uint*)p = ControlConstants.HdiFormat;
            User32.SendMessage(header, ControlConstants.HdmGetItem, column, (nint)p);
            return *(int*)(p + 28);
        }
    }

    [Fact]
    public void ListView_RowSelection_IsReadAndSetThroughTheControl()
    {
        var (initial, selected, isSelected, otherSelected) = Run((d, _) =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            var before = ListViewControl.SelectedRow(report);
            ListViewControl.SelectRow(report, 2);
            return (before, ListViewControl.SelectedRow(report), ListViewControl.IsRowSelected(report, 2),
                ListViewControl.IsRowSelected(report, 0));
        });

        Assert.Equal(-1, initial);
        Assert.Equal(2, selected);
        Assert.True(isSelected);
        Assert.False(otherSelected);
    }

    [Fact]
    public void ListView_ColumnClick_SortsAscendingThenDescending_AndDrawsTheArrowState()
    {
        var (afterFirst, afterSecond, clicks) = Run((d, _) =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            var nm = new byte[ControlConstants.NmListViewSize];
            fixed (byte* p = nm)
            {
                *(nint*)p = report;
                *(nint*)(p + 8) = ControlGalleryDialog.Report;
                *(int*)(p + 16) = ControlConstants.LvnColumnClick;
                *(int*)(p + 28) = 1; // iSubItem: the Profile column
            }

            Notify(d, nm);
            var first = Enumerable.Range(0, 4).Select(i => ListViewControl.CellText(report, i, 1)).ToArray();
            Notify(d, nm);
            var second = Enumerable.Range(0, 4).Select(i => ListViewControl.CellText(report, i, 1)).ToArray();
            return (first, second, d.ColumnClicks.ToArray());
        });

        Assert.Equal(["Fast Sequential", "Original Correctness", "Preview Light", "Rapid Key Press"], afterFirst);
        Assert.Equal(["Rapid Key Press", "Preview Light", "Original Correctness", "Fast Sequential"], afterSecond);
        Assert.Equal([1, 1], clicks);
    }

    [Fact]
    public void ListView_CustomDraw_ColoursBestFailAndAlternatingRows()
    {
        var (best, fail, even, odd) = Run((d, _) => (
            CustomDrawRow(d, row: 1, param: 1),
            CustomDrawRow(d, row: 2, param: 2),
            CustomDrawRow(d, row: 0, param: 0),
            CustomDrawRow(d, row: 3, param: 3)));

        Assert.Equal(DialogColors.FromKey("Dark.SuccessBackground"), best.TextBack);
        Assert.Equal(DialogColors.FromKey("Dark.ErrorBackground"), fail.TextBack);
        Assert.Equal(DialogColors.FromKey("Dark.Panel"), even.TextBack);
        Assert.Equal(DialogColors.FromKey("Dark.RowAlt"), odd.TextBack);
        Assert.Equal(DialogColors.Text, best.Text);
        Assert.Equal(ControlConstants.CdrfNewFont, best.Result);
    }

    [Fact]
    public void ListView_HeaderIsDrawnByTheDialog_SkippingTheThemesGreyText()
    {
        var result = Run((d, _) =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            var header = ListViewControl.Header(report);
            var hdc = User32.GetDC(header);
            var buffer = new byte[ControlConstants.NmCustomDrawSize];
            try
            {
                fixed (byte* p = buffer)
                {
                    *(nint*)p = header;
                    *(nint*)(p + 8) = 0;
                    *(int*)(p + 16) = ControlConstants.NmCustomDraw;
                    *(uint*)(p + 24) = ControlConstants.CddsItemPrePaint;
                    *(nint*)(p + 32) = hdc;
                    *(Rect*)(p + 40) = new Rect { Left = 0, Top = 0, Right = 80, Bottom = 20 };
                    *(nint*)(p + 56) = 1;
                    // The header notifies its parent (the listview); the dialog hooks the listview to draw it.
                    return User32.SendMessage(report, ControlConstants.WmNotify, 0, (nint)p);
                }
            }
            finally
            {
                _ = User32.ReleaseDC(header, hdc);
            }
        });

        Assert.Equal(ControlConstants.CdrfSkipDefault, result);
    }

    // ---- progress / trackbar ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.4, 400)]
    [InlineData(1.0, 1000)]
    [InlineData(-3.0, 0)]
    [InlineData(7.0, 1000)]
    [InlineData(double.NaN, 0)]
    public void Progress_ShowsTheFraction_Clamped(double fraction, int expectedPosition)
    {
        var position = Run((d, _) =>
        {
            var progress = Item(d, ControlGalleryDialog.Progress);
            ProgressControl.SetFraction(progress, fraction);
            return (int)User32.SendMessage(progress, ControlConstants.PbmGetPos, 0, 0);
        });

        Assert.Equal(expectedPosition, position);
    }

    [Fact]
    public void ListView_HeaderShowsTheSortArrowOnlyOnTheSortedColumn()
    {
        var (unsorted, sorted, otherColumn) = Run((d, _) =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            var header = ListViewControl.Header(report);
            // Pixels of the arrow area (right end of a 90 x 20 header cell) after drawing column `column`.
            uint[] Cell(int column)
            {
                var screen = User32.GetDC(0);
                var memory = CreateCompatibleDC(screen);
                var bitmap = CreateCompatibleBitmap(screen, 90, 20);
                var old = Gdi32.SelectObject(memory, bitmap);
                try
                {
                    var buffer = new byte[ControlConstants.NmCustomDrawSize];
                    fixed (byte* p = buffer)
                    {
                        *(nint*)p = header;
                        *(int*)(p + 16) = ControlConstants.NmCustomDraw;
                        *(uint*)(p + 24) = ControlConstants.CddsItemPrePaint;
                        *(nint*)(p + 32) = memory;
                        *(Rect*)(p + 40) = new Rect { Left = 0, Top = 0, Right = 90, Bottom = 20 };
                        *(nint*)(p + 56) = column;
                        User32.SendMessage(report, ControlConstants.WmNotify, 0, (nint)p);
                    }

                    var pixels = new List<uint>();
                    for (var x = 62; x < 82; x++)
                    {
                        for (var y = 3; y < 17; y++)
                        {
                            pixels.Add(GetPixel(memory, x, y));
                        }
                    }

                    return [.. pixels];
                }
                finally
                {
                    Gdi32.SelectObject(memory, old);
                    Gdi32.DeleteObject(bitmap);
                    DeleteDC(memory);
                    _ = User32.ReleaseDC(0, screen);
                }
            }

            var before = Cell(1);
            d.SortBy(1);
            return (before, Cell(1), Cell(2));
        });

        Assert.Equal(unsorted, otherColumn); // an unsorted column looks the same before and after sorting another one
        Assert.NotEqual(unsorted, sorted); // the sorted column gained an arrow
    }

    [Fact]
    public void Trackbar_HasItsRange_AndScrollMessagesReachTheDialog()
    {
        var (min, max, position, scrolls, label) = Run((d, _) =>
        {
            var slider = Item(d, ControlGalleryDialog.Slider);
            TrackbarControl.SetPosition(slider, 70);
            User32.SendMessage(d.Hwnd, ControlConstants.WmHScroll, ControlConstants.TbThumbTrack, slider);
            return ((int)User32.SendMessage(slider, ControlConstants.TbmGetRangeMin, 0, 0),
                (int)User32.SendMessage(slider, ControlConstants.TbmGetRangeMax, 0, 0), TrackbarControl.Position(slider),
                d.Scrolls.ToArray(), TextOf(d, ControlGalleryDialog.SliderValue));
        });

        Assert.Equal(0, min);
        Assert.Equal(100, max);
        Assert.Equal(70, position);
        Assert.Equal([(ControlGalleryDialog.Slider, ControlConstants.TbThumbTrack)], scrolls);
        Assert.Equal("70", label);
    }

    // ---- edit / checkbox / radio ---------------------------------------------------------------------------------------

    [Fact]
    public void MultilineEdit_KeepsEnterForItself_SoTheDialogDoesNotClose()
    {
        var (closed, text) = Run((d, _) =>
        {
            var notes = Item(d, ControlGalleryDialog.Notes);
            User32.SetFocus(notes);
            Key(d, notes, VkReturn);
            Drain(d); // a default-button press would be posted as WM_COMMAND(IDOK) and end the dialog
            return (d.IsClosed, TextOf(notes));
        });

        Assert.False(closed, "ES_WANTRETURN: Enter belongs to the edit box, not the default button");
        Assert.Contains("Line two", text);
    }

    [Fact]
    public void NumberEdit_RejectsLetters_AndAcceptsDigits()
    {
        var text = Run((d, _) =>
        {
            var edit = Item(d, ControlGalleryDialog.Count);
            User32.SendMessage(edit, DialogConstants.EmSetSel, 2, 2); // after "64"
            User32.SendMessage(edit, 0x0102 /* WM_CHAR */, 'x', 1);
            User32.SendMessage(edit, 0x0102, '5', 1);
            return TextOf(edit);
        });

        Assert.Equal("645", text);
    }

    [Fact]
    public void Checkbox_ToggleOnClick_AndRadiosAreExclusive()
    {
        var (checkedAfter, aAfter, bAfter) = Run((d, _) =>
        {
            var check = Item(d, ControlGalleryDialog.Check);
            User32.SendMessage(check, 0x00F5 /* BM_CLICK */, 0, 0);
            var radioA = Item(d, ControlGalleryDialog.RadioA);
            var radioB = Item(d, ControlGalleryDialog.RadioB);
            User32.SendMessage(radioB, 0x00F5, 0, 0);
            return (ButtonStateControl.IsChecked(check), ButtonStateControl.IsChecked(radioA), ButtonStateControl.IsChecked(radioB));
        });

        Assert.False(checkedAfter); // it started checked
        Assert.False(aAfter);
        Assert.True(bAfter);
    }

    // ---- bố cục + DPI + rò rỉ --------------------------------------------------------------------------------------------

    [Fact]
    public void Layout_AtOneAndOneAndAHalfScale_NoControlOverlapsAnotherOrLeavesTheClient()
    {
        var issues = Run((d, _) =>
        {
            var problems = new List<string>();
            problems.AddRange(OverlapsAndOutOfBounds(d, "96 dpi"));
            ChangeDpi(d, 144);
            problems.AddRange(OverlapsAndOutOfBounds(d, "144 dpi"));
            return problems;
        });

        Assert.Empty(issues);
    }

    private static IEnumerable<string> OverlapsAndOutOfBounds(ControlGalleryDialog dialog, string label)
    {
        // The group box deliberately contains the check/radio buttons; everything else must be disjoint.
        int[] ids =
        [
            ControlGalleryDialog.NameLabel, ControlGalleryDialog.Combo, ControlGalleryDialog.BadgeList, ControlGalleryDialog.Report,
            ControlGalleryDialog.Progress, ControlGalleryDialog.Slider, ControlGalleryDialog.Notes, ControlGalleryDialog.Count,
            ControlGalleryDialog.Group, ControlGalleryDialog.TipButton, NativeDialog.IdCancel, NativeDialog.IdOk,
        ];
        var client = ClientRect(dialog.Hwnd);
        var rects = ids.Select(id => (id, rect: ChildRect(dialog, id))).ToList();
        foreach (var (id, rect) in rects)
        {
            if (rect.Left < 0 || rect.Top < 0 || rect.Right > client.Right || rect.Bottom > client.Bottom)
            {
                yield return $"{label}: control {id} leaves the client area";
            }
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i].rect;
                var b = rects[j].rect;
                var combo = rects[i].id == ControlGalleryDialog.Combo || rects[j].id == ControlGalleryDialog.Combo;
                if (!combo && a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                {
                    yield return $"{label}: controls {rects[i].id} and {rects[j].id} overlap";
                }
            }
        }
    }

    private static void ChangeDpi(NativeDialog dialog, uint dpi)
    {
        var current = WindowRect(dialog.Hwnd);
        var factor = dpi / (double)dialog.Dpi;
        var suggested = new Rect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Left + (int)(Width(current) * factor),
            Bottom = current.Top + (int)(Height(current) * factor),
        };
        User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
    }

    [Fact]
    public void OpeningAndClosingTheGallery_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            // Warm up (class registration, comctl32 init, and the one-time GDI caches of the first PAINT: focus-rect pattern brush,
            // DC cache) with the same body as the loop, so only a per-dialog leak shows.
            for (var warm = 0; warm < 2; warm++)
            {
                ShowOnOwner(new ControlGalleryDialog(), (d, _) =>
                {
                    ComboBoxControl.Drop(Item(d, ControlGalleryDialog.Combo), true);
                    ComboBoxControl.Drop(Item(d, ControlGalleryDialog.Combo), false);
                    Drain(d);
                    d.Close();
                });
            }
            var gdiBefore = GdiObjects();
            var userBefore = UserObjects();
            for (var i = 0; i < 12; i++)
            {
                ShowOnOwner(new ControlGalleryDialog(), (d, _) =>
                {
                    ComboBoxControl.Drop(Item(d, ControlGalleryDialog.Combo), true);
                    ComboBoxControl.Drop(Item(d, ControlGalleryDialog.Combo), false);
                    Drain(d);
                    d.Close();
                });
            }

            return ((int)GdiObjects() - (int)gdiBefore, (int)UserObjects() - (int)userBefore);
        });

        Assert.True(gdi <= 2, $"GDI objects grew by {gdi}");
        Assert.True(user <= 2, $"USER objects grew by {user}");
    }
}
