using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Input;
using PhotoReview.App.Presenters;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3b: trang Shortcuts của Settings Win32 - 27 ô bắt phím (EDIT chỉ đọc, subclass bắt WM_KEYDOWN/WM_SYSKEYDOWN), nút xoá cho 14
/// phím tuỳ chọn, khôi phục phím mặc định, cảnh báo trùng phím trực tiếp + dấu ⚠ theo ô, Action profiles lồng, ngôn ngữ, Tab, DPI.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsShortcutsPageTests : IDisposable
{
    private const uint VkTab = 0x09;
    private const uint VkReturn = 0x0D;
    private const uint VkEscape = 0x1B;

    private readonly DataRootFixture _data = new();

    public SettingsShortcutsPageTests() => SettingsPresenter.RememberPage("Shortcuts");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hwnd);

    /// <summary>HIMC của cửa sổ (0 = đã gỡ IME: ô bắt phím không nhận chữ từ IME).</summary>
    private static nint ImmContextOf(nint hwnd) => ImmGetContext(hwnd);

    private static ShortcutsSettingsPage PageOf(SettingsDialog d) => (ShortcutsSettingsPage)d.Pages.Single(page => page.Key == "Shortcuts");

    private static int IndexOf(SettingsDialog d) => d.Pages.ToList().FindIndex(page => page.Key == "Shortcuts");

    private static nint Box(SettingsDialog d, string property) => H(d, PageOf(d).Cell(property).Box);

    private static string BoxText(SettingsDialog d, string property) => Txt(d, PageOf(d).Cell(property).Box);

    private static void SetShortcut(ShortcutMappings mappings, string property, string value)
        => typeof(ShortcutMappings).GetProperty(property)!.SetValue(mappings, value);

    private static string GetShortcut(ShortcutMappings mappings, string property)
        => (string)typeof(ShortcutMappings).GetProperty(property)!.GetValue(mappings)!;

    /// <summary>Phím đi qua subclass của ô (SendMessage tới ô, không qua vòng lặp dialog).</summary>
    private static void Press(nint box, uint virtualKey, bool system = false, bool extended = false)
        => User32.SendMessage(box, system ? WindowMessages.WmSysKeyDown : WindowMessages.WmKeyDown, (nint)virtualKey,
            (nint)(1 | (extended ? 0x01000000 : 0)));

    /// <summary>Phím đi đúng đường của vòng lặp modal: bộ lọc <c>OnDialogKey</c> rồi <c>IsDialogMessage</c>. Trả true nếu có ai nuốt.</summary>
    private static unsafe bool LoopKey(SettingsDialog d, nint box, uint virtualKey, bool system = false, bool extended = false)
    {
        var message = new Msg
        {
            Hwnd = box,
            Message = system ? WindowMessages.WmSysKeyDown : WindowMessages.WmKeyDown,
            WParam = (nint)virtualKey,
            LParam = 1 | (extended ? 0x01000000 : 0),
        };
        return d.HandleKeyForTest(message) || User32.IsDialogMessage(d.Hwnd, &message);
    }

    private static unsafe bool FilterKey(SettingsDialog d, nint box, uint virtualKey, bool system = false)
    {
        var message = new Msg
        {
            Hwnd = box,
            Message = system ? WindowMessages.WmSysKeyDown : WindowMessages.WmKeyDown,
            WParam = (nint)virtualKey,
            LParam = 1,
        };
        return d.HandleKeyForTest(message);
    }

    /// <summary>27 phím khác nhau, hợp lệ, không trùng phím action mặc định (Enter, F3, F4, F5).</summary>
    private static Dictionary<string, string> DistinctKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).Append("D5").ToArray();
        var i = 0;
        foreach (var row in ShortcutsSettingsPage.AllRows)
        {
            keys[row.Property] = names[i++];
        }

        return keys;
    }

    private static AppSettings WithKeys(IReadOnlyDictionary<string, string> keys)
    {
        var settings = new AppSettings();
        foreach (var (property, value) in keys)
        {
            SetShortcut(settings.Shortcuts, property, value);
        }

        return settings;
    }

    // ---- cấu trúc ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Page_HasOneKeyBoxPerFieldMapShortcut_OnlyTheOptionalOnesHaveAClearButton()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var rows = ShortcutsSettingsPage.AllRows.ToList();
        Assert.Equal(27, rows.Count);
        Assert.Equal(27, SettingsFieldMap.Shortcuts.Count);
        Assert.Equal(SettingsFieldMap.Shortcuts.Select(s => s.Property).Order(StringComparer.Ordinal),
            rows.Select(r => r.Property).Order(StringComparer.Ordinal));
        Assert.Equal(ShortcutMappings.OptionalNames.Order(StringComparer.Ordinal),
            rows.Where(r => r.ClearTip is not null).Select(r => r.Property).Order(StringComparer.Ordinal));
        Assert.Equal(14, rows.Count(r => r.ClearTip is not null));
        Assert.Equal(8, ShortcutsSettingsPage.NavigationRows.Count);
        Assert.Equal(13, ShortcutsSettingsPage.ViewRows.Count);
        Assert.Equal(6, ShortcutsSettingsPage.ActionRows.Count);
        Assert.All(rows.Where(r => r.ClearTip is not null), r => Assert.False(string.IsNullOrWhiteSpace(r.ClearTip!())));

        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            Assert.Equal("Shortcuts", d.ActivePageKey);
            foreach (var row in rows)
            {
                var cell = page.Cell(row.Property);
                Assert.True(d.IsKeyBoxId(cell.Box));
                Assert.Equal(row.Label(), Txt(d, cell.Label)); // accessible name: the label right before the box
                Assert.Equal(row.ClearTip is not null, cell.Clear != 0);
                Assert.True(Visible(d, cell.Box));
                var style = (uint)User32.GetWindowLongPtr(H(d, cell.Box), WindowMessages.GwlStyle);
                Assert.NotEqual(0u, style & 0x0800); // ES_READONLY: nothing but CaptureKey ever writes text
                Assert.NotEqual(0u, style & DialogConstants.WsTabStop);
                if (cell.Clear != 0)
                {
                    var clearStyle = (uint)User32.GetWindowLongPtr(H(d, cell.Clear), WindowMessages.GwlStyle);
                    Assert.NotEqual(0u, clearStyle & DialogConstants.WsTabStop);
                    Assert.Equal(PhotoReview.Shell.Win32.Dialogs.SettingsUi.SettingsPageBuilder.ClearGlyph, Txt(d, cell.Clear));
                }

                Assert.Equal(0, ImmContextOf(H(d, cell.Box)));
            }

            d.Close();
        });
    }

    [Fact]
    public void RowOrder_IsTheDocumentedReadingOrder_OfThe27Commands()
    {
        string[] expected =
        [
            "Next", "Previous", "FirstImage", "LastImage", "NextFolder", "PreviousFolder", "OpenFolder", "ToggleCaptureMember",
            "ZoomIn", "ZoomOut", "ZoomActualSize", "ToggleFit", "Fullscreen", "ToggleInfoOverlay", "ClickZoom", "FitWidth", "FitHeight",
            "FitWidth2", "ToggleKeepZoom", "CustomZoom", "Refresh",
            "Skip", "Undo", "Compare", "MoveToFolder", "CopyToFolder", "SendToRecycleBin",
        ];
        Assert.Equal(expected, ShortcutsSettingsPage.AllRows.Select(row => row.Property).ToArray());
    }

    [Fact]
    public void AKeyBox_IsPaintedLikeAnInput_NotLikeALabel()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            var viewport = H(d, SettingsDialog.ViewportId(d.ActivePageIndex));
            var box = Box(d, nameof(ShortcutMappings.Next));
            var label = H(d, page.Cell(nameof(ShortcutMappings.Next)).Label);
            var hdc = User32.GetDC(box);
            try
            {
                var boxBrush = User32.SendMessage(viewport, 0x0138 /* WM_CTLCOLORSTATIC */, hdc, box);
                var labelBrush = User32.SendMessage(viewport, 0x0138, hdc, label);
                Assert.NotEqual(0, boxBrush);
                Assert.NotEqual(0, labelBrush);
                Assert.NotEqual(boxBrush, labelBrush);
                Assert.Equal(boxBrush, User32.SendMessage(viewport, 0x0138, hdc, H(d, page.Cell(nameof(ShortcutMappings.Skip)).Box)));
                Assert.Equal(Gdi32.GetBkColor(hdc), DialogColors.Input);
                Assert.Equal(Gdi32.GetTextColor(hdc), DialogColors.TextStrong);
                // The conflict line is painted in the warning colour (a static, not an input).
                User32.SendMessage(viewport, 0x0138, hdc, H(d, page.Ids.Warning));
                Assert.Equal(Gdi32.GetTextColor(hdc), DialogColors.Warning);
            }
            finally
            {
                _ = User32.ReleaseDC(box, hdc);
            }

            d.Close();
        });
    }

    [Fact]
    public void KeyBoxes_FollowTheirLabel_InZOrderAndTabOrder_AndClearButtonsFollowTheirBox()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            var order = d.PageItems(IndexOf(d)).Select(item => item.Id).ToList();
            var previous = -1;
            foreach (var row in ShortcutsSettingsPage.AllRows)
            {
                var cell = page.Cell(row.Property);
                var position = order.IndexOf(cell.Label);
                Assert.True(position > previous, "rows are declared in reading order");
                Assert.Equal(cell.Box, order[position + 1]);
                if (cell.Clear != 0)
                {
                    Assert.Equal(cell.Clear, order[position + 2]);
                    Assert.Equal(H(d, cell.Box), (nint)User32.GetWindow(H(d, cell.Clear), 3 /* GW_HWNDPREV */));
                }

                Assert.Equal(H(d, cell.Label), (nint)User32.GetWindow(H(d, cell.Box), 3 /* GW_HWNDPREV */));
                previous = position;
            }

            d.Close();
        });
    }

    [Fact]
    public void ClearButtons_HaveATooltip_OneToolPerOptionalShortcut()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal(14, d.TooltipToolCount);
            d.Close();
        });
    }

    // ---- Load -> Collect -> Save -------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryShortcut_LoadsIntoItsBox_AndCollectsAndSavesBack()
    {
        var keys = DistinctKeys();
        AppSettings? saved = null;
        Show(WithKeys(keys), (d, _) =>
        {
            var collected = d.CollectAll();
            foreach (var (property, value) in keys)
            {
                Assert.Equal(value, BoxText(d, property));
                Assert.Equal(value, collected.ShortcutTexts[property]);
            }

            Assert.True(d.TrySaveAll(), "27 distinct valid keys are accepted");
            saved = d.Settings;
            d.Close();
        });

        Assert.All(keys, pair => Assert.Equal(pair.Value, GetShortcut(saved!.Shortcuts, pair.Key)));
    }

    [Fact]
    public void ABox_ShowsTheTextAsStored_AndSaveWritesTheCanonicalSpelling()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Next = "Return"; // an alias spelling in the config
        settings.Shortcuts.NextFolder = "Next";
        Show(settings, (d, _) =>
        {
            Assert.Equal("Return", BoxText(d, nameof(ShortcutMappings.Next)));
            Assert.Equal("Next", BoxText(d, nameof(ShortcutMappings.NextFolder)));
            d.Close();
        });
        var (_, dialog) = Show(settings, (d, _) =>
        {
            // Return/Enter is also an action's default shortcut: free it first so the only thing under test is the spelling.
            d.SetActionsJson("[]", source: null);
            Assert.True(d.TrySaveAll());
            d.Close();
        });
        Assert.Equal("Enter", dialog.Settings.Shortcuts.Next);
        Assert.Equal("PageDown", dialog.Settings.Shortcuts.NextFolder);
    }

    [Fact]
    public void Labels_AreTheCommandNames_InEnglishAndVietnamese()
    {
        foreach (var localizer in new[] { TestLocalization.English, TestLocalization.Vietnamese })
        {
            using var scope = TestLocalization.Use(localizer);
            Show(new AppSettings(), (d, _) =>
            {
                var page = PageOf(d);
                foreach (var row in ShortcutsSettingsPage.AllRows)
                {
                    Assert.Equal(row.Label(), Txt(d, page.Cell(row.Property).Label));
                    Assert.False(string.IsNullOrWhiteSpace(row.Label()));
                }

                Assert.Equal(Tr.SettingsShortcutsResetKeys, Txt(d, page.Ids.ResetKeys));
                Assert.Equal(Tr.SettingsEditActionProfiles, Txt(d, page.Ids.EditActions));
                d.Close();
            });
        }

        Assert.NotEqual(TestLocalization.English.Get("settings.shortcut.next"), TestLocalization.Vietnamese.Get("settings.shortcut.next"));
        Assert.NotEqual(TestLocalization.English.Get("settings.shortcuts.resetKeys"), TestLocalization.Vietnamese.Get("settings.shortcuts.resetKeys"));
    }

    // ---- bắt phím ----------------------------------------------------------------------------------------------------------------

    private static IEnumerable<(uint VirtualKey, string Expected)> CapturedKeys()
    {
        (uint, string)[] keys =
        [
            (0x20, "Space"), (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x24, "Home"), (0x23, "End"),
            (0x21, "PageUp"), (0x22, "PageDown"), (0x2D, "Insert"), (0x2E, "Delete"), (0x08, "Back"), (0x0D, "Enter"),
            (0x41, "A"), (0x5A, "Z"), (0x30, "D0"), (0x31, "D1"), (0x39, "D9"),
            (0x60, "NumPad0"), (0x69, "NumPad9"), (0x6A, "Multiply"), (0x6B, "Add"), (0x6D, "Subtract"), (0x6E, "Decimal"),
            (0x6F, "Divide"), (0x14, "CapsLock"), (0x2C, "PrintScreen"), (0x13, "Pause"), (0x90, "NumLock"), (0x91, "Scroll"),
            (0x5D, "Apps"), (0xBA, "OemSemicolon"), (0xBB, "OemPlus"), (0xBC, "OemComma"), (0xBD, "OemMinus"), (0xBE, "OemPeriod"),
            (0xBF, "OemQuestion"), (0xC0, "OemTilde"), (0xDB, "OemOpenBrackets"), (0xDC, "OemPipe"), (0xDD, "OemCloseBrackets"),
            (0xDE, "OemQuotes"), (0xE2, "OemBackslash"),
        ];
        foreach (var key in keys)
        {
            yield return key;
        }

        for (uint f = 1; f <= 24; f++)
        {
            yield return (0x6F + f, "F" + f);
        }
    }

    [Fact]
    public void APressedKey_IsWrittenAsItsCanonicalName_ThroughTheBoxSubclass()
    {
        var wrong = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Next));
            foreach (var (virtualKey, expected) in CapturedKeys())
            {
                SetText(d, PageOf(d).Cell(nameof(ShortcutMappings.Next)).Box, string.Empty);
                Press(box, virtualKey);
                var actual = BoxText(d, nameof(ShortcutMappings.Next));
                if (actual != expected)
                {
                    wrong.Add($"0x{virtualKey:X2}: expected \"{expected}\", got \"{actual}\"");
                }
            }

            d.Close();
        });
        Assert.Empty(wrong);
    }

    [Fact]
    public void APressedKey_IsWrittenAsItsCanonicalName_ThroughTheDialogLoopFilter()
    {
        var wrong = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Previous));
            User32.SetFocus(box);
            foreach (var (virtualKey, expected) in CapturedKeys())
            {
                SetText(d, PageOf(d).Cell(nameof(ShortcutMappings.Previous)).Box, string.Empty);
                if (!FilterKey(d, box, virtualKey))
                {
                    wrong.Add($"0x{virtualKey:X2}: the filter must take the key before IsDialogMessage sees it");
                }

                var actual = BoxText(d, nameof(ShortcutMappings.Previous));
                if (actual != expected)
                {
                    wrong.Add($"0x{virtualKey:X2}: expected \"{expected}\", got \"{actual}\"");
                }
            }

            Assert.False(d.IsClosed, "Enter/Space/arrows never reach the dialog manager from a key box");
            Assert.False(d.Saved);
            d.Close();
        });
        Assert.Empty(wrong);
    }
    [Fact]
    public void EveryCapturedKey_RoundTripsThroughTheShortcutParser_ToTheSameKey()
    {
        var captured = 0;
        for (var vk = 1; vk < 256; vk++)
        {
            var key = KeyIdMapping.FromVirtualKey(vk, isExtended: false);
            var text = SettingsPresenter.CaptureShortcut(key, key.ToString());
            if (text is null)
            {
                continue;
            }

            captured++;
            Assert.True(ShortcutKeyName.TryParse(text, out var parsed), $"VK 0x{vk:X2} -> \"{text}\" must be a valid shortcut name");
            Assert.Equal(key, parsed);
        }

        Assert.True(captured > 150, $"only {captured} keys are capturable");
    }

    [Fact]
    public void NumpadEnter_IsEnter_LikeTheMainEnterKey()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Next));
            Press(box, VkReturn, extended: true);
            Assert.Equal("Enter", BoxText(d, nameof(ShortcutMappings.Next)));
            d.Close();
        });
    }

    [Fact]
    public void AltPlusKey_ThroughTheBoxSubclass_IsCapturedToo()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Fullscreen));
            Press(box, 0x79, system: true); // F10
            Assert.Equal("F10", BoxText(d, nameof(ShortcutMappings.Fullscreen)));
            d.Close();
        });
    }

    [Fact]
    public void AltPlusKey_CapturesTheKey_AndDoesNotCloseOrOpenAnything()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Fullscreen));
            User32.SetFocus(box);
            Assert.True(LoopKey(d, box, 0x73, system: true), "Alt+F4 / F10 arrive as WM_SYSKEYDOWN: swallowed like the WPF Key.System path");
            Assert.Equal("F4", BoxText(d, nameof(ShortcutMappings.Fullscreen)));
            Assert.False(d.IsClosed);
            d.Close();
        });
    }

    [Fact]
    public void ModifierKeys_AreNeverCaptured_AndACombinationRecordsOnlyTheKey()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Next));
            var before = BoxText(d, nameof(ShortcutMappings.Next));
            foreach (uint modifier in new uint[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            {
                Press(box, modifier);
                Assert.False(FilterKey(d, box, modifier), $"modifier 0x{modifier:X2} stays with the dialog");
                Assert.Equal(before, BoxText(d, nameof(ShortcutMappings.Next)));
            }

            // Ctrl, Shift and Alt held, then A: the shortcut string is a single key name (as in WPF), never "Ctrl+Shift+Alt+A".
            Press(box, 0x11);
            Press(box, 0x10);
            Press(box, 0x12);
            Press(box, 0x41);
            Assert.Equal("A", BoxText(d, nameof(ShortcutMappings.Next)));
            d.Close();
        });
    }

    [Fact]
    public void TabAndEscape_AreNotCaptured_TabMovesFocus_EscapeCancelsTheDialog()
    {
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Next));
            User32.SetFocus(box);
            var before = BoxText(d, nameof(ShortcutMappings.Next));
            Assert.False(FilterKey(d, box, VkTab));
            Assert.False(FilterKey(d, box, VkEscape));
            Assert.Equal(before, BoxText(d, nameof(ShortcutMappings.Next)));

            Assert.True(LoopKey(d, box, VkTab), "Tab goes to the dialog manager");
            Assert.NotEqual(box, User32.GetFocus());
            Assert.Equal(H(d, PageOf(d).Cell(nameof(ShortcutMappings.Previous)).Box), User32.GetFocus());

            User32.SetFocus(box);
            Assert.True(LoopKey(d, box, VkEscape));
            Drain(d);
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.False(dialog.Saved);
    }

    [Fact]
    public void Enter_InAKeyBox_IsTheKey_NotTheDefaultButton()
    {
        var (_, dialog) = Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Skip));
            User32.SetFocus(box);
            Assert.True(LoopKey(d, box, VkReturn));
            Drain(d);
            Assert.False(d.IsClosed, "Enter did not press Save");
            Assert.Equal("Enter", BoxText(d, nameof(ShortcutMappings.Skip)));
            d.Close();
        });
        Assert.False(dialog.Saved);
    }

    [Fact]
    public void CharactersAndImeMessages_NeverReachTheBox()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var box = Box(d, nameof(ShortcutMappings.Next));
            var before = BoxText(d, nameof(ShortcutMappings.Next));
            foreach (var (message, wParam) in new (uint, nint)[]
                     {
                         (WindowMessages.WmChar, 'x'), (WindowMessages.WmChar, '\u1ebf'), (WindowMessages.WmSysChar, 'y'), (0x0103, 'a'),
                         (0x0286, '\u1ea1'), (0x010D, 0), (0x010F, 0), (0x0302, 0), (0x0300, 0), (0x0303, 0), (0x0304, 0),
                     })
            {
                User32.SendMessage(box, message, wParam, 0);
                Assert.Equal(before, BoxText(d, nameof(ShortcutMappings.Next)));
            }

            // VK_PROCESSKEY without a real key behind it (a composing IME): nothing is written.
            Press(box, 0xE5);
            Assert.Equal(before, BoxText(d, nameof(ShortcutMappings.Next)));
            d.Close();
        });
    }

    // ---- xoá / khôi phục ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ClearButton_EmptiesAnOptionalBox_AndSaveAcceptsIt()
    {
        AppSettings? saved = null;
        Show(new AppSettings(), (d, _) =>
        {
            var cell = PageOf(d).Cell(nameof(ShortcutMappings.LastImage));
            Assert.Equal("End", BoxText(d, nameof(ShortcutMappings.LastImage)));
            Command(d, cell.Clear);
            Assert.Equal(string.Empty, BoxText(d, nameof(ShortcutMappings.LastImage)));
            Assert.Equal(string.Empty, d.CollectAll().ShortcutTexts[nameof(ShortcutMappings.LastImage)]);
            Assert.True(d.TrySaveAll(), "an optional shortcut may be empty (feature disabled)");
            saved = d.Settings;
            d.Close();
        });

        Assert.Equal(string.Empty, saved!.Shortcuts.LastImage);
    }

    [Fact]
    public void EveryClearButton_EmptiesOnlyItsOwnBox()
    {
        Show(new AppSettings(), (d, _) =>
        {
            foreach (var row in ShortcutsSettingsPage.AllRows.Where(r => r.ClearTip is not null))
            {
                var before = d.CollectAll().ShortcutTexts.ToDictionary(p => p.Key, p => p.Value);
                Command(d, PageOf(d).Cell(row.Property).Clear);
                var after = d.CollectAll().ShortcutTexts;
                Assert.Equal(string.Empty, after[row.Property]);
                foreach (var (property, value) in before.Where(p => p.Key != row.Property))
                {
                    Assert.Equal(value, after[property]);
                }
            }

            d.Close();
        });
    }

    [Fact]
    public void AMandatoryBoxEmptied_IsRefusedAtSave_WithTheInvalidShortcutMessage()
    {
        var warnings = new FakeWarnings();
        var (_, dialog) = Show(new AppSettings(), (d, _) =>
        {
            SetText(d, PageOf(d).Cell(nameof(ShortcutMappings.Next)).Box, string.Empty);
            Assert.False(d.TrySaveAll());
            Assert.False(d.IsClosed);
            d.Close();
        }, Options(warnings, new FakeFiles()));

        Assert.False(dialog.Saved);
        Assert.Contains(warnings.Shown, w => w.Message == Tr.DialogSettingsInvalidShortcuts);
    }

    [Fact]
    public void ResetKeys_PutsEveryBoxBackToItsDefault_AndLeavesTheActionsAlone()
    {
        var keys = DistinctKeys();
        Show(WithKeys(keys), (d, _) =>
        {
            var actionsBefore = d.ActionsJson;
            Command(d, PageOf(d).Ids.ResetKeys);
            var defaults = ShortcutMappings.Default();
            foreach (var row in ShortcutsSettingsPage.AllRows)
            {
                Assert.Equal(GetShortcut(defaults, row.Property), BoxText(d, row.Property));
            }

            Assert.Equal(actionsBefore, d.ActionsJson);
            Assert.True(d.TrySaveAll());
            Assert.Equal(defaults.Next, d.Settings.Shortcuts.Next);
            Assert.Equal(defaults.ToggleCaptureMember, d.Settings.Shortcuts.ToggleCaptureMember);
            d.Close();
        });
    }

    [Fact]
    public void FrameButtons_DefaultsAndResetPage_RestoreTheKeys_OfTheShortcutsPage()
    {
        var keys = DistinctKeys();
        Show(WithKeys(keys), (d, _) =>
        {
            Assert.Equal("A", BoxText(d, nameof(ShortcutMappings.Next)));
            d.ResetActivePage();
            Assert.Equal("Right", BoxText(d, nameof(ShortcutMappings.Next)));
            Assert.Equal("End", BoxText(d, nameof(ShortcutMappings.LastImage)));

            Press(Box(d, nameof(ShortcutMappings.Next)), 0x42);
            Assert.Equal("B", BoxText(d, nameof(ShortcutMappings.Next)));
            Command(d, SettingsDialog.DefaultsId);
            Assert.Equal("Right", BoxText(d, nameof(ShortcutMappings.Next))); // "Defaults" = the built-in keys, not the opened/typed ones
            Assert.Equal("Left", BoxText(d, nameof(ShortcutMappings.Previous)));
            d.Close();
        });
    }

    // ---- xung đột ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TwoBoxesWithTheSameKey_ShowAWarning_AndAMarkOnBothLabels_UntilFixed()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            var warning = page.Ids.Warning;
            Assert.Equal(string.Empty, Txt(d, warning));
            Assert.False(Visible(d, warning));
            Assert.Empty(page.MarkedProperties);

            Press(Box(d, nameof(ShortcutMappings.Previous)), 0x27); // Right = Next's key
            Assert.NotEqual(string.Empty, Txt(d, warning));
            Assert.True(Visible(d, warning));
            Assert.Contains("Right", Txt(d, warning));
            Assert.Equal(new[] { nameof(ShortcutMappings.Next), nameof(ShortcutMappings.Previous) }, page.MarkedProperties.Order(StringComparer.Ordinal));
            Assert.StartsWith("\u26A0 ", Txt(d, page.Cell(nameof(ShortcutMappings.Next)).Label));
            Assert.StartsWith("\u26A0 ", Txt(d, page.Cell(nameof(ShortcutMappings.Previous)).Label));
            Assert.Equal(Tr.SettingsShortcutFirstImage, Txt(d, page.Cell(nameof(ShortcutMappings.FirstImage)).Label));

            Press(Box(d, nameof(ShortcutMappings.Previous)), 0x25); // Left again
            Assert.Equal(string.Empty, Txt(d, warning));
            Assert.False(Visible(d, warning));
            Assert.Empty(page.MarkedProperties);
            Assert.Equal(Tr.SettingsShortcutNext, Txt(d, page.Cell(nameof(ShortcutMappings.Next)).Label));
            d.Close();
        });
    }

    [Fact]
    public void AnOptionalKeyEqualToAMandatoryOne_IsAConflict_AndSaveIsRefused()
    {
        var warnings = new FakeWarnings();
        var (_, dialog) = Show(new AppSettings(), (d, _) =>
        {
            Press(Box(d, nameof(ShortcutMappings.LastImage)), 0x27); // Right = Next
            Assert.Contains(nameof(ShortcutMappings.LastImage), PageOf(d).MarkedProperties);
            Assert.False(d.TrySaveAll());
            Assert.False(d.IsClosed);
            d.Close();
        }, Options(warnings, new FakeFiles()));

        Assert.False(dialog.Saved);
        Assert.Single(warnings.Shown);
        Assert.Equal(Tr.DialogSettingsInvalidTitle, warnings.Shown[0].Title);
    }

    [Fact]
    public void TwoOptionalBoxesWithTheSameKey_AreAConflict_ButTwoEmptyOnesAreNot()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            Command(d, page.Cell(nameof(ShortcutMappings.Refresh)).Clear);
            Command(d, page.Cell(nameof(ShortcutMappings.CustomZoom)).Clear);
            Assert.Empty(page.MarkedProperties);
            Assert.Equal(string.Empty, Txt(d, page.Ids.Warning));

            Press(Box(d, nameof(ShortcutMappings.Refresh)), 0x51);
            Press(Box(d, nameof(ShortcutMappings.CustomZoom)), 0x51);
            Assert.Equal(new[] { nameof(ShortcutMappings.CustomZoom), nameof(ShortcutMappings.Refresh) },
                page.MarkedProperties.Order(StringComparer.Ordinal));
            d.Close();
        });
    }

    [Fact]
    public void AnAliasSpelling_CountsAsTheSameKey()
    {
        var problems = ShortcutsSettingsPage.FindProblems(new Dictionary<string, string>
        {
            [nameof(ShortcutMappings.Next)] = "Return",
            [nameof(ShortcutMappings.Previous)] = "enter",
            [nameof(ShortcutMappings.FirstImage)] = "PageUp",
            [nameof(ShortcutMappings.NextFolder)] = "Prior",
            [nameof(ShortcutMappings.Skip)] = "Space",
            [nameof(ShortcutMappings.Compare)] = "c",
            [nameof(ShortcutMappings.Undo)] = "C",
        });
        Assert.Equal(new[] { nameof(ShortcutMappings.FirstImage), nameof(ShortcutMappings.NextFolder), nameof(ShortcutMappings.Next),
            nameof(ShortcutMappings.Previous), nameof(ShortcutMappings.Compare), nameof(ShortcutMappings.Undo) }.Order(StringComparer.Ordinal),
            problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FindProblems_FlagsEmptyMandatory_AndInvalidNames_ButNotEmptyOptional()
    {
        var problems = ShortcutsSettingsPage.FindProblems(new Dictionary<string, string>
        {
            [nameof(ShortcutMappings.Next)] = string.Empty,
            [nameof(ShortcutMappings.Previous)] = "NotAKey",
            [nameof(ShortcutMappings.LastImage)] = string.Empty,
            [nameof(ShortcutMappings.Refresh)] = "Tab",
            [nameof(ShortcutMappings.Skip)] = "  Space ",
        });
        Assert.Equal(new[] { nameof(ShortcutMappings.Next), nameof(ShortcutMappings.Previous), nameof(ShortcutMappings.Refresh) }
            .Order(StringComparer.Ordinal), problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AKeyEqualToAnActionShortcut_ShowsTheLiveWarning()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            Assert.Equal(string.Empty, Txt(d, page.Ids.Warning));
            Press(Box(d, nameof(ShortcutMappings.Skip)), 0x72); // F3 = the default "Group-3" action
            Assert.NotEqual(string.Empty, Txt(d, page.Ids.Warning));
            Assert.False(d.TrySaveAll());
            d.Close();
        });
    }

    // ---- Action profiles lồng --------------------------------------------------------------------------------------------------

    private static SettingsDialogOptions WithEditor(Func<nint, IReadOnlyList<ReviewAction>, IReadOnlyList<ReviewAction>?>? editor,
        FakeWarnings? warnings = null)
        => Options(warnings ?? new FakeWarnings(), new FakeFiles()) with { ShowActionProfiles = editor };

    [Fact]
    public void EditActionProfiles_OpensTheEditorOverTheSettingsWindow_AndAppliedActionsAreSaved()
    {
        using var temp = new TempRoot("settings-keys-actions");
        var store = NewStore(temp.Path);
        IReadOnlyList<ReviewAction>? shown = null;
        nint shownOwner = 0;
        var (_, dialog) = Show(new AppSettings(), (d, _) =>
        {
            Command(d, PageOf(d).Ids.EditActions);
            Assert.NotNull(shown);
            Assert.Equal(d.Hwnd, shownOwner);
            Assert.Contains("Keep", d.ActionsJson, StringComparison.Ordinal);
            Assert.True(d.TrySaveAll());
            d.Close();
        }, WithEditor((owner, actions) =>
        {
            shownOwner = owner;
            shown = actions;
            return [new ReviewAction { Name = "Keep", Shortcut = "F8", Destination = "Keep" }];
        }), store);

        Assert.Equal(ReviewAction.Defaults().Count, shown!.Count);
        Assert.True(dialog.Saved);
        var action = Assert.Single(store.Current.Actions);
        Assert.Equal("Keep", action.Name);
        Assert.Equal("F8", action.Shortcut);
    }

    [Fact]
    public void ImportingASettingsFile_ReplacesTheKeysAndTheSharedActionsJson()
    {
        using var temp = new TempRoot("settings-keys-import");
        var imported = new AppSettings { Actions = [new ReviewAction { Name = "FromFile", Shortcut = "F9", Destination = "FromFile" }] };
        imported.Shortcuts.Next = "N";
        var path = Path.Combine(temp.Path, "imported.json");
        Assert.Null(new SettingsPresenter(imported).ExportSettingsTo(path));

        Show(new AppSettings(), (d, _) =>
        {
            Assert.DoesNotContain("FromFile", d.ActionsJson, StringComparison.Ordinal);
            d.ImportSettingsFrom(path);
            Assert.Equal("N", BoxText(d, nameof(ShortcutMappings.Next)));
            Assert.Contains("FromFile", d.ActionsJson, StringComparison.Ordinal);
            Assert.Contains("FromFile", d.CollectAll().ActionsJson, StringComparison.Ordinal);
            d.Close();
        });
    }

    [Fact]
    public void CancellingTheEditor_ChangesNothing()
    {
        var calls = 0;
        Show(new AppSettings(), (d, _) =>
        {
            var before = d.ActionsJson;
            Command(d, PageOf(d).Ids.EditActions);
            Assert.Equal(1, calls);
            Assert.Equal(before, d.ActionsJson);
            d.Close();
        }, WithEditor((_, _) =>
        {
            calls++;
            return null;
        }));
    }

    [Fact]
    public void AnEditedActionWithAKeyThatIsAlreadyBound_ShowsTheLiveWarning()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            Assert.Equal(string.Empty, Txt(d, page.Ids.Warning));
            Command(d, page.Ids.EditActions);
            Assert.NotEqual(string.Empty, Txt(d, page.Ids.Warning));
            Assert.False(d.TrySaveAll());
            d.Close();
        }, WithEditor((_, _) => [new ReviewAction { Name = "Clash", Shortcut = "Right", Destination = "Clash" }]));
    }

    [Fact]
    public void WhenTheEditorCannotBeOpened_TheUserIsToldAndNothingChanges()
    {
        foreach (var editor in new Func<nint, IReadOnlyList<ReviewAction>, IReadOnlyList<ReviewAction>?>?[]
                 {
                     null,
                     (_, _) => throw new InvalidOperationException("boom"),
                 })
        {
            var warnings = new FakeWarnings();
            Show(new AppSettings(), (d, _) =>
            {
                var before = d.ActionsJson;
                Command(d, PageOf(d).Ids.EditActions);
                Assert.Equal(before, d.ActionsJson);
                Assert.Single(warnings.Shown);
                Assert.Equal(Tr.DialogOpenEditorFailedTitle, warnings.Shown[0].Title);
                Assert.Equal(Tr.DialogOpenEditorFailedMessage, warnings.Shown[0].Message);
                d.Close();
            }, WithEditor(editor, warnings));
        }
    }

    [Fact]
    public void TheNestedEditor_OpensAsAModalDialogOverSettings_ThroughThePort()
    {
        using var temp = new TempRoot("settings-keys-port");
        var store = NewStore(temp.Path);
        var sawEditor = false;
        var result = OnSta(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            using var provider = services.BuildServiceProvider();
            var owner = CreateOwner();
            SettingsDialog? settings = null;
            using var port = new Win32SecondaryWindowPort(provider, ready: d =>
            {
                if (d is SettingsDialog sd)
                {
                    settings = sd;
                    Assert.Equal("Shortcuts", sd.ActivePageKey);
                    Command(sd, PageOf(sd).Ids.EditActions); // runs the nested modal loop; `ready` is called again for the editor
                    Assert.True(sawEditor);
                    Assert.True(User32.IsWindowEnabled(sd.Hwnd), "the editor closed: Settings is enabled again");
                    sd.Close();
                    return;
                }

                sawEditor = true;
                Assert.IsType<ActionProfilesDialog>(d);
                Assert.False(User32.IsWindowEnabled(settings!.Hwnd), "the editor is modal over Settings");
                Assert.Equal(settings.Hwnd, (nint)User32.GetWindow(d.Hwnd, 4 /* GW_OWNER */));
                d.Close();
            }, settings: Options(new FakeWarnings(), new FakeFiles()), settingsEnabled: () => true);
            var saved = port.ShowSettings(owner, SettingsTarget.Default);
            DestroyOwner(owner);
            return saved;
        });

        Assert.False(result);
        Assert.True(sawEditor);
    }

    // ---- khung / bố cục ---------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> LayoutProblems(SettingsDialog d, string label)
    {
        var page = PageOf(d);
        var width = Width(ClientRect(H(d, SettingsDialog.ViewportId(d.ActivePageIndex))));
        foreach (var row in ShortcutsSettingsPage.AllRows)
        {
            var cell = page.Cell(row.Property);
            var labelRect = RectInViewport(d, cell.Label);
            var boxRect = RectInViewport(d, cell.Box);
            if (labelRect.Left < 0 || boxRect.Right > width + 1)
            {
                yield return $"{label}: {row.Property} leaves the page horizontally";
            }

            if (labelRect.Right > boxRect.Left + 1 && labelRect.Top < boxRect.Bottom && boxRect.Top < labelRect.Bottom)
            {
                yield return $"{label}: {row.Property} label overlaps its box";
            }

            if (Width(boxRect) < 20 || Height(boxRect) < 10)
            {
                yield return $"{label}: {row.Property} box is {Width(boxRect)}x{Height(boxRect)}";
            }

            if (Height(labelRect) < NeededTextHeight(H(d, cell.Label)) - 1)
            {
                yield return $"{label}: {row.Property} label is cut";
            }

            if (cell.Clear != 0)
            {
                var clear = RectInViewport(d, cell.Clear);
                if (clear.Left < boxRect.Right - 1 || clear.Right > width + 1)
                {
                    yield return $"{label}: {row.Property} clear button misplaced";
                }
            }
        }

        var all = ShortcutsSettingsPage.AllRows.Select(r => page.Cell(r.Property)).ToList();
        var labels = all.Select(cell => RectInViewport(d, cell.Label)).ToList();
        // A row is as tall as its (wrapped) label: the next label of the same column starts below this one with a gap.
        for (var i = 0; i < all.Count; i++)
        {
            var box = RectInViewport(d, all[i].Box);
            for (var j = i + 1; j < all.Count; j++)
            {
                var other = RectInViewport(d, all[j].Box);
                if (other.Left == box.Left && other.Top > box.Top)
                {
                    if (labels[j].Top < labels[i].Bottom + 3)
                    {
                        yield return $"{label}: label {j} is closer than 3 px below label {i}";
                    }

                    break;
                }
            }
        }

        for (var i = 0; i < all.Count; i++)
        {
            for (var j = 0; j < all.Count; j++)
            {
                var a = labels[i];
                var b = RectInViewport(d, all[j].Box);
                if (a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                {
                    yield return $"{label}: label {i} overlaps box {j}";
                }

                if (j > i)
                {
                    var c = labels[j];
                    if (a.Left < c.Right && c.Left < a.Right && a.Top < c.Bottom && c.Top < a.Bottom)
                    {
                        yield return $"{label}: labels {i} and {j} overlap";
                    }
                }
            }
        }

        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                var a = RectInViewport(d, all[i].Box);
                var b = RectInViewport(d, all[j].Box);
                if (a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                {
                    yield return $"{label}: boxes {i} and {j} overlap";
                }
            }
        }
    }

    [Theory]
    [InlineData("en", 96u)]
    [InlineData("vi", 96u)]
    [InlineData("en", 144u)]
    [InlineData("vi", 144u)]
    public void TheGrid_DoesNotOverlapOrCutAnything_AtOneAndOneAndAHalfScale(string language, uint dpi)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var problems = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            if (dpi != 96)
            {
                ChangeDpi(d, dpi);
            }

            problems.AddRange(LayoutProblems(d, language + dpi));
            // a long conflict message must also fit (wrapped, not cut)
            Press(Box(d, nameof(ShortcutMappings.Previous)), 0x27);
            problems.AddRange(LayoutProblems(d, language + dpi + " warned"));
            Assert.True(d.ContentHeight(d.ActivePageIndex) > d.ViewportHeight, "27 rows scroll");
            d.Close();
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void ScrollingToTheBottom_ReachesTheActionProfilesButton_AndFocusScrollsTheBoxIntoView()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var index = d.ActivePageIndex;
            Assert.Equal(0, d.ScrollOffset(index));
            d.FocusControl(PageOf(d).Cell(nameof(ShortcutMappings.SendToRecycleBin)).Box);
            Assert.True(d.ScrollOffset(index) > 0, "the last row is below the fold");
            var rect = RectInViewport(d, PageOf(d).Cell(nameof(ShortcutMappings.SendToRecycleBin)).Box);
            Assert.True(rect.Top >= 0 && rect.Bottom <= d.ViewportHeight + 1, $"{rect.Top}..{rect.Bottom} in {d.ViewportHeight}");
            d.Close();
        });
    }

    [Fact]
    public void ChangingTheLanguage_RelabelsEveryRow_KeepsTheKeys_AndTheMarkers()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var page = PageOf(d);
            Press(Box(d, nameof(ShortcutMappings.Previous)), 0x27); // conflict with Next
            Press(Box(d, nameof(ShortcutMappings.Compare)), 0x71);
            var english = Txt(d, page.Cell(nameof(ShortcutMappings.Skip)).Label);
            var warningEnglish = Txt(d, page.Ids.Warning);

            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            foreach (var row in ShortcutsSettingsPage.AllRows)
            {
                var expected = (page.MarkedProperties.Contains(row.Property) ? "\u26A0 " : string.Empty) + row.Label();
                Assert.Equal(expected, Txt(d, page.Cell(row.Property).Label));
            }

            Assert.NotEqual(english, Txt(d, page.Cell(nameof(ShortcutMappings.Skip)).Label));
            Assert.Equal(Tr.SettingsShortcutsResetKeys, Txt(d, page.Ids.ResetKeys));
            Assert.Equal("Right", BoxText(d, nameof(ShortcutMappings.Previous)));
            Assert.Equal("F2", BoxText(d, nameof(ShortcutMappings.Compare)));
            Assert.NotEqual(string.Empty, Txt(d, page.Ids.Warning));
            Assert.NotEqual(warningEnglish, Txt(d, page.Ids.Warning));
            Assert.Equal(new[] { nameof(ShortcutMappings.Next), nameof(ShortcutMappings.Previous) }, page.MarkedProperties.Order(StringComparer.Ordinal));
            Localizer.SetCurrent(TestLocalization.English);
            Drain(d);
            d.Close();
        });
    }

    [Fact]
    public void OpeningAndClosingWithKeysPressed_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            void One() => ShowOnOwner(NewDialog(), (d, _) =>
            {
                var sd = (SettingsDialog)d;
                sd.EnsureAllPagesCreated();
                GoTo(sd, IndexOf(sd));
                foreach (var row in ShortcutsSettingsPage.AllRows.Take(6))
                {
                    Press(Box(sd, row.Property), 0x41);
                }

                Command(sd, PageOf(sd).Ids.ResetKeys);
                d.Close();
            });

            One();
            var gdiBefore = GdiObjects();
            var userBefore = UserObjects();
            for (var i = 0; i < 5; i++)
            {
                One();
            }

            return ((int)GdiObjects() - (int)gdiBefore, (int)UserObjects() - (int)userBefore);
        });

        Assert.True(gdi <= 2, $"GDI objects grew by {gdi}");
        Assert.True(user <= 2, $"USER objects grew by {user}");
    }

    private static SettingsDialog NewDialog()
    {
        var dialog = new SettingsDialog(new AppSettings(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
        dialog.Presenter.ShellIntegration = new FakeShell();
        dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
        return dialog;
    }

    /// <summary>
    /// Thủ công: ảnh chụp trang Shortcuts, en + vi, 100 % + 150 %, đầu trang / cuối trang / có xung đột.
    /// <c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;thư mục&gt;</c>; không có biến thì bỏ qua (Category=Manual nên CI không chạy).
    /// </summary>
    [Fact]
    [Trait("Category", "Manual")]
    public void WriteSnapshots_OfTheShortcutsPage()
    {
        var directory = Environment.GetEnvironmentVariable("PHOTOREVIEW_DIALOG_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        foreach (var (code, localizer) in new[] { ("en", TestLocalization.English), ("vi", TestLocalization.Vietnamese) })
        {
            using var scope = TestLocalization.Use(localizer);
            OnSta(() =>
            {
                using var styles = ActivateVisualStyles();
                void Snap(string name, Action<SettingsDialog> before)
                {
                    var dialog = NewDialog();
                    ShowOnOwner(dialog, (d, _) =>
                    {
                        var sd = (SettingsDialog)d;
                        before(sd);
                        Drain(d);
                        PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogSnapshotTests.CaptureWindow(
                            Path.Combine(directory, $"settings-shortcuts-{name}-{code}.png"), d.Hwnd);
                        d.Close();
                    });
                }

                Snap("top", d => { });
                Snap("top-150", d => ChangeDpi(d, 144));
                Snap("conflict", d =>
                {
                    Press(Box(d, nameof(ShortcutMappings.Previous)), 0x27);
                    Press(Box(d, nameof(ShortcutMappings.LastImage)), 0x27);
                    User32.SetFocus(Box(d, nameof(ShortcutMappings.Previous)));
                });
                Snap("bottom", d => User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, ScrollConstants.SbBottom,
                    d.ControlHandle(SettingsDialog.ScrollId)));
                Snap("bottom-150", d =>
                {
                    ChangeDpi(d, 144);
                    User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, ScrollConstants.SbBottom, d.ControlHandle(SettingsDialog.ScrollId));
                });
                return 0;
            });
        }
    }

    [Fact]
    public void TheShortcutsOfAPageNeverOpened_AreSavedUnchanged_AndOwnedByTheFieldMap()
    {
        SettingsPresenter.RememberPage("General");
        var settings = new AppSettings();
        settings.Shortcuts.Next = "N";
        settings.Shortcuts.Previous = "P";
        var (_, dialog) = Show(settings, (d, _) =>
        {
            Assert.False(d.IsPageCreated(IndexOf(d)));
            Assert.True(d.TrySaveAll());
            d.Close();
        }, realizeAll: false);
        Assert.Equal("N", dialog.Settings.Shortcuts.Next);
        Assert.Equal("P", dialog.Settings.Shortcuts.Previous);
        Assert.Contains(nameof(AppSettings.Shortcuts), SettingsFieldMap.CoveredProperties);
        Assert.Contains(nameof(AppSettings.Actions), SettingsFieldMap.CoveredProperties);
    }
}
