using System.Globalization;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Updates;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: nội dung của ba trang đầu (General, Display, Diagnostics - 20 thuộc tính). Mỗi thuộc tính của trang có ca Load -&gt; Collect
/// -&gt; Save (qua <c>SettingsFieldMap</c>/<c>SettingsPresenter</c> thật), thứ tự mục combobox, kiểm hợp lệ theo trường, phụ thuộc bật/tắt,
/// và các hành động của trang (cập nhật, Explorer, ngôn ngữ).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsPagesTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public SettingsPagesTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    private static GeneralSettingsPage General(SettingsDialog d) => (GeneralSettingsPage)d.Pages[0];

    private static DisplaySettingsPage DisplayPage(SettingsDialog d) => (DisplaySettingsPage)d.Pages[1];

    private static DiagnosticsSettingsPage Diagnostics(SettingsDialog d) => (DiagnosticsSettingsPage)d.Pages[6];

    /// <summary>Mở với <paramref name="settings"/>, chạy Save (TrySaveAll) và trả bản làm việc đã ghi.</summary>
    private static AppSettings Saved(AppSettings settings, Action<SettingsDialog>? edit = null)
    {
        AppSettings? result = null;
        Show(settings, (d, _) =>
        {
            edit?.Invoke(d);
            Assert.True(d.TrySaveAll(), "the values must be accepted");
            result = d.Settings;
            d.Close();
        });
        return result!;
    }

    // ---- guard: mỗi thuộc tính của trang nằm trong bản đồ trường ----------------------------------------------------------------

    private static readonly string[] GeneralProperties =
        [nameof(AppSettings.ImageSortMode), nameof(AppSettings.InitialViewMode), nameof(AppSettings.InstanceMode), nameof(AppSettings.UiLanguage)];

    private static readonly string[] DisplayProperties =
    [
        nameof(AppSettings.ShowInfoOverlay), nameof(AppSettings.ShowFileInfo), nameof(AppSettings.ShowFolderInfo),
        nameof(AppSettings.InfoOverlayFontSize), nameof(AppSettings.ShowZoomIndicator), nameof(AppSettings.ToolbarAutoHide),
        nameof(AppSettings.ToolbarAutoHideDelayMs), nameof(AppSettings.InfoOverlayAutoHide), nameof(AppSettings.InfoOverlayAutoHideDelayMs),
        nameof(AppSettings.ToolbarOpacityPercent), nameof(AppSettings.LoadingMode), nameof(AppSettings.ScalingQuality),
        nameof(AppSettings.ShowExifInfo), nameof(AppSettings.ExifInfoFields), nameof(AppSettings.TitleBarFields),
    ];

    private static readonly string[] DiagnosticsProperties = [nameof(AppSettings.LoggingEnabled)];

    [Fact]
    public void EveryPagesProperty_IsOwnedByTheFieldMap_AndNoTwoPagesShareOne()
    {
        var all = GeneralProperties.Concat(DisplayProperties).Concat(DiagnosticsProperties).ToList();
        Assert.All(all, property => Assert.Contains(property, SettingsFieldMap.CoveredProperties));
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.True(all.Count >= 15, "the first three pages carry at least 15 settings");
    }

    // ================= General =======================================================================================================

    [Theory]
    [InlineData(InitialViewMode.Fit, 0)]
    [InlineData(InitialViewMode.FitWidth, 1)]
    [InlineData(InitialViewMode.FitHeight, 2)]
    [InlineData(InitialViewMode.ClickZoomLevel, 3)]
    [InlineData(InitialViewMode.Percent100, 4)]
    [InlineData(InitialViewMode.Percent200, 5)]
    public void General_ViewMode_ComboOrderMatchesTheFormIndex(InitialViewMode mode, int index)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var saved = Saved(new AppSettings { InitialViewMode = mode });
        Assert.Equal(mode, saved.InitialViewMode);
        Show(new AppSettings { InitialViewMode = mode }, (d, _) =>
        {
            var ids = General(d).Ids;
            Assert.Equal(index, SettingsFieldMap.ViewModeToIndex(mode));
            Assert.Equal(index, ComboIndex(d, ids.ViewMode));
            Assert.Equal(index, d.CollectAll().ViewModeIndex);
            Assert.Equal(6, ComboCount(d, ids.ViewMode));
            Assert.Equal("100%", ComboText(d, ids.ViewMode, 4));
            Assert.Equal("200%", ComboText(d, ids.ViewMode, 5));
            d.Close();
        });
    }

    [Fact]
    public void General_SortMode_EveryTagRoundTrips_InComboOrder()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Assert.Equal(6, GeneralSettingsPage.SortTags.Length);
        for (var i = 0; i < GeneralSettingsPage.SortTags.Length; i++)
        {
            var mode = Enum.Parse<ImageSortMode>(GeneralSettingsPage.SortTags[i]);
            Show(new AppSettings { ImageSortMode = mode }, (d, _) =>
            {
                var ids = General(d).Ids;
                Assert.Equal(i, ComboIndex(d, ids.SortMode));
                Assert.Equal(GeneralSettingsPage.SortTags[i], d.CollectAll().SortModeTag);
                d.Close();
            });
            Assert.Equal(mode, Saved(new AppSettings { ImageSortMode = mode }).ImageSortMode);
        }

        Show(new AppSettings(), (d, _) =>
        {
            var ids = General(d).Ids;
            Assert.Equal(Tr.EnumImageSortModeNameAscending, ComboText(d, ids.SortMode, 1));
            Assert.Equal(Tr.EnumImageSortModeDefault, ComboText(d, ids.SortMode, 5));
            d.Close();
        });
    }

    [Theory]
    [InlineData(InstanceMode.SingleWindow, 0)]
    [InlineData(InstanceMode.PerFolder, 1)]
    public void General_InstanceMode_RoundTrips(InstanceMode mode, int index)
    {
        Show(new AppSettings { InstanceMode = mode }, (d, _) =>
        {
            Assert.Equal(index, ComboIndex(d, General(d).Ids.InstanceMode));
            d.Close();
        });
        Assert.Equal(mode, Saved(new AppSettings { InstanceMode = mode }).InstanceMode);
    }

    [Fact]
    public void General_SortMode_EveryComboItem_SavesTheModeItsTextNames()
    {
        // Pairs the item TEXT (what the user reads) with the mode that is saved: the tag array and the combo list must agree.
        using var scope = TestLocalization.Use(TestLocalization.English);
        var expected = new (string Text, ImageSortMode Mode)[]
        {
            (Tr.EnumImageSortModeName, ImageSortMode.Name),
            (Tr.EnumImageSortModeNameAscending, ImageSortMode.NameAscending),
            (Tr.EnumImageSortModeNameDescending, ImageSortMode.NameDescending),
            (Tr.EnumImageSortModeSizeDescending, ImageSortMode.SizeDescending),
            (Tr.EnumImageSortModeSizeAscending, ImageSortMode.SizeAscending),
            (Tr.EnumImageSortModeDefault, ImageSortMode.Default),
        };
        for (var i = 0; i < expected.Length; i++)
        {
            var index = i;
            var saved = Saved(new AppSettings { ImageSortMode = ImageSortMode.Name }, d =>
            {
                var ids = General(d).Ids;
                Assert.Equal(expected[index].Text, ComboText(d, ids.SortMode, index));
                Choose(d, ids.SortMode, index);
            });
            Assert.Equal(expected[i].Mode, saved.ImageSortMode);
        }
    }

    [Fact]
    public void General_EditingCombos_WritesThroughToTheSettings()
    {
        var saved = Saved(new AppSettings(), d =>
        {
            var ids = General(d).Ids;
            Choose(d, ids.SortMode, 3);
            Choose(d, ids.ViewMode, 2);
            Choose(d, ids.InstanceMode, 1);
        });

        Assert.Equal(ImageSortMode.SizeDescending, saved.ImageSortMode);
        Assert.Equal(InitialViewMode.FitHeight, saved.InitialViewMode);
        Assert.Equal(InstanceMode.PerFolder, saved.InstanceMode);
    }

    [Fact]
    public void General_WithoutALocalizationService_HidesTheLanguageGroup()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var ids = General(d).Ids;
            Assert.False(Visible(d, ids.LanguageGroup));
            Assert.False(Visible(d, ids.Language));
            Assert.False(Visible(d, ids.OpenFolder));
            d.Close();
        });
    }

    [Fact]
    public void General_LanguagePicker_ListsTheCatalogs_AndSaveSwitchesTheLanguage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        using var temp = new TempRoot("settings-lang");
        var service = new LocalizationService(new Paths(Path.Combine(temp.Path, "cfg", "config.json")), new PhysicalFileSystem(), NullLog.Instance);
        var (_, dialog) = OnSta(() =>
        {
            var dlg = new SettingsDialog(new AppSettings(), null, null, service, null, Options(new FakeWarnings(), new FakeFiles()));
            dlg.Presenter.ShellIntegration = new FakeShell();
            dlg.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            var result = ShowOnOwner(dlg, (d, _) =>
            {
                var page = General((SettingsDialog)d);
                var ids = page.Ids;
                Assert.True(Visible((SettingsDialog)d, ids.Language));
                var codes = page.Languages.Select(l => l.Code).ToList();
                Assert.Equal("auto", codes[0]);
                Assert.Contains("en", codes);
                Assert.Contains("vi", codes);
                Assert.Equal(codes.Count, ComboCount((SettingsDialog)d, ids.Language));
                // The first entry (automatic) is a real choice: index 0 must be collected like any other.
                Assert.Equal("auto", ((SettingsDialog)d).CollectAll().SelectedLanguage!.Code);
                Choose((SettingsDialog)d, ids.Language, codes.IndexOf("vi"));
                Assert.Equal("vi", ((SettingsDialog)d).CollectAll().SelectedLanguage!.Code);
                Assert.True(((SettingsDialog)d).TrySaveAll());
                Drain(d);
                d.Close();
            });
            return (result, dlg);
        });

        Assert.Equal("vi", dialog.Settings.UiLanguage);
        Assert.Equal("vi", Localizer.Current.Code);
    }

    private sealed class Paths(string configFile) : IAppPaths
    {
        public string ConfigFile => configFile;

        public string JournalFile => throw new NotSupportedException();

        public string SessionsDir => throw new NotSupportedException();

        public string LogFile => throw new NotSupportedException();

        public string PreviewCacheDir => throw new NotSupportedException();

        public string ThumbnailCacheDir => throw new NotSupportedException();

        public string WindowPlacementFile => throw new NotSupportedException();
    }

    [Fact]
    public async Task General_UpdateCheck_ShowsTheResult_AndOffersTheDownloadPage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var opened = new List<string>();
        var updates = new FakeUpdates(() => UpdateCheckResult.UpdateAvailable("9.9.9", "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v9.9.9"));
        var options = Options(new FakeWarnings(), new FakeFiles()) with { OpenUrl = opened.Add };
        Task? pending = null;
        var snapshot = OnSta(() =>
        {
            var dialog = new SettingsDialog(new AppSettings(), null, null, null, updates, options);
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            (string Checking, string Status, bool PageVisible, bool CheckEnabled) state = default;
            ShowOnOwner(dialog, (d, _) =>
            {
                var sd = (SettingsDialog)d;
                var ids = General(sd).Ids;
                Assert.False(Visible(sd, ids.OpenUpdatePage));
                Command(sd, ids.CheckUpdate, 0);
                var checking = Txt(sd, ids.UpdateStatus);
                pending = General(sd).UpdateCheckTask;
                pending.GetAwaiter().GetResult();
                Drain(sd); // the continuation posts the display back to the UI thread
                state = (checking, Txt(sd, ids.UpdateStatus), Visible(sd, ids.OpenUpdatePage), Enabled(sd, ids.CheckUpdate));
                Command(sd, ids.OpenUpdatePage, 0);
                d.Close();
            });
            return state;
        });

        await pending!;
        Assert.Equal(Tr.UpdateCheckChecking, snapshot.Checking);
        Assert.Equal(Tr.UpdateStatusAvailable("9.9.9", "2.0.1"), snapshot.Status);
        Assert.True(snapshot.PageVisible);
        Assert.True(snapshot.CheckEnabled);
        Assert.Equal(1, updates.Calls);
        Assert.Equal("2.0.1+test", updates.LastVersion);
        Assert.Equal(["https://github.com/ConanKLOP2/PhotoReview/releases/tag/v9.9.9"], opened);
    }

    [Fact]
    public void General_ShellIntegration_AddAndRemoveApplyImmediately_AndShowTheStatus()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var shell = new FakeShell();
        Show(new AppSettings(), (d, _) =>
        {
            var ids = General(d).Ids;
            Assert.Equal(Tr.SettingsShellStatusNotRegistered, Txt(d, ids.ShellStatus));
            Assert.True(Enabled(d, ids.ShellAdd));
            Assert.False(Enabled(d, ids.ShellRemove));
            Command(d, ids.ShellAdd, 0);
            Assert.Equal(["register:C:\\Apps\\PhotoReview\\PhotoReview.exe"], shell.Calls);
            Assert.Equal(Tr.SettingsShellStatusRegistered, Txt(d, ids.ShellStatus));
            Assert.False(Enabled(d, ids.ShellAdd));
            Assert.True(Enabled(d, ids.ShellRemove));
            Command(d, ids.ShellRemove, 0);
            Assert.Equal("unregister", shell.Calls[^1]);
            d.Close();
        }, prepare: d => d.Presenter.ShellIntegration = shell);
    }

    [Fact]
    public void General_ShellIntegration_WithAMovedExe_OffersUpdatePath()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var shell = new FakeShell { State = ShellMenuState.RegisteredElsewhere };
        Show(new AppSettings(), (d, _) =>
        {
            var ids = General(d).Ids;
            Assert.Equal(Tr.SettingsShellUpdate, Txt(d, ids.ShellAdd));
            Assert.True(Enabled(d, ids.ShellAdd));
            d.Close();
        }, prepare: d => d.Presenter.ShellIntegration = shell);
    }

    [Fact]
    public void General_OpenLanguagesFolder_AsksExplorerForTheFolder()
    {
        using var temp = new TempRoot("settings-langdir");
        var explorer = new List<string>();
        var service = new LocalizationService(new Paths(Path.Combine(temp.Path, "cfg", "config.json")), new PhysicalFileSystem(), NullLog.Instance);
        OnSta(() =>
        {
            var dlg = new SettingsDialog(new AppSettings(), null, null, service, null, Options(new FakeWarnings(), new FakeFiles(), explorer));
            dlg.Presenter.ShellIntegration = new FakeShell();
            ShowOnOwner(dlg, (d, _) =>
            {
                Command((SettingsDialog)d, General((SettingsDialog)d).Ids.OpenFolder, 0);
                d.Close();
            });
            return 0;
        });

        var arguments = Assert.Single(explorer);
        Assert.Equal($"\"{service.UserLanguagesDir}\"", arguments);
        Assert.True(Directory.Exists(service.UserLanguagesDir));
    }

    // ================= Display =======================================================================================================

    private static AppSettings NonDefaultDisplay() => new()
    {
        ShowInfoOverlay = false,
        ShowFileInfo = false,
        ShowFolderInfo = true,
        InfoOverlayFontSize = 17.5,
        ShowZoomIndicator = true,
        ToolbarAutoHide = true,
        ToolbarAutoHideDelayMs = 4321,
        InfoOverlayAutoHide = true,
        InfoOverlayAutoHideDelayMs = 2345,
        ToolbarOpacityPercent = 63,
        LoadingMode = LoadingMode.Original,
        ScalingQuality = ScalingQuality.Linear,
        ShowExifInfo = false,
        ExifInfoFields = ExifInfoFields.DateTaken | ExifInfoFields.Lens | ExifInfoFields.ShutterSpeed,
        TitleBarFields = TitleBarFields.FolderPath | TitleBarFields.FileSize | TitleBarFields.Iso,
    };

    [Fact]
    public void Display_LoadsEveryProperty_AndSaveWritesThemBackUnchanged()
    {
        var source = NonDefaultDisplay();
        Show(source, (d, _) =>
        {
            var ids = DisplayPage(d).Ids;
            Assert.False(Checked(d, ids.ShowInfoOverlay));
            Assert.False(Checked(d, ids.ShowFileInfo));
            Assert.True(Checked(d, ids.ShowFolderInfo));
            Assert.Equal("17.5", Txt(d, ids.FontSize));
            Assert.True(Checked(d, ids.ShowZoomIndicator));
            Assert.True(Checked(d, ids.ToolbarAutoHide));
            Assert.Equal("4321", Txt(d, ids.ToolbarDelay));
            Assert.True(Checked(d, ids.InfoAutoHide));
            Assert.Equal("2345", Txt(d, ids.InfoDelay));
            Assert.Equal(63, ((int)User32.SendMessage(H(d, ids.Opacity), ControlConstants.TbmGetPos, 0, 0)));
            Assert.Equal(2, ComboIndex(d, ids.LoadingMode));
            Assert.Equal(1, ComboIndex(d, ids.ScalingQuality));
            Assert.False(Checked(d, ids.ShowExif));
            Assert.Equal(["DateTaken", "Lens", "ShutterSpeed"], CheckedNames(d, DisplayPage(d).ExifChecks, ExifNames));
            Assert.Equal(["FolderPath", "FileSize", "Iso"], CheckedNames(d, DisplayPage(d).TitleChecks, TitleNames));
            Assert.True(d.TrySaveAll());
            var s = d.Settings;
            Assert.False(s.ShowInfoOverlay);
            Assert.False(s.ShowFileInfo);
            Assert.True(s.ShowFolderInfo);
            Assert.Equal(17.5, s.InfoOverlayFontSize);
            Assert.True(s.ShowZoomIndicator);
            Assert.True(s.ToolbarAutoHide);
            Assert.Equal(4321, s.ToolbarAutoHideDelayMs);
            Assert.True(s.InfoOverlayAutoHide);
            Assert.Equal(2345, s.InfoOverlayAutoHideDelayMs);
            Assert.Equal(63, s.ToolbarOpacityPercent);
            Assert.Equal(LoadingMode.Original, s.LoadingMode);
            Assert.Equal(ScalingQuality.Linear, s.ScalingQuality);
            Assert.False(s.ShowExifInfo);
            Assert.Equal(source.ExifInfoFields, s.ExifInfoFields);
            Assert.Equal(source.TitleBarFields, s.TitleBarFields);
            d.Close();
        });
    }

    private static readonly string[] ExifNames =
        ["FileName", "DateTaken", "ModifiedDate", "Dimensions", "Camera", "Lens", "Iso", "FocalLength", "Aperture", "ShutterSpeed"];

    private static readonly string[] TitleNames =
    [
        "FolderName", "FolderPath", "IndexCount", "FileName", "FileSize", "Dimensions", "ModifiedDate", "DateTaken", "Camera", "Lens", "Iso",
        "FocalLength", "Aperture", "ShutterSpeed",
    ];

    private static string[] CheckedNames(SettingsDialog d, IReadOnlyList<int> ids, string[] names)
        => ids.Select((id, i) => (id, i)).Where(x => Checked(d, x.id)).Select(x => names[x.i]).ToArray();

    [Fact]
    public void Display_EveryExifAndTitleFlag_IsItsOwnCheckBox_InTheDocumentedOrder()
    {
        // one flag at a time: tick exactly one box, the saved flags equal exactly that member
        var exifFlags = new[]
        {
            ExifInfoFields.FileName, ExifInfoFields.DateTaken, ExifInfoFields.ModifiedDate, ExifInfoFields.Dimensions, ExifInfoFields.Camera,
            ExifInfoFields.Lens, ExifInfoFields.Iso, ExifInfoFields.FocalLength, ExifInfoFields.Aperture, ExifInfoFields.ShutterSpeed,
        };
        var titleFlags = new[]
        {
            TitleBarFields.FolderName, TitleBarFields.FolderPath, TitleBarFields.IndexCount, TitleBarFields.FileName, TitleBarFields.FileSize,
            TitleBarFields.Dimensions, TitleBarFields.ModifiedDate, TitleBarFields.DateTaken, TitleBarFields.Camera, TitleBarFields.Lens,
            TitleBarFields.Iso, TitleBarFields.FocalLength, TitleBarFields.Aperture, TitleBarFields.ShutterSpeed,
        };
        for (var i = 0; i < exifFlags.Length; i++)
        {
            var flag = exifFlags[i];
            var saved = Saved(new AppSettings { ExifInfoFields = ExifInfoFields.None, ShowExifInfo = true }, d =>
            {
                GoTo(d, 1);
                Tick(d, DisplayPage(d).ExifChecks[i], true);
            });
            Assert.Equal(flag, saved.ExifInfoFields);
        }

        for (var i = 0; i < titleFlags.Length; i++)
        {
            var flag = titleFlags[i];
            var saved = Saved(new AppSettings { TitleBarFields = TitleBarFields.None }, d =>
            {
                GoTo(d, 1);
                Tick(d, DisplayPage(d).TitleChecks[i], true);
            });
            Assert.Equal(flag, saved.TitleBarFields);
        }
    }

    [Fact]
    public void Display_SubOptions_FollowTheirParents()
    {
        Show(new AppSettings { ShowInfoOverlay = true, ToolbarAutoHide = false, InfoOverlayAutoHide = false, ShowExifInfo = true }, (d, _) =>
        {
            GoTo(d, 1);
            var ids = DisplayPage(d).Ids;
            Assert.True(Enabled(d, ids.ShowFileInfo));
            Assert.False(Enabled(d, ids.ToolbarDelay));
            Assert.False(Enabled(d, ids.InfoDelay));
            Assert.All(DisplayPage(d).ExifChecks, id => Assert.True(Enabled(d, id)));

            Tick(d, ids.ShowInfoOverlay, false);
            Assert.False(Enabled(d, ids.ShowFileInfo));
            Assert.False(Enabled(d, ids.ShowFolderInfo));
            Tick(d, ids.ToolbarAutoHide, true);
            Assert.True(Enabled(d, ids.ToolbarDelay));
            Tick(d, ids.InfoAutoHide, true);
            Assert.True(Enabled(d, ids.InfoDelay));
            Tick(d, ids.ShowExif, false);
            Assert.All(DisplayPage(d).ExifChecks, id => Assert.False(Enabled(d, id)));
            d.Close();
        });
    }

    [Fact]
    public void Display_OpacitySlider_MovesTheValueText_AndTheSavedPercent()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings { ToolbarOpacityPercent = 80 }, (d, _) =>
        {
            GoTo(d, 1);
            var ids = DisplayPage(d).Ids;
            Assert.Equal("80 %", Txt(d, ids.OpacityValue));
            User32.SendMessage(H(d, ids.Opacity), ControlConstants.TbmSetPos, 1, 55);
            User32.SendMessage(d.Hwnd, ControlConstants.WmHScroll, ControlConstants.TbEndTrack, H(d, ids.Opacity));
            Assert.Equal("55 %", Txt(d, ids.OpacityValue));
            Assert.True(d.TrySaveAll());
            Assert.Equal(55, d.Settings.ToolbarOpacityPercent);
            d.Close();
        });
    }

    [Fact]
    public void Display_SliderKeepsTheDocumentedRange()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var ids = DisplayPage(d).Ids;
            var slider = H(d, ids.Opacity);
            Assert.Equal(AppSettings.MinToolbarOpacityPercent, (int)User32.SendMessage(slider, ControlConstants.TbmGetRangeMin, 0, 0));
            Assert.Equal(AppSettings.MaxToolbarOpacityPercent, (int)User32.SendMessage(slider, ControlConstants.TbmGetRangeMax, 0, 0));
            d.Close();
        });
    }

    [Theory]
    [InlineData("fontSize", "abc")]
    [InlineData("fontSize", "0")]
    [InlineData("toolbarDelay", "-5")]
    [InlineData("infoDelay", "x")]
    public void Display_InvalidValues_AreRefused_WithThePresentersMessage_AndNothingIsSaved(string field, string text)
    {
        var warnings = new FakeWarnings();
        var (_, dialog) = Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 1);
            var ids = DisplayPage(d).Ids;
            var id = field switch { "fontSize" => ids.FontSize, "toolbarDelay" => ids.ToolbarDelay, _ => ids.InfoDelay };
            SetText(d, id, text);
            Assert.False(d.TrySaveAll());
            d.Close();
        }, Options(warnings, new FakeFiles()));

        Assert.False(dialog.Saved);
        var shown = Assert.Single(warnings.Shown);
        var expected = field switch
        {
            "fontSize" => Tr.DialogSettingsInvalidInfoOverlayFontSize(AppSettings.MinInfoOverlayFontSize, AppSettings.MaxInfoOverlayFontSize),
            "toolbarDelay" => Tr.DialogSettingsInvalidToolbarAutoHideDelay(AppSettings.MinToolbarAutoHideDelayMs, AppSettings.MaxToolbarAutoHideDelayMs),
            _ => Tr.DialogSettingsInvalidInfoOverlayAutoHideDelay(AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs),
        };
        Assert.Equal(expected, shown.Message);
    }

    [Fact]
    public void Display_ComboOrders_MatchTheFormIndices()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            var ids = DisplayPage(d).Ids;
            Assert.Equal(3, ComboCount(d, ids.LoadingMode));
            Assert.Equal(Tr.EnumLoadingModeFast, ComboText(d, ids.LoadingMode, 0));
            Assert.Equal(Tr.EnumLoadingModePreview, ComboText(d, ids.LoadingMode, 1));
            Assert.Equal(Tr.SettingsLoadingModeOriginalWithHint, ComboText(d, ids.LoadingMode, 2));
            Assert.Equal(2, ComboCount(d, ids.ScalingQuality));
            Assert.Equal(Tr.EnumScalingQualityHighQuality, ComboText(d, ids.ScalingQuality, 0));
            d.Close();
        });

        foreach (var (mode, index) in new[] { (LoadingMode.Fast, 0), (LoadingMode.Preview, 1), (LoadingMode.Original, 2) })
        {
            Assert.Equal(mode, Saved(new AppSettings { LoadingMode = mode }).LoadingMode);
            Show(new AppSettings { LoadingMode = mode }, (d, _) =>
            {
                Assert.Equal(index, ComboIndex(d, DisplayPage(d).Ids.LoadingMode));
                d.Close();
            });
        }
    }

    // ================= Diagnostics ===================================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Diagnostics_Logging_RoundTrips(bool enabled)
    {
        Show(new AppSettings { LoggingEnabled = enabled }, (d, _) =>
        {
            GoTo(d, 6);
            Assert.Equal(enabled, Checked(d, Diagnostics(d).Ids.Logging));
            d.Close();
        });
        Assert.Equal(enabled, Saved(new AppSettings { LoggingEnabled = enabled }).LoggingEnabled);
        Assert.Equal(!enabled, Saved(new AppSettings { LoggingEnabled = enabled }, d => Tick(d, Diagnostics(d).Ids.Logging, !enabled)).LoggingEnabled);
    }

    [Fact]
    public void Diagnostics_OpenLogLocation_AsksExplorerToSelectTheLogFile()
    {
        var explorer = new List<string>();
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 6);
            Command(d, Diagnostics(d).Ids.OpenLog, 0);
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), explorer));

        var arguments = Assert.Single(explorer);
        Assert.StartsWith("/select,\"", arguments, StringComparison.Ordinal);
        Assert.EndsWith(".log\"", arguments, StringComparison.OrdinalIgnoreCase);
    }

    // ================= the pages that are not done yet keep their values ==============================================================

    [Fact]
    public void AnUnportedPage_ShowsANotice_AndSaveKeepsItsSettingsUnchanged()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var original = new AppSettings
        {
            ClickZoomPercent = 150,
            ConfirmBeforeDelete = true,
            ExternalEditorPath = @"C:\Tools\edit.exe",
            PreloadForwardCount = 7,
            ImageTransition = ImageTransition.Fade,
        };
        original.Shortcuts.Next = "N";
        var saved = Saved(original, d =>
        {
            var mouse = SettingsPresenter.Pages.ToList().FindIndex(p => p.Key == "Mouse");
            GoTo(d, mouse);
            Assert.IsType<PendingSettingsPage>(d.Pages[mouse]);
        });

        Assert.Equal(150, saved.ClickZoomPercent);
        Assert.True(saved.ConfirmBeforeDelete);
        Assert.Equal(@"C:\Tools\edit.exe", saved.ExternalEditorPath);
        Assert.Equal(7, saved.PreloadForwardCount);
        Assert.Equal(ImageTransition.Fade, saved.ImageTransition);
        Assert.Equal("N", saved.Shortcuts.Next);
    }
}
