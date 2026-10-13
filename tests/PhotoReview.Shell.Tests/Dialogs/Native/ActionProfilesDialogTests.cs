using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ2: editor Action profiles (modal) trên HWND thật. Presenter (<see cref="ActionProfilesPresenter"/>) đã có test riêng; ở đây
/// kiểm VIEW: chép đúng giá trị vào/ra điều khiển, thứ tự "lưu hàng trước rồi mở hàng mới", Add/Remove/Import/Export/Apply, modal,
/// dialog manager (Enter/Esc), bố cục theo DPI và tiếng Việt.
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class ActionProfilesDialogTests
{
    private sealed class FakeFiles : IShellFileDialog
    {
        public string? OpenResult { get; set; }

        public string? SaveResult { get; set; }

        public List<string> Calls { get; } = [];

        public List<string> Filters { get; } = [];

        public string? PickOpenFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder)
        {
            Calls.Add("open:" + title);
            Filters.AddRange(filters.Select(f => f.Name + "|" + f.Pattern));
            return OpenResult;
        }

        public string? PickSaveFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? defaultExtension,
            string? suggestedFileName, string? initialFolder)
        {
            Calls.Add($"save:{title}:{defaultExtension}:{suggestedFileName}");
            Filters.AddRange(filters.Select(f => f.Name + "|" + f.Pattern));
            return SaveResult;
        }
    }

    private sealed class FakeWarnings : IDialogWarningSink
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public void ShowWarning(nint owner, string title, string message) => Shown.Add((title, message));
    }

    private static List<ReviewAction> Sample() =>
    [
        new() { Name = "Keep", Shortcut = "F6", Operation = FileOperationType.Move, Destination = "Keep", Confirm = false },
        new() { Name = "Copy out", Shortcut = "F7", Operation = FileOperationType.Copy, Destination = "Out", Confirm = true },
        new() { Name = "Trash", Shortcut = "F8", Operation = FileOperationType.Recycle, Destination = "x", Confirm = false },
    ];

    private static (int Result, ActionProfilesDialog Dialog) Show(IEnumerable<ReviewAction> actions, Action<ActionProfilesDialog, nint> ready,
        FakeFiles? files = null, FakeWarnings? warnings = null, INativeDialogChrome? chrome = null)
    {
        return OnSta(() =>
        {
            var dialog = new ActionProfilesDialog(actions, files, warnings, chrome);
            var result = ShowOnOwner(dialog, (d, owner) => ready((ActionProfilesDialog)d, owner));
            return (result, dialog);
        });
    }

    private static void Select(NativeDialog dialog, int index)
    {
        ListBoxControl.SelectSingle(Item(dialog, ActionProfilesDialog.ListId), index);
        Command(dialog, ActionProfilesDialog.ListId, ControlConstants.LbnSelChange);
    }

    private static nint ListOf(NativeDialog dialog) => Item(dialog, ActionProfilesDialog.ListId);

    private static string[] ListNames(NativeDialog dialog)
    {
        var list = ListOf(dialog);
        return Enumerable.Range(0, ListBoxControl.Count(list)).Select(i => ListBoxControl.TextAt(list, i)).ToArray();
    }

    private static string FieldText(NativeDialog dialog, int id) => TextOf(dialog, id);

    private static int OperationIndex(NativeDialog dialog) => ComboBoxControl.SelectedIndex(Item(dialog, ActionProfilesDialog.OperationId));

    private static bool Confirm(NativeDialog dialog) => ButtonStateControl.IsChecked(Item(dialog, ActionProfilesDialog.ConfirmId));

    // ---- mở + hiển thị ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Opens_WithTheListAndTheFirstRowsFields()
    {
        var snapshot = Show(Sample(), (d, _) =>
        {
            Assert.Equal(["Keep", "Copy out", "Trash"], ListNames(d));
            Assert.Equal(0, ListBoxControl.SelectedIndex(ListOf(d)));
            Assert.Equal("Keep", FieldText(d, ActionProfilesDialog.NameId));
            Assert.Equal("F6", FieldText(d, ActionProfilesDialog.ShortcutId));
            Assert.Equal("Keep", FieldText(d, ActionProfilesDialog.DestinationId));
            Assert.Equal(0, OperationIndex(d));
            Assert.False(Confirm(d));
            Assert.Equal(3, ComboBoxControl.Count(Item(d, ActionProfilesDialog.OperationId)));
            d.Close();
        });

        Assert.Equal(NativeDialog.IdCancel, snapshot.Result);
    }

    [Fact]
    public void EmptyList_ShowsEmptyFields_AndNothingIsSelected()
    {
        Show([], (d, _) =>
        {
            Assert.Empty(ListNames(d));
            Assert.Equal(-1, ListBoxControl.SelectedIndex(ListOf(d)));
            Assert.Equal(string.Empty, FieldText(d, ActionProfilesDialog.NameId));
            d.Close();
        });
    }

    [Fact]
    public void TheListShowsTheShortcutAsABadge()
    {
        Show(Sample(), (d, _) =>
        {
            Assert.Equal(("Keep", "F6"), d.RowText(0));
            Assert.Equal(("Trash", "F8"), d.RowText(2));
            d.Close();
        });
    }

    // ---- chọn hàng / lưu hàng trước ---------------------------------------------------------------------------------------

    [Fact]
    public void SelectingAnotherRow_SavesThePreviousOne_RefreshesTheList_AndShowsTheNewFields()
    {
        var (_, dialog) = Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.NameId, "  Keep it  ");
            SetText(d, ActionProfilesDialog.ShortcutId, " F9 ");
            ComboBoxControl.Select(Item(d, ActionProfilesDialog.OperationId), 1);
            Select(d, 1);

            Assert.Equal(["Keep it", "Copy out", "Trash"], ListNames(d));
            Assert.Equal("Copy out", FieldText(d, ActionProfilesDialog.NameId));
            Assert.Equal("F7", FieldText(d, ActionProfilesDialog.ShortcutId));
            Assert.Equal(1, OperationIndex(d));
            Assert.True(Confirm(d));
            d.Close();
        });

        var first = dialog.Actions[0];
        Assert.Equal("Keep it", first.Name); // trimmed
        Assert.Equal("F9", first.Shortcut); // trimmed by the presenter
        Assert.Equal(FileOperationType.Copy, first.Operation);
    }

    [Fact]
    public void OperationAndConfirm_SurviveASelectionRoundTrip()
    {
        Show(Sample(), (d, _) =>
        {
            ComboBoxControl.Select(Item(d, ActionProfilesDialog.OperationId), 2);
            ButtonStateControl.SetChecked(Item(d, ActionProfilesDialog.ConfirmId), true);
            Select(d, 2);
            Select(d, 0);

            Assert.Equal(2, OperationIndex(d));
            Assert.True(Confirm(d));
            d.Close();
        });
    }

    // ---- Add / Remove -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Add_AppendsANewActionWithTheNextFreeShortcut_AndOpensIt()
    {
        var (_, dialog) = Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.NameId, "Edited");
            Command(d, ActionProfilesDialog.AddId);

            Assert.Equal(["Edited", "Copy out", "Trash", Tr.ActionProfilesNewActionName], ListNames(d));
            Assert.Equal(3, ListBoxControl.SelectedIndex(ListOf(d)));
            Assert.Equal("F9", FieldText(d, ActionProfilesDialog.ShortcutId)); // F6..F8 are taken
            Assert.Equal(Tr.ActionProfilesNewActionName, FieldText(d, ActionProfilesDialog.NameId));
            Assert.Equal("Output", FieldText(d, ActionProfilesDialog.DestinationId));
            d.Close();
        });

        Assert.Equal("Edited", dialog.Actions[0].Name);
        Assert.Equal(4, dialog.Actions.Count);
    }

    [Fact]
    public void Remove_SelectsTheNextRow_AndRemovingTheLastOneClearsTheFields()
    {
        Show(Sample(), (d, _) =>
        {
            Select(d, 1);
            Command(d, ActionProfilesDialog.RemoveId);
            Assert.Equal(["Keep", "Trash"], ListNames(d));
            Assert.Equal(1, ListBoxControl.SelectedIndex(ListOf(d)));
            Assert.Equal("Trash", FieldText(d, ActionProfilesDialog.NameId));

            Command(d, ActionProfilesDialog.RemoveId); // removes the last row: selection falls back to the previous
            Assert.Equal(["Keep"], ListNames(d));
            Assert.Equal("Keep", FieldText(d, ActionProfilesDialog.NameId));

            Command(d, ActionProfilesDialog.RemoveId);
            Assert.Empty(ListNames(d));
            Assert.Equal(string.Empty, FieldText(d, ActionProfilesDialog.NameId));
            Assert.Equal(string.Empty, FieldText(d, ActionProfilesDialog.ShortcutId));
            Assert.Equal(string.Empty, FieldText(d, ActionProfilesDialog.DestinationId));
            Assert.False(Confirm(d));
            Command(d, ActionProfilesDialog.RemoveId); // nothing selected: a no-op
            d.Close();
        });
    }

    // ---- Apply ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_WithAValidList_ClosesWithOk_AndKeepsTheEdits()
    {
        var (result, dialog) = Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.NameId, "Renamed");
            ComboBoxControl.Select(Item(d, ActionProfilesDialog.OperationId), 2);
            Command(d, NativeDialog.IdOk);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.Equal("Renamed", dialog.Actions[0].Name);
        Assert.Equal(FileOperationType.Recycle, dialog.Actions[0].Operation);
    }

    [Fact]
    public void Apply_WithABlankName_WarnsAndKeepsTheDialogOpen()
    {
        var warnings = new FakeWarnings();
        var (result, _) = Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.NameId, "   ");
            Command(d, NativeDialog.IdOk);

            Assert.False(d.IsClosed);
            var warning = Assert.Single(warnings.Shown);
            Assert.Equal(Tr.DialogActionProfilesInvalidTitle, warning.Title);
            Assert.Equal(Tr.DialogActionProfilesInvalidMessage, warning.Message);
            d.Close();
        }, warnings: warnings);

        Assert.Equal(NativeDialog.IdCancel, result);
    }

    [Fact]
    public void Apply_WithDuplicateShortcuts_Warns()
    {
        var warnings = new FakeWarnings();
        Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.ShortcutId, "F7"); // the second row's key
            Command(d, NativeDialog.IdOk);

            Assert.False(d.IsClosed);
            Assert.Single(warnings.Shown);
            d.Close();
        }, warnings: warnings);
    }

    [Fact]
    public void Apply_WithADestinationOutsideThePhotoFolder_ReportsThePresentersReason()
    {
        var warnings = new FakeWarnings();
        Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.DestinationId, @"..\elsewhere");
            Command(d, NativeDialog.IdOk);

            var warning = Assert.Single(warnings.Shown);
            Assert.Equal(Tr.ActionProfilesErrorDestinationEscapes("Keep"), warning.Message);
            d.Close();
        }, warnings: warnings);
    }

    [Fact]
    public void Enter_InTheNameBox_AppliesThroughTheDialogManager()
    {
        var (result, _) = Show(Sample(), (d, _) =>
        {
            var name = Item(d, ActionProfilesDialog.NameId);
            User32.SetFocus(name);
            Assert.True(Key(d, name, VkReturn));
            Drain(d);
        });

        Assert.Equal(NativeDialog.IdOk, result);
    }

    // ---- huỷ ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("close")]
    [InlineData("syscommand")]
    public void EveryCancelPath_ReturnsCancel_WithoutWarnings(string how)
    {
        var warnings = new FakeWarnings();
        var (result, _) = Show(Sample(), (d, _) =>
        {
            SetText(d, ActionProfilesDialog.NameId, "changed but cancelled");
            switch (how)
            {
                case "button":
                    Command(d, NativeDialog.IdCancel);
                    break;
                case "escape":
                    Key(d, Item(d, ActionProfilesDialog.NameId), VkEscape);
                    Drain(d);
                    break;
                case "close":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                default:
                    User32.SendMessage(d.Hwnd, WindowMessages.WmSysCommand, (nint)ScClose, 0);
                    break;
            }
        }, warnings: warnings);

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Empty(warnings.Shown);
    }

    // ---- Import / Export ----------------------------------------------------------------------------------------------------

    private static string TempFile(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wp35g2-actionprofiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    [Fact]
    public void Import_ReplacesTheListAndOpensTheFirstRow()
    {
        var path = TempFile("in.json");
        File.WriteAllText(path, ActionListJson.SerializeForExport(
            [new ReviewAction { Name = "Imported A", Shortcut = "F10", Operation = FileOperationType.Copy, Destination = "A", Confirm = true }]));
        var files = new FakeFiles { OpenResult = path };
        try
        {
            Show(Sample(), (d, _) =>
            {
                Command(d, ActionProfilesDialog.ImportId);

                Assert.Equal(["Imported A"], ListNames(d));
                Assert.Equal(0, ListBoxControl.SelectedIndex(ListOf(d)));
                Assert.Equal("F10", FieldText(d, ActionProfilesDialog.ShortcutId));
                Assert.Equal(1, OperationIndex(d));
                Assert.True(Confirm(d));
                d.Close();
            }, files);

            Assert.Equal(["open:" + Tr.ActionProfilesImport], files.Calls);
            Assert.Contains(files.Filters, f => f.EndsWith("*.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Import_OfAnInvalidFile_WarnsAndKeepsTheList()
    {
        var path = TempFile("bad.json");
        File.WriteAllText(path, "{ not json");
        var files = new FakeFiles { OpenResult = path };
        var warnings = new FakeWarnings();
        try
        {
            Show(Sample(), (d, _) =>
            {
                Command(d, ActionProfilesDialog.ImportId);
                Assert.Equal(["Keep", "Copy out", "Trash"], ListNames(d));
                d.Close();
            }, files, warnings);

            var warning = Assert.Single(warnings.Shown);
            Assert.Equal(Tr.DialogImportActionsFailedTitle, warning.Title);
            Assert.Equal(Tr.DialogImportActionsInvalidMessage, warning.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Import_Cancelled_ChangesNothing()
    {
        var files = new FakeFiles { OpenResult = null };
        var warnings = new FakeWarnings();
        Show(Sample(), (d, _) =>
        {
            Command(d, ActionProfilesDialog.ImportId);
            Assert.Equal(["Keep", "Copy out", "Trash"], ListNames(d));
            d.Close();
        }, files, warnings);

        Assert.Empty(warnings.Shown);
        Assert.Single(files.Calls);
    }

    [Fact]
    public void Export_WritesTheCurrentEdits_ToTheChosenFile()
    {
        var path = TempFile("out.json");
        var files = new FakeFiles { SaveResult = path };
        try
        {
            Show(Sample(), (d, _) =>
            {
                SetText(d, ActionProfilesDialog.NameId, "Exported name"); // not yet saved to the row: Export saves it first
                Command(d, ActionProfilesDialog.ExportId);
                d.Close();
            }, files);

            var text = File.ReadAllText(path);
            Assert.Contains("Exported name", text, StringComparison.Ordinal);
            Assert.Equal(["save:" + Tr.ActionProfilesExport + ":json:photoreview-actions.json"], files.Calls);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Export_ToAnUnwritableTarget_WarnsWithTheReason()
    {
        var directory = Path.GetDirectoryName(TempFile("x.json"))!;
        var files = new FakeFiles { SaveResult = directory }; // a directory: File.WriteAllText throws
        var warnings = new FakeWarnings();
        try
        {
            Show(Sample(), (d, _) =>
            {
                Command(d, ActionProfilesDialog.ExportId);
                d.Close();
            }, files, warnings);

            var warning = Assert.Single(warnings.Shown);
            Assert.Equal(Tr.DialogExportActionsFailedTitle, warning.Title);
            Assert.False(string.IsNullOrWhiteSpace(warning.Message));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- modal, lồng nhau -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Modal_DisablesTheOwner_AndRestoresItOnClose()
    {
        var (duringEnabled, afterEnabled) = OnSta(() =>
        {
            var owner = CreateOwner();
            try
            {
                var during = true;
                new ActionProfilesDialog(Sample()).ShowModal(owner, d => { during = User32.IsWindowEnabled(owner); d.Close(); });
                return (during, User32.IsWindowEnabled(owner));
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.False(duringEnabled);
        Assert.True(afterEnabled);
    }

    [Fact]
    public void ModalInsideModal_DisablesTheOuterDialog_ThenRestoresIt_WhileTheShellOwnerStaysDisabled()
    {
        var (outerDuring, ownerDuring, outerAfter, ownerAfter, innerResult) = OnSta(() =>
        {
            var owner = CreateOwner();
            try
            {
                var outer = new ActionProfilesDialog(Sample());
                var outerDuringInner = true;
                var ownerDuringInner = true;
                var inner = 0;
                var outerAfterInner = false;
                var ownerAfterInner = false;
                outer.ShowModal(owner, d =>
                {
                    inner = new ZoomPromptDialog(100).ShowModal(d.Hwnd, z =>
                    {
                        outerDuringInner = User32.IsWindowEnabled(d.Hwnd);
                        ownerDuringInner = User32.IsWindowEnabled(owner);
                        z.Close();
                    });
                    outerAfterInner = User32.IsWindowEnabled(d.Hwnd);
                    ownerAfterInner = User32.IsWindowEnabled(owner);
                    d.Close();
                });
                return (outerDuringInner, ownerDuringInner, outerAfterInner, ownerAfterInner, inner);
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.False(outerDuring);
        Assert.False(ownerDuring);
        Assert.True(outerAfter);
        Assert.False(ownerAfter, "the shell window must stay disabled until the OUTER dialog closes");
        Assert.Equal(NativeDialog.IdCancel, innerResult);
    }

    [Fact]
    public void ChromeIsApplied_ExactlyOnce_WithTheComboAndTheListBox()
    {
        var chrome = new RecordingChrome();
        Show(Sample(), (d, _) => d.Close(), chrome: chrome);

        var call = Assert.Single(chrome.Calls);
        Assert.Contains(call.Controls, c => c.Class == DialogControlClass.ComboBox);
        Assert.Contains(call.Controls, c => c.Class == DialogControlClass.ListBox);
    }

    // ---- bố cục -----------------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Problems(NativeDialog dialog, string label)
    {
        int[] ids =
        [
            ActionProfilesDialog.ListHeaderId, ActionProfilesDialog.ListId, ActionProfilesDialog.AddId, ActionProfilesDialog.RemoveId,
            ActionProfilesDialog.ImportId, ActionProfilesDialog.ExportId, ActionProfilesDialog.DetailsHeaderId,
            ActionProfilesDialog.NameLabelId, ActionProfilesDialog.NameId, ActionProfilesDialog.ShortcutLabelId,
            ActionProfilesDialog.ShortcutId, ActionProfilesDialog.OperationLabelId, ActionProfilesDialog.OperationId,
            ActionProfilesDialog.DestinationLabelId, ActionProfilesDialog.DestinationId, ActionProfilesDialog.ConfirmId,
            NativeDialog.IdCancel, NativeDialog.IdOk,
        ];
        var client = ClientRect(dialog.Hwnd);
        var rects = ids.Select(id => (id, rect: ChildRect(dialog, id))).ToList();
        foreach (var (id, rect) in rects)
        {
            if (rect.Left < 0 || rect.Top < 0 || rect.Right > client.Right || rect.Bottom > client.Bottom)
            {
                yield return $"{label}: control {id} leaves the client area ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom} in {client.Right}x{client.Bottom})";
            }
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i].rect;
                var b = rects[j].rect;
                if (a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
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
        unsafe
        {
            User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void Layout_DoesNotOverlapOrCutAnything_AtOneAndOneAndAHalfScale(string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var problems = new List<string>();
        Show(Sample(), (d, _) =>
        {
            problems.AddRange(Problems(d, "96 dpi"));
            ChangeDpi(d, 144);
            problems.AddRange(Problems(d, "144 dpi"));
            // Every wrapped label keeps the height its text needs (Vietnamese is longer).
            foreach (var id in new[] { ActionProfilesDialog.DestinationLabelId, ActionProfilesDialog.ConfirmId })
            {
                var control = Item(d, id);
                if (Height(WindowRect(control)) < NeededTextHeight(control) - 1)
                {
                    problems.Add($"control {id} is shorter than its text");
                }
            }

            d.Close();
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void OpeningAndClosing_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            ShowOnOwner(new ActionProfilesDialog(Sample()), (d, _) => d.Close());
            var gdiBefore = GdiObjects();
            var userBefore = UserObjects();
            for (var i = 0; i < 6; i++)
            {
                ShowOnOwner(new ActionProfilesDialog(Sample()), (d, _) =>
                {
                    Command(d, ActionProfilesDialog.AddId);
                    d.Close();
                });
            }

            return ((int)GdiObjects() - (int)gdiBefore, (int)UserObjects() - (int)userBefore);
        });

        Assert.True(gdi <= 2, $"GDI objects grew by {gdi}");
        Assert.True(user <= 2, $"USER objects grew by {user}");
    }
}
