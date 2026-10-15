using System.Runtime.InteropServices;
using PhotoReview.App.Menus;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// FEAT-RENAME (PR3): the Win32 name box of "Rename..." / F2. Behaviour on a REAL window (modal, STA thread; the user is simulated
/// with SendMessage / IsDialogMessage): name part pre-filled and selected, the extension fixed beside it, the verdict of
/// <see cref="RenameNamePolicy"/> shown in place while typing with OK disabled, Enter / Esc through the dialog manager. And the real
/// pixels (BitBlt from the window DC, never PrintWindow) of the box and of the right-click menu at 96 / 144 DPI in English and
/// Vietnamese: a surface that is one flat colour is a failure (lesson L1). The real policy is wired through
/// <see cref="RenamePromptPresenter"/> so the table below is the user's view of <c>RenameNamePolicy</c>.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class RenamePromptDialogTests(ITestOutputHelper output)
{
    private const string CurrentPath = @"C:\Photos\Trip\IMG_0042.JPG";

    private static RenamePromptRequest Request(Func<string, bool, bool>? taken = null) =>
        new RenamePromptPresenter(CurrentPath, typed => RenameNamePolicy.Check(typed, CurrentPath), taken).ToRequest();

    private static (int Result, RenamePromptDialog Dialog) Show(RenamePromptRequest request, Action<RenamePromptDialog> body)
        => OnSta(() =>
        {
            var dialog = new RenamePromptDialog(request);
            var result = ShowOnOwner(dialog, (d, _) => body((RenamePromptDialog)d));
            return (result, dialog);
        });

    // ---- behaviour ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Opens_WithTheStemSelected_TheExtensionFixedBesideIt_AndNothingToComplainAbout()
    {
        var (result, _) = Show(Request(), d =>
        {
            Assert.True(User32.IsWindowVisible(d.Hwnd));
            Assert.Equal(Tr.DialogRenameTitle, TextOf(d.Hwnd));
            Assert.Equal(Tr.DialogRenameLabel, TextOf(d, RenamePromptDialog.LabelId));
            Assert.Equal("IMG_0042", d.CurrentText); // no extension in the box
            Assert.Equal((0, "IMG_0042".Length), d.Selection); // the whole name part is selected
            Assert.Equal(".JPG", d.ExtensionText); // the extension is shown, not editable
            Assert.True(IsVisible(d, RenamePromptDialog.ExtensionId));
            Assert.Equal(Item(d, RenamePromptDialog.EditId), User32.GetFocus());
            Assert.Equal(Tr.DialogRenameOk, TextOf(d, RenamePromptDialog.RenameId));
            Assert.True(d.IsRenameEnabled); // the unchanged name is not an error
            Assert.Null(d.ErrorMessage);
            Assert.False(IsVisible(d, RenamePromptDialog.ErrorId));
            Command(d, NativeDialog.IdCancel);
        });

        Assert.Equal(NativeDialog.IdCancel, result);
    }

    [Fact]
    public void AFileWithoutAnExtension_ShowsNoExtensionLabelText()
    {
        var request = new RenamePromptRequest("README", string.Empty, (_, _) => null);

        Show(request, d =>
        {
            Assert.Equal("README", d.CurrentText);
            Assert.Equal(string.Empty, d.ExtensionText);
            Command(d, NativeDialog.IdCancel);
        });
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("   ", "Empty")]
    [InlineData("a?b", "InvalidCharacter")]
    [InlineData("a:b", "InvalidCharacter")]
    [InlineData("a*b", "InvalidCharacter")]
    [InlineData("a\"b", "InvalidCharacter")]
    [InlineData("a<b", "InvalidCharacter")]
    [InlineData("a>b", "InvalidCharacter")]
    [InlineData("a|b", "InvalidCharacter")]
    [InlineData("a/b", "InvalidCharacter")]
    [InlineData("a\\b", "InvalidCharacter")]
    [InlineData("name.", "TrailingDot")]
    [InlineData("CON", "ReservedName")]
    [InlineData("nul", "ReservedName")]
    [InlineData("COM1", "ReservedName")]
    [InlineData("lpt9", "ReservedName")]
    public void InvalidNames_AreRefusedInPlaceWhileTyping_OkIsDisabled_AndEnterDoesNothing(string typed, string kind)
    {
        var (result, dialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, typed);

            Assert.False(d.IsRenameEnabled, $"'{typed}' must disable OK");
            Assert.Equal(Message(kind), d.ErrorMessage);
            Assert.True(IsVisible(d, RenamePromptDialog.ErrorId));
            Assert.Equal(Message(kind), TextOf(d, RenamePromptDialog.ErrorId));
            // Enter reaches OK through the dialog manager but the button is disabled: the box stays open, nothing is returned.
            Key(d, Item(d, RenamePromptDialog.EditId), VkReturn);
            Drain(d);
            Assert.True(User32.IsWindow(d.Hwnd));
            Command(d, NativeDialog.IdOk); // even a direct OK command re-validates
            Assert.True(User32.IsWindow(d.Hwnd));
            Command(d, NativeDialog.IdCancel);
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Null(dialog.Value);
    }

    private static string Message(string kind) => kind switch
    {
        "Empty" => Tr.DialogRenameErrorEmpty,
        "InvalidCharacter" => Tr.DialogRenameErrorInvalidCharacter,
        "TrailingDot" => Tr.DialogRenameErrorTrailingDot,
        "ReservedName" => Tr.DialogRenameErrorReservedName,
        "TooLong" => Tr.DialogRenameErrorTooLong,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Fact]
    public void ATooLongName_IsRefused_AndTheErrorGoesWhenTheNameIsFixed()
    {
        var (result, dialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, new string('x', 256));
            Assert.False(d.IsRenameEnabled);
            Assert.Equal(Tr.DialogRenameErrorTooLong, d.ErrorMessage);

            SetText(d, RenamePromptDialog.EditId, new string('x', 200));
            Assert.True(d.IsRenameEnabled);
            Assert.Null(d.ErrorMessage);
            Assert.False(IsVisible(d, RenamePromptDialog.ErrorId));
            Command(d, NativeDialog.IdOk);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(new string('x', 200), dialog.Value);
    }

    [Theory]
    [InlineData("holiday 01")]
    [InlineData("a.v2")]
    [InlineData("Ảnh đẹp ★")]
    [InlineData("IMG_0042")]
    [InlineData("img_0042")] // a change of letter case only is allowed
    public void ValidNames_EnableOk_AndAreReturnedTrimmed(string typed)
    {
        var (result, dialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, "  " + typed + "  ");
            Assert.True(d.IsRenameEnabled);
            Assert.Null(d.ErrorMessage);
            Command(d, NativeDialog.IdOk);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal(typed, dialog.Value);
    }

    [Fact]
    public void TheExtensionIsKept_TheTypedTextIsOnlyTheNamePart()
    {
        // What the caller does with the returned text: the policy appends the ORIGINAL extension (and drops a typed copy of it).
        var (_, dialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, "sunset");
            Command(d, NativeDialog.IdOk);
        });

        Assert.Equal("sunset", dialog.Value);
        Assert.Equal("sunset.JPG", RenameNamePolicy.Check(dialog.Value, CurrentPath).NewFileName);
        Assert.Equal("sunset.JPG", RenameNamePolicy.Check("sunset.jpg", CurrentPath).NewFileName);
        Assert.Equal("a.v2.JPG", RenameNamePolicy.Check("a.v2", CurrentPath).NewFileName);
    }

    [Fact]
    public void ANameThatIsTaken_IsRefusedWhileTypingForTheListedOnes_AndOnTheConfirmingCallForTheOthers()
    {
        var checkedOnDisk = new List<string>();
        var request = Request((fileName, commit) =>
        {
            if (commit) checkedOnDisk.Add(fileName);
            return fileName.Equals("IMG_0001.JPG", StringComparison.OrdinalIgnoreCase)
                || (commit && fileName.Equals("late.JPG", StringComparison.OrdinalIgnoreCase));
        });

        var (result, dialog) = Show(request, d =>
        {
            SetText(d, RenamePromptDialog.EditId, "IMG_0001");
            Assert.Equal(Tr.DialogRenameErrorExists("IMG_0001.JPG"), d.ErrorMessage);
            Assert.False(d.IsRenameEnabled);
            Assert.Empty(checkedOnDisk); // typing never asks the disk

            SetText(d, RenamePromptDialog.EditId, "late");
            Assert.True(d.IsRenameEnabled); // only the confirming call can know
            Command(d, NativeDialog.IdOk);
            Assert.True(User32.IsWindow(d.Hwnd), "the box stays open on the confirming refusal");
            Assert.Equal(Tr.DialogRenameErrorExists("late.JPG"), d.ErrorMessage);
            Assert.False(d.IsRenameEnabled);
            Assert.Equal(["late.JPG"], checkedOnDisk);

            SetText(d, RenamePromptDialog.EditId, "later");
            Assert.True(d.IsRenameEnabled);
            Command(d, NativeDialog.IdOk);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal("later", dialog.Value);
    }

    [Fact]
    public void Enter_InTheBox_Renames_AndEscape_Cancels_ThroughTheDialogManager()
    {
        var (enter, enterDialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, "kept");
            Assert.True(Key(d, Item(d, RenamePromptDialog.EditId), VkReturn));
            Drain(d);
        });
        var (escape, escapeDialog) = Show(Request(), d =>
        {
            SetText(d, RenamePromptDialog.EditId, "kept");
            Assert.True(Key(d, Item(d, RenamePromptDialog.EditId), VkEscape));
            Drain(d);
        });

        Assert.Equal(NativeDialog.IdOk, enter);
        Assert.Equal("kept", enterDialog.Value);
        Assert.Equal(NativeDialog.IdCancel, escape);
        Assert.Null(escapeDialog.Value);
    }

    [Fact]
    public void TheErrorLineGrowsTheDialogAndShrinksItBack_NothingOverlapsOrLeavesTheClientArea()
    {
        Show(Request(), d =>
        {
            var before = ClientRect(d.Hwnd);
            SetText(d, RenamePromptDialog.EditId, "a?b");
            var withError = ClientRect(d.Hwnd);
            Assert.True(Height(withError) > Height(before), "the error line needs room");
            var edit = ChildRect(d, RenamePromptDialog.EditId);
            var error = ChildRect(d, RenamePromptDialog.ErrorId);
            var ok = ChildRect(d, RenamePromptDialog.RenameId);
            Assert.True(error.Top >= edit.Bottom, "the error is below the box");
            Assert.True(ok.Top >= error.Bottom, "the buttons are below the error");
            Assert.True(error.Right <= withError.Right && ok.Bottom <= withError.Bottom);
            Assert.True(NeededTextHeight(Item(d, RenamePromptDialog.ErrorId)) <= Height(error));

            SetText(d, RenamePromptDialog.EditId, "fine");
            Assert.Equal(Height(before), Height(ClientRect(d.Hwnd)));
            Command(d, NativeDialog.IdCancel);
        });
    }

    [Fact]
    public void TheExtensionSitsRightOfTheBox_OnTheSameRow()
    {
        Show(Request(), d =>
        {
            var edit = ChildRect(d, RenamePromptDialog.EditId);
            var extension = ChildRect(d, RenamePromptDialog.ExtensionId);
            Assert.True(extension.Left >= edit.Right, "extension is to the right of the box");
            Assert.True(extension.Top >= edit.Top && extension.Bottom <= edit.Bottom, "same row as the box");
            Assert.True(Width(extension) > 0 && Width(edit) > Width(extension));
            Command(d, NativeDialog.IdCancel);
        });
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
    public void Dialog_PaintsTheBoxTheExtensionTheErrorAndTheButtons(uint dpi, string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var measured = 0;
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new RenamePromptDialog(Request());
            ShowOnOwner(dialog, (d, _) =>
            {
                var dlg = (RenamePromptDialog)d;
                if (dpi != 96)
                {
                    SettingsDialogKit.ChangeDpi(dlg, dpi);
                }

                SetText(dlg, RenamePromptDialog.EditId, "a?b"); // shows the error line too
                // A window-DC BitBlt returns BLACK for the part that is off screen (small CI screen at 144 DPI): measure at the origin.
                User32.SetWindowPos(dlg.Hwnd, 0, 0, 0, 0, 0, WindowMessages.SwpNoSize | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
                Drain(dlg);
                var pixels = Capture(dlg.Hwnd, out var width, out var height);
                var origin = WindowRect(dlg.Hwnd);
                Dump($"rename-dialog-{language}-{dpi}", pixels, width, height);
                ExternalEditorsUiTests.AssertNotFlat(pixels, width, height, $"rename dialog {language} {dpi}");
                foreach (var (name, id) in new[]
                {
                    ("label", RenamePromptDialog.LabelId), ("edit", RenamePromptDialog.EditId), ("extension", RenamePromptDialog.ExtensionId),
                    ("error", RenamePromptDialog.ErrorId), ("cancel", RenamePromptDialog.CancelId),
                })
                {
                    var rect = WindowRect(Item(dlg, id));
                    var box = new Rect { Left = rect.Left - origin.Left, Top = rect.Top - origin.Top, Right = rect.Right - origin.Left, Bottom = rect.Bottom - origin.Top };
                    Assert.True(box.Right > box.Left && box.Bottom > box.Top, name + " has no area");
                    Assert.True(box.Right <= width && box.Bottom <= height, name + " is outside the dialog");
                    ExternalEditorsUiTests.AssertNotFlat(ExternalEditorsUiTests.Crop(pixels, width, box), box.Right - box.Left, box.Bottom - box.Top,
                        $"rename dialog {name} {language} {dpi}");
                    measured++;
                }

                // The text box itself shows its text: more than the border (ink inside the inner area).
                Command(dlg, NativeDialog.IdCancel);
            });
            return 0;
        });

        Assert.Equal(5, measured);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ContextMenu_RenameEntry_IsPaintedAndTakesARow(uint dpi, string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var with = Menu(hideRename: false);
        var without = Menu(hideRename: true);
        var top = ContextMenuModelBuilder.Build(Context(new AppSettings(), true))
            .Where(i => i.Kind != MenuItemKind.Separator).ToList();
        var index = top.FindIndex(i => i.Id == nameof(ContextMenuItemId.RenameFile));
        Assert.True(index >= 0, "the menu model has no Rename entry");
        Assert.Equal(Tr.MainMenuRenameFile, top[index].Text);
        Assert.Equal("F2", top[index].ShortcutText);

        var withShots = ExternalEditorsUiTests.ShowMenuAndCapture(with.Items, dpi, Enumerable.Repeat((nint)0x28, index + 1).ToList());
        var withoutShots = ExternalEditorsUiTests.ShowMenuAndCapture(without.Items, dpi, [0x28]);

        Assert.NotEmpty(withShots);
        Assert.NotEmpty(withoutShots);
        var menu = withShots[0];
        Dump($"rename-menu-{language}-{dpi}", menu.Pixels, menu.Width, menu.Height);
        ExternalEditorsUiTests.AssertNotFlat(menu.Pixels, menu.Width, menu.Height, $"menu with Rename {language} {dpi}");
        ExternalEditorsUiTests.AssertNotFlat(withoutShots[0].Pixels, withoutShots[0].Width, withoutShots[0].Height, $"menu without Rename {language} {dpi}");
        // One more row: the entry really takes room in the painted menu (not just in the model).
        var rowMin = (int)(14 * dpi / 96.0);
        Assert.True(menu.Height >= withoutShots[0].Height + rowMin,
            $"menu with Rename is {menu.Height}px high, without it {withoutShots[0].Height}px");
    }

    private static ContextMenuBuildContext Context(AppSettings settings, bool hasImage) =>
        new(settings, hasImage, CanUndo: true, IsFit: true, null, CanOpenInExternalEditor: true, CanCopyPath: hasImage, IsCompareVisible: false);

    private static (IReadOnlyList<MenuItemModel> Items, AppSettings Settings) Menu(bool hideRename)
    {
        // The default hidden set (no zoom cluster / recycle): a short menu fits any CI screen at 144 DPI.
        var settings = new AppSettings();
        if (hideRename)
        {
            ContextMenuItems.ApplyHidden(settings, [.. ContextMenuItems.DefaultHidden, ContextMenuItemId.RenameFile]);
        }

        return (ContextMenuModelBuilder.Build(Context(settings, true)), settings);
    }

    private static string? DumpDir => Environment.GetEnvironmentVariable("PHOTOREVIEW_RENAME_DUMP");

    private void Dump(string name, byte[] pixels, int width, int height)
    {
        if (DumpDir is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), NativeDialogSnapshotTests.EncodePng(pixels, width, height));
        output.WriteLine("saved " + name);
    }

    private static byte[] Capture(nint hwnd, out int width, out int height)
    {
        _ = RedrawWindow(hwnd, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        return ExternalEditorsUiTests.Capture(hwnd, out width, out height);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
}
