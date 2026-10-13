using System.Text.Json;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: KHUNG cửa sổ Settings Win32 trên HWND thật (điều hướng, nhớ trang, Save/Apply/Cancel, Defaults/Reset trang, Import/Export,
/// cuộn, Tab, bố cục en/vi ở 100 %/150 %, ngôn ngữ, rò handle). Nội dung từng trang: <see cref="SettingsPagesTests"/>.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsDialogTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public SettingsDialogTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    // ---- mở + điều hướng ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Opens_WithEveryPageInTheNavigation_OnTheRememberedPage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal(SettingsPresenter.Pages.Count, ListBoxControl.Count(H(d, SettingsDialog.NavId)));
            for (var i = 0; i < SettingsPresenter.Pages.Count; i++)
            {
                Assert.Equal(SettingsPresenter.PageTitle(SettingsPresenter.Pages[i].Key), NavText(d, i));
                Assert.Equal(SettingsPresenter.Pages[i].Key, d.Pages[i].Key);
            }

            Assert.Equal("General", d.ActivePageKey);
            Assert.Equal("General", Txt(d, SettingsDialog.PageTitleId));
            Assert.True(Visible(d, SettingsDialog.ViewportId(0)));
            Assert.False(Visible(d, SettingsDialog.ViewportId(1)));
            Assert.Equal(0, ListBoxControl.SelectedIndex(H(d, SettingsDialog.NavId)));
            d.Close();
        });
    }

    [Fact]
    public void SelectingAPage_ShowsItAlone_RetitlesAndRemembersIt()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            Assert.Equal("Display", d.ActivePageKey);
            Assert.Equal("Display", Txt(d, SettingsDialog.PageTitleId));
            Assert.True(Visible(d, SettingsDialog.ViewportId(1)));
            Assert.False(Visible(d, SettingsDialog.ViewportId(0)));
            Assert.Equal("Display", SettingsPresenter.RememberedPageKey);
            d.Close();
        });
    }

    [Fact]
    public void TheLastPage_IsRememberedForTheNextOpen()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 6);
            d.Close();
        });

        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal("Diagnostics", d.ActivePageKey);
            Assert.Equal(6, ListBoxControl.SelectedIndex(H(d, SettingsDialog.NavId)));
            d.Close();
        });
    }

    [Fact]
    public void ExternalEditorTarget_OpensTheFilesPage_WithoutChangingTheRememberedPage()
    {
        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal("Files", d.ActivePageKey);
            Assert.Equal("General", SettingsPresenter.RememberedPageKey);
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), target: SettingsTarget.ExternalEditor));
    }

    [Fact]
    public void CtrlTab_CyclesThePages_AndWraps()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var message = new Msg { Hwnd = H(d, SettingsDialog.NavId), Message = WindowMessages.WmKeyDown, WParam = 0x09, LParam = 1 };
            Assert.True(d.HandleKeyForTest(message));
            Assert.Equal("Display", d.ActivePageKey);
            d.CyclePage(forward: false);
            d.CyclePage(forward: false);
            Assert.Equal("Diagnostics", d.ActivePageKey);
            d.CyclePage(forward: true);
            Assert.Equal("General", d.ActivePageKey);
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), control: () => true));
    }

    [Fact]
    public void PlainTab_IsLeftToTheDialogManager()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var message = new Msg { Hwnd = H(d, SettingsDialog.NavId), Message = WindowMessages.WmKeyDown, WParam = 0x09, LParam = 1 };
            Assert.False(d.HandleKeyForTest(message));
            Assert.Equal("General", d.ActivePageKey);
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), control: () => false));
    }

    [Fact]
    public void ARealPageIsBuilt_ForEveryNavigationEntry_AndPendingOnesAreAnnounced()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var pending = SettingsPageCatalog.PendingKeys;
            foreach (var key in pending)
            {
                var index = SettingsPresenter.Pages.ToList().FindIndex(p => p.Key == key);
                Assert.IsType<PendingSettingsPage>(d.Pages[index]);
            }

            foreach (var key in SettingsPageCatalog.ImplementedKeys)
            {
                var index = SettingsPresenter.Pages.ToList().FindIndex(p => p.Key == key);
                Assert.IsNotType<PendingSettingsPage>(d.Pages[index]);
            }

            d.Close();
        });
    }

    // ---- Save / Apply / Cancel ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Save_WithValidValues_ClosesWithOk_AndPersistsThroughTheStore()
    {
        using var temp = new TempRoot("settings-save");
        var store = NewStore(temp.Path);
        var current = new AppSettings();
        var (result, dialog) = Show(current, (d, _) =>
        {
            var ids = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            Choose(d, ids.SortMode, 4); // SizeAscending
            Command(d, SettingsDialog.SaveId);
        }, store: store);

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.True(dialog.Saved);
        Assert.Equal(ImageSortMode.SizeAscending, dialog.Settings.ImageSortMode);
        Assert.Equal(ImageSortMode.SizeAscending, store.Current.ImageSortMode);
        Assert.True(File.Exists(Path.Combine(temp.Path, "config.json")));
    }

    [Fact]
    public void Enter_InAnEdit_SavesThroughTheDialogManager()
    {
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            var ids = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids;
            User32.SetFocus(H(d, ids.FontSize));
            Assert.True(Key(d, H(d, ids.FontSize), VkReturn));
            Drain(d);
        });

        Assert.Equal(NativeDialog.IdOk, result);
        Assert.True(dialog.Saved);
    }

    [Fact]
    public void Apply_SavesAndStaysOpen_AndASubsequentCancelStillReportsSaved()
    {
        using var temp = new TempRoot("settings-apply");
        var store = NewStore(temp.Path);
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            var ids = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            Choose(d, ids.InstanceMode, 1);
            Command(d, SettingsDialog.ApplyId);
            Assert.False(d.IsClosed);
            Assert.Equal(InstanceMode.PerFolder, store.Current.InstanceMode);
            Choose(d, ids.InstanceMode, 0); // an edit after Apply is thrown away by Cancel
            Command(d, NativeDialog.IdCancel);
        }, store: store);

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.True(dialog.Saved);
        Assert.Equal(InstanceMode.PerFolder, store.Current.InstanceMode);
    }

    [Fact]
    public void Cancel_WritesNothing_AndIsNotSaved()
    {
        using var temp = new TempRoot("settings-cancel");
        var store = NewStore(temp.Path);
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            var ids = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            Choose(d, ids.SortMode, 2);
            Command(d, NativeDialog.IdCancel);
        }, store: store);

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.False(dialog.Saved);
        Assert.False(File.Exists(Path.Combine(temp.Path, "config.json")));
        Assert.Equal(new AppSettings().ImageSortMode, store.Current.ImageSortMode);
    }

    [Theory]
    [InlineData("esc")]
    [InlineData("close")]
    [InlineData("api")]
    public void EveryCancelPath_ReturnsCancel(string how)
    {
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            switch (how)
            {
                case "esc":
                    Assert.True(Key(d, H(d, SettingsDialog.NavId), VkEscape));
                    Drain(d);
                    break;
                case "close":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                default:
                    d.Close();
                    break;
            }
        });

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.False(dialog.Saved);
    }

    [Fact]
    public void AnInvalidValue_WarnsWithThePresentersMessage_StaysOpen_AndFocusesTheFieldOnItsPage()
    {
        var warnings = new FakeWarnings();
        var (result, dialog) = Show(new AppSettings(), (d, _) =>
        {
            var display = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids;
            GoTo(d, 1);
            Tick(d, display.InfoAutoHide, true);
            SetText(d, display.InfoDelay, "not a number");
            GoTo(d, 0); // the error must bring the owning page back
            Command(d, SettingsDialog.SaveId);
            Assert.False(d.IsClosed);
            Assert.Equal("Display", d.ActivePageKey);
            Assert.Equal(H(d, display.InfoDelay), User32.GetFocus());
            d.Close();
        }, Options(warnings, new FakeFiles()));

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.False(dialog.Saved);
        var shown = Assert.Single(warnings.Shown);
        Assert.Equal(Tr.DialogSettingsInvalidTitle, shown.Title);
        Assert.Equal(
            Tr.DialogSettingsInvalidInfoOverlayAutoHideDelay(AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs),
            shown.Message);
    }

    [Fact]
    public void AStoreThatCannotWrite_WarnsWithTheSaveFailedTitle_AndKeepsTheDialog()
    {
        using var temp = new TempRoot("settings-fail");
        var blocked = Path.Combine(temp.Path, "blocked");
        Directory.CreateDirectory(blocked);
        // config.json is a DIRECTORY: the store cannot replace it.
        Directory.CreateDirectory(Path.Combine(blocked, "config.json"));
        var store = NewStore(blocked);
        var warnings = new FakeWarnings();
        var (result, _) = Show(new AppSettings(), (d, _) =>
        {
            Command(d, SettingsDialog.SaveId);
            Assert.False(d.IsClosed);
            d.Close();
        }, Options(warnings, new FakeFiles()), store);

        Assert.Equal(NativeDialog.IdCancel, result);
        Assert.Equal(Tr.DialogSettingsSaveFailedTitle, Assert.Single(warnings.Shown).Title);
    }

    // ---- Defaults / Reset trang ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_ReloadsEveryPage_FromTheBuiltInDefaults()
    {
        var settings = new AppSettings { InitialViewMode = InitialViewMode.Percent100, LoggingEnabled = true, ShowFolderInfo = true };
        Show(settings, (d, _) =>
        {
            var general = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            var display = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids;
            var diag = ((Win32.Dialogs.SettingsUi.Pages.DiagnosticsSettingsPage)d.Pages[6]).Ids;
            Assert.Equal(4, ComboIndex(d, general.ViewMode));
            Assert.True(Checked(d, display.ShowFolderInfo));
            Assert.True(Checked(d, diag.Logging));
            Command(d, SettingsDialog.DefaultsId);
            Assert.Equal(0, ComboIndex(d, general.ViewMode));
            Assert.False(Checked(d, display.ShowFolderInfo));
            Assert.False(Checked(d, diag.Logging));
            d.Close();
        });
    }

    [Fact]
    public void ResetPage_ResetsOnlyTheShowingPage()
    {
        var settings = new AppSettings { InitialViewMode = InitialViewMode.Percent100, LoggingEnabled = true };
        Show(settings, (d, _) =>
        {
            var general = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            var diag = ((Win32.Dialogs.SettingsUi.Pages.DiagnosticsSettingsPage)d.Pages[6]).Ids;
            Command(d, SettingsDialog.ResetPageId); // General is showing
            Assert.Equal(0, ComboIndex(d, general.ViewMode));
            Assert.True(Checked(d, diag.Logging), "the other pages keep what they had");
            GoTo(d, 6);
            Command(d, SettingsDialog.ResetPageId);
            Assert.False(Checked(d, diag.Logging));
            // the working copy only changes on Save
            Assert.True(d.Settings.LoggingEnabled);
            d.Close();
        });
    }

    // ---- Import / Export -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Import_FillsThePages_AndAnUnreadableFileWarnsAndKeepsThem()
    {
        using var temp = new TempRoot("settings-import");
        var good = temp.File("good.json", System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new AppSettings { InitialViewMode = InitialViewMode.FitHeight, LoggingEnabled = true },
                AppSettingsJsonContext.Default.AppSettings)));
        var bad = temp.File("bad.json", System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var warnings = new FakeWarnings();
        Show(new AppSettings(), (d, _) =>
        {
            var general = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            var diag = ((Win32.Dialogs.SettingsUi.Pages.DiagnosticsSettingsPage)d.Pages[6]).Ids;
            d.ImportSettingsFrom(good);
            Assert.Equal(2, ComboIndex(d, general.ViewMode));
            Assert.True(Checked(d, diag.Logging));
            Assert.Empty(warnings.Shown);
            d.ImportSettingsFrom(bad);
            Assert.Equal(2, ComboIndex(d, general.ViewMode));
            Assert.Single(warnings.Shown);
            d.Close();
        }, Options(warnings, new FakeFiles()));
    }

    [Fact]
    public void ImportButton_UsesTheFileDialog_AndCancelledPickChangesNothing()
    {
        var files = new FakeFiles { OpenResult = null };
        Show(new AppSettings(), (d, _) =>
        {
            var general = ((Win32.Dialogs.SettingsUi.Pages.GeneralSettingsPage)d.Pages[0]).Ids;
            Choose(d, general.ViewMode, 3);
            Command(d, SettingsDialog.ImportId);
            Assert.Equal(3, ComboIndex(d, general.ViewMode));
            Assert.Single(files.Calls);
            Assert.StartsWith("open:", files.Calls[0], StringComparison.Ordinal);
            d.Close();
        }, Options(new FakeWarnings(), files));
    }

    [Fact]
    public void Export_WritesTheWorkingCopyAsConfigJson()
    {
        using var temp = new TempRoot("settings-export");
        var target = Path.Combine(temp.Path, "out.json");
        var files = new FakeFiles { SaveResult = target };
        Show(new AppSettings { LoggingEnabled = true }, (d, _) =>
        {
            Command(d, SettingsDialog.ExportId);
            d.Close();
        }, Options(new FakeWarnings(), files));

        Assert.Equal("save:" + Tr.DialogExportSettingsTitle + ":json:PhotoReview-settings.json", files.Calls[0]);
        using var json = JsonDocument.Parse(File.ReadAllText(target));
        Assert.True(json.RootElement.GetProperty("LoggingEnabled").GetBoolean());
    }

    // ---- cuộn ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATallPage_Scrolls_WithTheWheelAndTheScrollBar_AndClipsToItsViewport()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            Assert.True(d.ContentHeight(1) > d.ViewportHeight, "the Display page is taller than the window");
            var ids = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids;
            Assert.True(Visible(d, SettingsDialog.ScrollId));
            var before = RectInViewport(d, ids.ShowInfoOverlay).Top;

            // wheel delivered to a page control travels up to the dialog
            User32.SendMessage(H(d, ids.ShowInfoOverlay), WindowMessages.WmMouseWheel, unchecked((nint)0xFF880000L), 0);
            Assert.True(d.ScrollOffset(1) > 0);
            Assert.Equal(before - d.ScrollOffset(1), RectInViewport(d, ids.ShowInfoOverlay).Top);

            User32.SendMessage(H(d, ids.ShowInfoOverlay), WindowMessages.WmMouseWheel, (nint)(120u << 16), 0);
            Assert.Equal(0, d.ScrollOffset(1));

            User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, ScrollConstants.SbBottom, H(d, SettingsDialog.ScrollId));
            Assert.Equal(d.ContentHeight(1) - d.ViewportHeight, d.ScrollOffset(1));
            // the last control is now inside the viewport
            var last = d.PageItems(1)[^1].Id;
            Assert.True(RectInViewport(d, last).Bottom <= d.ViewportHeight + 1);

            // switching pages starts again at the top
            GoTo(d, 0);
            GoTo(d, 1);
            Assert.Equal(0, d.ScrollOffset(1));
            d.Close();
        });
    }

    [Fact]
    public void FocusingAControlBelowTheFold_ScrollsItIntoView()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            var last = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).TitleChecks[^1];
            Assert.True(RectInViewport(d, last).Bottom > d.ViewportHeight, "starts out of sight");
            d.FocusControl(last);
            var rect = RectInViewport(d, last);
            Assert.True(rect.Top >= 0 && rect.Bottom <= d.ViewportHeight + 1);
            Assert.Equal(H(d, last), User32.GetFocus());
            d.Close();
        });
    }

    // ---- Tab ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tab_StaysOnTheShowingPage_InDeclarationOrder()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            User32.SetFocus(H(d, SettingsDialog.NavId));
            var expected = d.PageItems(1)
                .Where(i => i.Class != DialogControlClass.Static && (i.Style & DialogConstants.WsTabStop) != 0)
                .Select(i => i.Id).ToList();
            var visited = new List<int>();
            for (var step = 0; step < expected.Count + 8; step++)
            {
                var focus = User32.GetFocus();
                Key(d, focus, VkTab);
                var next = User32.GetFocus();
                var id = User32.GetDlgCtrlID(next);
                if (id == SettingsDialog.SaveId || id == SettingsDialog.ApplyId || id == SettingsDialog.CancelId || id == SettingsDialog.DefaultsId
                    || id == SettingsDialog.ResetPageId || id == SettingsDialog.ExportId || id == SettingsDialog.ImportId)
                {
                    break;
                }

                visited.Add(id);
            }

            // Disabled controls (sub-options of an unticked parent) are skipped by the dialog manager.
            var reachable = expected.Where(id => User32.IsWindowEnabled(H(d, id))).ToList();
            Assert.Equal(reachable, visited);
            Assert.All(visited, id => Assert.Equal(SettingsDialog.PageIdBase(1) / 1000, id / 1000));
            d.Close();
        });
    }

    // ---- bố cục ---------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Problems(SettingsDialog dialog, string label)
    {
        var width = Width(ClientRect(H(dialog, SettingsDialog.ViewportId(dialog.ActivePageIndex))));
        var items = dialog.PageItems(dialog.ActivePageIndex);
        var rects = new List<(int Id, Rect Rect)>();
        foreach (var item in items)
        {
            var control = H(dialog, item.Id);
            if (control == 0 || !User32.IsWindowVisible(control) || IsGroupBox(control))
            {
                continue;
            }

            var rect = RectInViewport(dialog, item.Id);
            rects.Add((item.Id, rect));
            if (rect.Left < 0 || rect.Right > width + 1)
            {
                yield return $"{label}: control {item.Id} leaves the page horizontally ({rect.Left}..{rect.Right} in {width})";
            }

            var kind = item.Style & 0xF;
            var isText = item.Class == DialogControlClass.Static || (item.Class == DialogControlClass.Button && kind is 2 or 3);
            if (isText && Height(rect) < NeededTextHeight(control) - 1)
            {
                yield return $"{label}: control {item.Id} is shorter than its text";
            }
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i].Rect;
                var b = rects[j].Rect;
                if (a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                {
                    yield return $"{label}: controls {rects[i].Id} and {rects[j].Id} overlap";
                }
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void EveryPage_DoesNotOverlapOrCutAnything_AtOneAndOneAndAHalfScale(string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var problems = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            for (var page = 0; page < SettingsPresenter.Pages.Count; page++)
            {
                GoTo(d, page);
                problems.AddRange(Problems(d, $"{language} {d.ActivePageKey} 96 dpi"));
            }

            ChangeDpi(d, 144);
            for (var page = 0; page < SettingsPresenter.Pages.Count; page++)
            {
                GoTo(d, page);
                problems.AddRange(Problems(d, $"{language} {d.ActivePageKey} 144 dpi"));
            }

            // the button row and the navigation stay inside the client area
            var client = ClientRect(d.Hwnd);
            foreach (var id in new[] { SettingsDialog.DefaultsId, SettingsDialog.ResetPageId, SettingsDialog.ExportId, SettingsDialog.ImportId,
                         SettingsDialog.CancelId, SettingsDialog.ApplyId, SettingsDialog.SaveId, SettingsDialog.NavId })
            {
                var rect = ChildRect(d, id);
                if (rect.Left < 0 || rect.Top < 0 || rect.Right > client.Right || rect.Bottom > client.Bottom)
                {
                    problems.Add($"{language}: host control {id} leaves the client area");
                }
            }

            d.Close();
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void ResizingTheWindow_RelaysOutThePage_WithoutOverlap()
    {
        var problems = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            var rect = WindowRect(d.Hwnd);
            User32.SetWindowPos(d.Hwnd, 0, rect.Left, rect.Top, Width(rect) + 220, Height(rect) + 120,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder);
            problems.AddRange(Problems(d, "wider"));
            User32.SetWindowPos(d.Hwnd, 0, rect.Left, rect.Top, Math.Max(500, Width(rect) - 120), Height(rect) - 100,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder);
            problems.AddRange(Problems(d, "narrower"));
            d.Close();
        });

        Assert.Empty(problems);
    }

    // ---- ngôn ngữ -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheLanguage_RetitlesTheWindow_TheNavigationAndThePage_AndRelaysOut()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var english = (Title: TextOf(d.Hwnd), Nav: NavText(d, 1), Page: Txt(d, SettingsDialog.PageTitleId), Save: Txt(d, SettingsDialog.SaveId));
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            Assert.NotEqual(english.Title, TextOf(d.Hwnd));
            Assert.Equal(Tr.SettingsWindowTitle, TextOf(d.Hwnd));
            Assert.Equal(TestLocalization.Vietnamese.Get("settings.nav.display"), NavText(d, 1));
            Assert.Equal(TestLocalization.Vietnamese.Get("settings.nav.general"), Txt(d, SettingsDialog.PageTitleId));
            Assert.NotEqual(english.Save, Txt(d, SettingsDialog.SaveId));
            // Every button of the frame follows the language (each is set separately by RefreshLocalizedTexts).
            Assert.Equal(Tr.CommonSave, Txt(d, SettingsDialog.SaveId));
            Assert.Equal(Tr.CommonApply, Txt(d, SettingsDialog.ApplyId));
            Assert.Equal(Tr.CommonCancel, Txt(d, SettingsDialog.CancelId));
            Assert.Equal(Tr.SettingsRestoreDefaults, Txt(d, SettingsDialog.DefaultsId));
            Assert.Equal(Tr.SettingsResetPage, Txt(d, SettingsDialog.ResetPageId));
            Assert.Equal(Tr.SettingsExportSettings, Txt(d, SettingsDialog.ExportId));
            Assert.Equal(Tr.SettingsImportSettings, Txt(d, SettingsDialog.ImportId));
            Assert.NotEqual("Apply", Txt(d, SettingsDialog.ApplyId));
            Assert.Empty(Problems(d, "vi after switch"));
            Localizer.SetCurrent(TestLocalization.English);
            Drain(d);
            Assert.Equal(english.Title, TextOf(d.Hwnd));
            Assert.Equal(english.Nav, NavText(d, 1));
            d.Close();
        });
    }

    // ---- handle -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void OpeningAndClosing_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            void One() => ShowOnOwner(NewDialog(), (d, _) =>
            {
                GoTo((SettingsDialog)d, 1);
                ((SettingsDialog)d).CyclePage(true);
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

    [Fact]
    public void Chrome_IsAppliedToTheFrame_AndThenToEachPageWhenItsControlsAreCreated()
    {
        var chrome = new RecordingChrome();
        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal(2, chrome.Calls.Count);
            GoTo(d, 1);
            Assert.Equal(3, chrome.Calls.Count);
            GoTo(d, 1);
            GoTo(d, 0);
            Assert.Equal(3, chrome.Calls.Count);
            d.Close();
        }, chrome: chrome, realizeAll: false);

        Assert.Contains(chrome.Calls[0].Controls, c => c.Class == DialogControlClass.ScrollBar);
        Assert.Contains(chrome.Calls[0].Controls, c => c.Class == DialogControlClass.ListBox);
        Assert.Contains(chrome.Calls[1].Controls, c => c.Class == DialogControlClass.ComboBox);
        Assert.Contains(chrome.Calls[2].Controls, c => c.Class == DialogControlClass.Trackbar);
    }

    [Fact]
    public void OnlyTheShowingPagesControlsExist_UntilAnotherPageIsOpened()
    {
        Show(new AppSettings(), (d, _) =>
        {
            Assert.True(d.IsPageCreated(0));
            Assert.False(d.IsPageCreated(1));
            var firstOfDisplay = d.PageItems(1)[0].Id;
            Assert.Equal(0, H(d, firstOfDisplay));
            GoTo(d, 1);
            Assert.True(d.IsPageCreated(1));
            Assert.NotEqual(0, H(d, firstOfDisplay));
            d.Close();
        }, realizeAll: false);
    }

    [Fact]
    public void APageOpenedAfterDefaults_ShowsTheDefaultsAndNotTheOldValues()
    {
        Show(new AppSettings { LoggingEnabled = true, ShowFolderInfo = true }, (d, _) =>
        {
            Command(d, SettingsDialog.DefaultsId);
            GoTo(d, 6);
            Assert.False(Checked(d, ((Win32.Dialogs.SettingsUi.Pages.DiagnosticsSettingsPage)d.Pages[6]).Ids.Logging));
            GoTo(d, 1);
            Assert.False(Checked(d, ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids.ShowFolderInfo));
            d.Close();
        }, realizeAll: false);
    }

    [Fact]
    public void SavingWithoutVisitingAPage_KeepsThatPagesValues()
    {
        var (_, dialog) = Show(new AppSettings { LoggingEnabled = true, ShowFolderInfo = true, ToolbarOpacityPercent = 45 }, (d, _) =>
        {
            Assert.True(d.TrySaveAll());
            d.Close();
        }, realizeAll: false);

        Assert.True(dialog.Settings.LoggingEnabled);
        Assert.True(dialog.Settings.ShowFolderInfo);
        Assert.Equal(45, dialog.Settings.ToolbarOpacityPercent);
    }

    [Fact]
    public void ALanguageChange_BeforeAPageIsOpened_LocalizesItWhenItIsCreated()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            GoTo(d, 1);
            var ids = ((Win32.Dialogs.SettingsUi.Pages.DisplaySettingsPage)d.Pages[1]).Ids;
            Assert.Equal(Tr.SettingsShowInfoOverlayLabel, Txt(d, ids.ShowInfoOverlay));
            Assert.Equal(Tr.SettingsLoadingModeLabel, Txt(d, d.PageItems(1).First(i => i.Id == ids.LoadingMode - 1).Id));
            Assert.Equal(Tr.EnumLoadingModeFast, ComboText(d, ids.LoadingMode, 0));
            d.Close();
        }, realizeAll: false);
    }

    [Fact]
    public void ModalDisablesTheOwner_AndRestoresItOnClose()
    {
        var owner = 0;
        Show(new AppSettings(), (d, o) =>
        {
            owner = (int)o;
            Assert.False(User32.IsWindowEnabled(o));
            d.Close();
        });
        Assert.NotEqual(0, owner);
    }
}
