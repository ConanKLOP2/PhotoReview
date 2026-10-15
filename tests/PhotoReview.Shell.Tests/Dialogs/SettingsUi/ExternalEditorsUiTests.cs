using System.Reflection;
using System.Runtime.InteropServices;
using PhotoReview.App.Menus;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// FEAT-EXTERNAL-EDITORS (Win32 shell): the Files page group (manage button, summary, RAW switch), the editors list dialog, the name
/// prompt, the default-editor choice list and the right-click submenu -- behaviour through messages, and the real pixels (BitBlt from
/// the window DC, never PrintWindow) at 96/144 DPI in English and Vietnamese: a surface that is one flat colour is a failure
/// (lesson L1: a control that "exists" but paints nothing). Set <c>PHOTOREVIEW_EE_DUMP=&lt;dir&gt;</c> to also save the images.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class ExternalEditorsUiTests(ITestOutputHelper output)
{
    private const int FilesIndex = 5;
    private const uint BmClick = 0x00F5;

    private static AppSettings ThreeEditors() => new()
    {
        // Rename... (FEAT-RENAME) is not under test here: hidden, so the menu keeps the height this test was calibrated on (a taller
        // menu at 144 DPI on the small CI screen scrolls and the VK_DOWN steps no longer reach the editor entry).
        HiddenContextMenuItems = ["RenameFile"],
        ExternalEditors =
        [
            new ExternalEditor { Id = "lr", Name = "Lightroom", Path = @"C:\Adobe\Lightroom\lightroom.exe" },
            new ExternalEditor { Id = "tp", Name = "Topaz AI", Path = @"C:\Topaz\topaz.exe" },
            new ExternalEditor { Id = "dxo", Name = "DxO Lab", Path = @"C:\DxO\dxo.exe", Arguments = "--open {file}" },
        ],
        DefaultExternalEditorId = "tp",
        ExternalEditorPath = @"C:\Topaz\topaz.exe",
    };

    private static FilesSettingsPage FilesPage(SettingsDialog d) => (FilesSettingsPage)d.Pages[FilesIndex];

    private static void Click(SettingsDialog d, int id) => User32.SendMessage(H(d, id), BmClick, 0, 0);

    // ---- Files page --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FilesPage_ManageEditors_Apply_UpdatesThePathBoxTheSummaryAndTheSavedSettings()
    {
        AppSettings? saved = null;
        var shown = new List<(IReadOnlyList<ExternalEditor> Editors, string? DefaultId)>();
        var options = Options(new FakeWarnings(), new FakeFiles()) with
        {
            ShowExternalEditors = (_, editors, defaultId) =>
            {
                shown.Add((editors, defaultId));
                return new ExternalEditorsEdit([.. editors.Select(e => e.Clone()), new ExternalEditor { Id = "new", Name = "Capture One", Path = @"C:\CO\co.exe" }], "new");
            },
        };
        Show(ThreeEditors(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            Assert.Equal(@"C:\Topaz\topaz.exe", Txt(d, page.Ids.EditorPath));
            Assert.Contains("3", Txt(d, page.EditorIds.Summary), StringComparison.Ordinal);
            Assert.Contains("Topaz AI", Txt(d, page.EditorIds.Summary), StringComparison.Ordinal);

            Click(d, page.EditorIds.Manage);

            Assert.Equal("tp", Assert.Single(shown).DefaultId);
            Assert.Equal(3, shown[0].Editors.Count);
            Assert.Equal(@"C:\CO\co.exe", Txt(d, page.Ids.EditorPath));
            Assert.Contains("Capture One", Txt(d, page.EditorIds.Summary), StringComparison.Ordinal);
            Assert.True(d.TrySaveAll());
            saved = d.Settings;
            d.Close();
        }, options);

        Assert.NotNull(saved);
        Assert.Equal(["lr", "tp", "dxo", "new"], saved.ExternalEditors.Select(e => e.Id));
        Assert.Equal("new", saved.DefaultExternalEditorId);
        Assert.Equal(@"C:\CO\co.exe", saved.ExternalEditorPath);
    }

    [Fact]
    public void FilesPage_ManageEditors_Cancel_ChangesNothing_AndTheRawSwitchRoundTrips()
    {
        AppSettings? saved = null;
        var options = Options(new FakeWarnings(), new FakeFiles()) with { ShowExternalEditors = (_, _, _) => null };
        Show(ThreeEditors(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            Assert.True(Checked(d, page.EditorIds.SendRaw)); // default on
            Click(d, page.EditorIds.Manage);
            Tick(d, page.EditorIds.SendRaw, false);
            Assert.True(d.TrySaveAll());
            saved = d.Settings;
            d.Close();
        }, options);

        Assert.NotNull(saved);
        Assert.Equal(["lr", "tp", "dxo"], saved.ExternalEditors.Select(e => e.Id));
        Assert.Equal("tp", saved.DefaultExternalEditorId);
        Assert.False(saved.ExternalEditorSendsRawOriginal);
    }

    [Fact]
    public void FilesPage_NoEditorsDeclared_SummarySaysSo_AndTheDefaultPathBoxStillCreatesTheFirstEditor()
    {
        AppSettings? saved = null;
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            Assert.Equal(Tr.SettingsExternalEditorsSummaryNone, Txt(d, page.EditorIds.Summary));
            SetText(d, page.Ids.EditorPath, @"C:\Tools\Affinity\Photo.exe");
            Assert.True(d.TrySaveAll());
            saved = d.Settings;
            d.Close();
        });

        var editor = Assert.Single(saved!.ExternalEditors);
        Assert.Equal("Photo", editor.Name);
        Assert.Equal(editor.Id, saved.DefaultExternalEditorId);
    }

    // ---- the editors dialog ------------------------------------------------------------------------------------------------------

    [Fact]
    public void EditorsDialog_AddRemoveSetDefaultAndEdit_ProduceTheExpectedResult()
    {
        var files = new FakeFiles { OpenResult = @"C:\Apps\Canon DPP\dpp.exe" };
        var dialog = new ExternalEditorsDialog(ThreeEditors().ExternalEditors, "tp", files, new FakeWarnings(), _ => "Canon DPP");
        var result = OnSta(() => ShowOnOwner(dialog, (d, _) =>
        {
            Command(d, ExternalEditorsDialog.AddId); // picks dpp.exe, named by the suggestion, selected
            Assert.Equal("Canon DPP", TextOf(d, ExternalEditorsDialog.NameId));
            Assert.Equal(@"C:\Apps\Canon DPP\dpp.exe", TextOf(d, ExternalEditorsDialog.PathId));
            SetText(d, ExternalEditorsDialog.ArgumentsId, "-edit {file}");
            Command(d, ExternalEditorsDialog.SetDefaultId); // the new editor becomes the default
            Assert.Equal(Tr.ExternalEditorsDefaultBadge, ((ExternalEditorsDialog)d).RowText(3).Badge);
            Assert.Null(((ExternalEditorsDialog)d).RowText(1).Badge);

            ListBoxControl.SelectSingle(Item(d, ExternalEditorsDialog.ListId), 0); // Lightroom
            Command(d, ExternalEditorsDialog.ListId, ControlConstants.LbnSelChange);
            Command(d, ExternalEditorsDialog.RemoveId);
            Command(d, IdOkOf(d));
        }));

        Assert.Equal(NativeDialog.IdOk, result);
        var edit = dialog.Outcome;
        Assert.Equal(["tp", "dxo", edit.Editors[2].Id], edit.Editors.Select(e => e.Id));
        Assert.Equal("Canon DPP", edit.Editors[2].Name);
        Assert.Equal("-edit {file}", edit.Editors[2].Arguments);
        Assert.Equal(edit.Editors[2].Id, edit.DefaultId);
    }

    [Fact]
    public void EditorsDialog_RemovingTheDefault_MovesTheDefaultToTheFirstRemaining_AndABlankPathBlocksApply()
    {
        var warnings = new FakeWarnings();
        var dialog = new ExternalEditorsDialog(ThreeEditors().ExternalEditors, "lr", new FakeFiles(), warnings);
        var result = OnSta(() => ShowOnOwner(dialog, (d, _) =>
        {
            Command(d, ExternalEditorsDialog.RemoveId); // Lightroom (selected = the default) goes
            SetText(d, ExternalEditorsDialog.PathId, "   "); // the now selected editor loses its program
            Command(d, IdOkOf(d));
            Assert.Single(warnings.Shown); // refused: the dialog is still open
            SetText(d, ExternalEditorsDialog.PathId, @"C:\Topaz\topaz.exe");
            Command(d, IdOkOf(d));
        }));

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(["tp", "dxo"], dialog.Outcome.Editors.Select(e => e.Id));
        Assert.Equal("tp", dialog.Outcome.DefaultId);
    }

    [Fact]
    public void EditorsDialog_Cancel_ReturnsCancel()
    {
        var dialog = new ExternalEditorsDialog(ThreeEditors().ExternalEditors, "tp");

        var result = OnSta(() => ShowOnOwner(dialog, (d, _) => Command(d, NativeDialog.IdCancel)));

        Assert.Equal(NativeDialog.IdCancel, result);
    }

    private static int IdOkOf(NativeDialog dialog) => NativeDialog.IdOk;

    // ---- name prompt and default choice -----------------------------------------------------------------------------------------

    [Fact]
    public void TextPrompt_BlankIsRefused_ATrimmedNameIsReturned()
    {
        var dialog = new TextPromptDialog("t", "label", "  ");
        var result = OnSta(() => ShowOnOwner(dialog, (d, _) =>
        {
            Command(d, NativeDialog.IdOk);
            Assert.Null(dialog.Value);
            SetText(d, TextPromptDialog.EditId, "  Capture One  ");
            Command(d, NativeDialog.IdOk);
        }));

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal("Capture One", dialog.Value);
    }

    [Fact]
    public void ChoiceList_PreselectsTheCurrent_AndReportsTheChosenRow()
    {
        var content = ListDialogContent.ForChoice("Set default editor", "intro", ["Lightroom", "Topaz AI", "DxO Lab"], 1);
        var dialog = new ListDialog(content);
        var result = OnSta(() => ShowOnOwner(dialog, (d, _) =>
        {
            Assert.Equal(1, ListBoxControl.SelectedIndex(Item(d, ListDialog.ListId)));
            ListBoxControl.SelectSingle(Item(d, ListDialog.ListId), 2);
            Command(d, NativeDialog.IdOk);
        }));

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(2, dialog.SelectedIndex);
    }

    // ---- real pixels -------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Cases()
    {
        foreach (var dpi in new[] { 96u, 144u })
        {
            foreach (var language in new[] { "en", "vi" })
            {
                yield return [dpi, language];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Menu_EditorSubmenu_PaintsItsEntries(uint dpi, string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var items = ContextMenuModelBuilder.Build(new ContextMenuBuildContext(ThreeEditors(), HasImage: true, CanUndo: true, IsFit: true, null,
            CanOpenInExternalEditor: true, CanCopyPath: true, IsCompareVisible: false));
        var top = items.Where(i => i.Kind != MenuItemKind.Separator).ToList();
        var editorIndex = top.FindIndex(i => i.Id == nameof(ContextMenuItemId.ExternalEditor));
        Assert.True(editorIndex >= 0);
        var submenu = top[editorIndex];
        Assert.Equal(MenuItemKind.Submenu, submenu.Kind);
        Assert.Equal(3 + 1 + 2, submenu.Children.Count); // three editors, a separator, Add + Set default

        const nint VkDown = 0x28;
        const nint VkRight = 0x27;
        var steps = new List<nint>();
        for (var i = 0; i <= editorIndex; i++)
        {
            steps.Add(VkDown);
        }

        steps.Add(VkRight);
        steps.Add(VkDown);
        var captured = ShowMenuAndCapture(items, dpi, steps);

        Assert.True(captured.Count >= 2, $"expected the root menu and the submenu windows, found {captured.Count}");
        for (var level = 0; level < captured.Count; level++)
        {
            var (pixels, width, height) = captured[level];
            Dump($"menu-{language}-{dpi}-level{level}", pixels, width, height);
            AssertNotFlat(pixels, width, height, $"menu {language} {dpi} level {level}");
        }

        // The submenu is the right-most window and shows 5 entry rows + a separator: it must be taller than 4 lines of text.
        var sub = captured[^1];
        Assert.True(sub.Height > 4 * (int)(16 * dpi / 96.0), $"submenu window is {sub.Height}px high");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FilesPage_EditorGroup_PaintsTheManageButtonAndTheSummary(uint dpi, string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var measured = 0;
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new SettingsDialog(ThreeEditors(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                var dlg = (SettingsDialog)d;
                dlg.EnsureAllPagesCreated();
                if (dpi != 96)
                {
                    ChangeDpi(dlg, dpi);
                }

                GoTo(dlg, FilesIndex);
                var page = FilesPage(dlg);
                foreach (var id in new[] { page.EditorIds.Manage, page.EditorIds.Summary, page.EditorIds.SendRaw })
                {
                    dlg.EnsureVisible(id);
                    Drain(dlg);
                }

                var pixels = Capture(dlg.Hwnd, out var width, out var height);
                var origin = WindowRect(dlg.Hwnd);
                Dump($"files-{language}-{dpi}", pixels, width, height);
                foreach (var (name, id) in new[] { ("manage", page.EditorIds.Manage), ("summary", page.EditorIds.Summary), ("sendraw", page.EditorIds.SendRaw) })
                {
                    var rect = WindowRect(H(dlg, id));
                    Assert.True(User32.IsWindowVisible(H(dlg, id)), name + " is not visible");
                    var box = new Rect { Left = rect.Left - origin.Left, Top = rect.Top - origin.Top, Right = rect.Right - origin.Left, Bottom = rect.Bottom - origin.Top };
                    Assert.True(box.Right > box.Left && box.Bottom > box.Top, name + " has no area");
                    AssertNotFlat(Crop(pixels, width, box), box.Right - box.Left, box.Bottom - box.Top, $"files page {name} {language} {dpi}");
                    measured++;
                }

                d.Close();
            });
            return 0;
        });

        Assert.Equal(3, measured);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EditorsDialog_PaintsListAndDetails(uint dpi, string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var measured = 0;
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new ExternalEditorsDialog(ThreeEditors().ExternalEditors, "tp", new FakeFiles());
            ShowOnOwner(dialog, (d, _) =>
            {
                if (dpi != 96)
                {
                    ChangeDpi(d, dpi);
                }

                Drain(d);
                var pixels = Capture(d.Hwnd, out var width, out var height);
                var origin = WindowRect(d.Hwnd);
                Dump($"editors-{language}-{dpi}", pixels, width, height);
                foreach (var (name, id) in new[]
                {
                    ("list", ExternalEditorsDialog.ListId), ("name", ExternalEditorsDialog.NameId), ("path", ExternalEditorsDialog.PathId),
                    ("arguments", ExternalEditorsDialog.ArgumentsId), ("setdefault", ExternalEditorsDialog.SetDefaultId),
                })
                {
                    var rect = WindowRect(Item(d, id));
                    var box = new Rect { Left = rect.Left - origin.Left, Top = rect.Top - origin.Top, Right = rect.Right - origin.Left, Bottom = rect.Bottom - origin.Top };
                    Assert.True(box.Right > box.Left && box.Bottom > box.Top, name + " has no area");
                    AssertNotFlat(Crop(pixels, width, box), box.Right - box.Left, box.Bottom - box.Top, $"editors dialog {name} {language} {dpi}");
                    measured++;
                }

                Command(d, NativeDialog.IdCancel);
            });
            return 0;
        });

        Assert.Equal(5, measured);
    }

    private static void ChangeDpi(NativeDialog dialog, uint dpi) => SettingsDialogKit.ChangeDpi(dialog, dpi);

    private static string? DumpDir => Environment.GetEnvironmentVariable("PHOTOREVIEW_EE_DUMP");

    private void Dump(string name, byte[] pixels, int width, int height)
    {
        if (DumpDir is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), Native.NativeDialogSnapshotTests.EncodePng(pixels, width, height));
        output.WriteLine("saved " + name);
    }

    /// <summary>Not one flat colour: at least 3 distinct colours and 1 % of the pixels differ from the most common one.</summary>
    internal static void AssertNotFlat(byte[] bgra, int width, int height, string what)
    {
        var counts = new Dictionary<int, int>();
        for (var i = 0; i + 3 < bgra.Length && i / 4 < width * height; i += 4)
        {
            var color = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
            counts[color] = counts.GetValueOrDefault(color) + 1;
        }

        var total = Math.Max(1, width * height);
        var dominant = counts.Values.Max();
        Assert.True(counts.Count >= 3 && dominant < total * 0.99, $"{what}: {counts.Count} colours, dominant covers {dominant * 100.0 / total:0.0} %");
    }

    internal static byte[] Crop(byte[] bgra, int width, Rect box)
    {
        var w = box.Right - box.Left;
        var h = box.Bottom - box.Top;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(bgra, (((box.Top + y) * width) + box.Left) * 4, result, y * w * 4, w * 4);
        }

        return result;
    }

    internal static byte[] Capture(nint hwnd, out int width, out int height)
    {
        Rect rect = default;
        User32.GetWindowRect(hwnd, &rect);
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        _ = RedrawWindow(hwnd, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        var source = GetWindowDC(hwnd);
        var memory = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            _ = BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x00CC0020 /* SRCCOPY */);
            var info = new byte[40];
            BitConverter.GetBytes(40).CopyTo(info, 0);
            BitConverter.GetBytes(width).CopyTo(info, 4);
            BitConverter.GetBytes(-height).CopyTo(info, 8);
            BitConverter.GetBytes((short)1).CopyTo(info, 12);
            BitConverter.GetBytes((short)32).CopyTo(info, 14);
            var pixels = new byte[width * height * 4];
            fixed (byte* p = pixels)
            fixed (byte* i = info)
            {
                SelectObject(memory, old);
                _ = GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
            }

            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(hwnd, source);
        }
    }

    /// <summary>Opens the owner-draw popup on a hidden owner, drives it with keys posted from a worker, captures every menu window.</summary>
    internal static List<(byte[] Pixels, int Width, int Height)> ShowMenuAndCapture(IReadOnlyList<MenuItemModel> items, uint dpi, IReadOnlyList<nint> keys)
    {
        return OnSta(() =>
        {
            var captured = new List<(byte[], int, int)>();
            var owner = CreateOwner();
            try
            {
                var worker = Task.Run(() =>
                {
                    var deadline = Environment.TickCount64 + 15000;
                    while (Environment.TickCount64 < deadline && MenuWindows().Count == 0)
                    {
                        Thread.Yield();
                    }

                    SpinFor(600); // first paint of the menu window (an unpainted window shows the shared screen surface)
                    foreach (var key in keys)
                    {
                        _ = User32.PostMessage(owner, WindowMessages.WmKeyDown, key, 0);
                        SpinFor(150);
                    }

                    SpinFor(500);
                    foreach (var menu in MenuWindows())
                    {
                        var pixels = Capture(menu, out var width, out var height);
                        captured.Add((pixels, width, height));
                    }

                    for (var i = 0; i < 4; i++)
                    {
                        _ = User32.PostMessage(owner, WindowMessages.WmKeyDown, 0x1B, 0);
                        SpinFor(100);
                    }
                });

                using var session = new OwnerDrawMenuSession(owner, dpi);
                var attached = session.Attach();
                var root = User32.CreatePopupMenu();
                var ids = new List<string>();
                var populate = typeof(NativePopupMenuHost).GetMethod("Populate", BindingFlags.NonPublic | BindingFlags.Static)!;
                populate.Invoke(null, [root, items, ids, null, attached ? session : null]);
                _ = User32.TrackPopupMenuEx(root, WindowMessages.TpmReturnCmd | WindowMessages.TpmRightButton, 150, 0, owner, 0); // y=0: a tall menu at 144 DPI must not be pushed/scrolled by a small (CI) screen
                User32.DestroyMenu(root);
                worker.Wait(TimeSpan.FromSeconds(20));
            }
            finally
            {
                DestroyOwner(owner);
            }

            return captured;
        });
    }

    private static void SpinFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Thread.Yield();
        }
    }

    private static List<nint> MenuWindows()
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            var buffer = stackalloc char[64];
            var length = GetClassName(hwnd, buffer, 64);
            if (new string(buffer, 0, length) == "#32768" && User32.IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        found.Sort((a, b) => WindowRect(a).Left.CompareTo(WindowRect(b).Left));
        return found;
    }

    private delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc proc, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hwnd, char* name, int max);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);
}

/// <summary>FEAT-EXTERNAL-EDITORS: the submenu ids reach the right window commands through <see cref="ContextMenuController"/>.</summary>
public sealed class ExternalEditorsControllerTests
{
    [Fact]
    public async Task Controller_DispatchesEditorEntries_AddAndSetDefault()
    {
        var actions = new PhotoReview.Shell.Tests.Menus.FakeContextMenuActions
        {
            Settings = new AppSettings
            {
                ExternalEditors = [new ExternalEditor { Id = "dxo", Path = @"C:\DxO\dxo.exe" }],
            },
        };
        var controller = new ContextMenuController(actions, new PhotoReview.Shell.Tests.Menus.FakePopupMenuHost());

        await controller.ExecuteAsync(ContextMenuController.ExternalEditorOpenPrefix + "dxo");
        await controller.ExecuteAsync(ContextMenuController.ExternalEditorAddId);
        await controller.ExecuteAsync(ContextMenuController.ExternalEditorSetDefaultId);
        await controller.ExecuteAsync(nameof(ContextMenuItemId.ExternalEditor));

        Assert.Equal(["ExternalEditor:dxo", "AddEditor", "SetDefaultEditor", "ExternalEditor"], actions.Log);
    }
}
