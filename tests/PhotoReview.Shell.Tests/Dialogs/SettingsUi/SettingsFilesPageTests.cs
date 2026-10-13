using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding.Wic;
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
/// WP-35 GĐ3b (trang Files): từng thuộc tính của trang (Load -&gt; Collect -&gt; Save qua <c>SettingsFieldMap</c> thật), thứ tự combobox,
/// radio nhật ký (nhóm, Tab, mũi tên), xoá vĩnh viễn (mặc định TẮT, Defaults/Reset), đường dẫn trình soạn thảo (rỗng/dấu cách/tiếng Việt/
/// không hợp lệ), Browse (fake <see cref="IShellFileDialog"/>), hook FocusExternalEditor, đổi ngôn ngữ, rò handle; cộng khối kiểm
/// <c>SettingsPageBuilder.Radio</c> (cờ WS_GROUP/WS_TABSTOP, cắt nhóm, tham số).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsFilesPageTests : IDisposable
{
    private const int FilesIndex = 5;
    private const uint BmClick = 0x00F5;
    private const uint VkTab = 0x09;
    private const uint VkDown = 0x28;
    private const uint VkUp = 0x26;
    private readonly DataRootFixture _data = new();

    public SettingsFilesPageTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    private static FilesSettingsPage FilesPage(SettingsDialog d) => (FilesSettingsPage)d.Pages[FilesIndex];

    private static AppSettings Saved(AppSettings settings, Action<SettingsDialog>? edit = null)
    {
        AppSettings? result = null;
        Show(settings, (d, _) =>
        {
            GoTo(d, FilesIndex);
            edit?.Invoke(d);
            Assert.True(d.TrySaveAll(), "the values must be accepted");
            result = d.Settings;
            d.Close();
        });
        return result!;
    }

    private static void Click(SettingsDialog d, int id) => User32.SendMessage(H(d, id), BmClick, 0, 0);

    private sealed class RecordingFiles : IShellFileDialog
    {
        public string? OpenResult { get; set; }

        public Exception? Throw { get; set; }

        public List<(string Title, IReadOnlyList<FileDialogFilter> Filters, string? InitialFolder)> Opens { get; } = [];

        public string? PickOpenFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder)
        {
            Opens.Add((title, filters, initialFolder));
            return Throw is null ? OpenResult : throw Throw;
        }

        public string? PickSaveFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? defaultExtension,
            string? suggestedFileName, string? initialFolder) => throw new NotSupportedException();
    }

    /// <summary>Mọi giá trị KHÁC mặc định, mỗi thuộc tính của trang một giá trị riêng.</summary>
    private static AppSettings NonDefault() => new()
    {
        CompareHashEnabled = false,
        CompareSizeEnabled = false,
        RawSupportEnabled = !new AppSettings().RawSupportEnabled,
        RawFullDecode = RawFullDecode.OnZoom,
        RawPairMode = RawPairMode.PreferRaw,
        WebpHeicSupportEnabled = !new AppSettings().WebpHeicSupportEnabled,
        AllowPermanentDeleteWithoutRecycleBin = true,
        ConfirmBeforeDelete = true,
        ExternalEditorPath = @"C:\Tools\Edit Photo\editor.exe",
        JournalDurability = JournalDurability.PowerLossSafe,
        MoveCopyReuseLastFolder = true,
    };

    private static readonly string[] OwnedProperties =
    [
        nameof(AppSettings.CompareHashEnabled), nameof(AppSettings.CompareSizeEnabled), nameof(AppSettings.RawSupportEnabled),
        nameof(AppSettings.RawFullDecode), nameof(AppSettings.RawPairMode), nameof(AppSettings.WebpHeicSupportEnabled),
        nameof(AppSettings.AllowPermanentDeleteWithoutRecycleBin), nameof(AppSettings.ConfirmBeforeDelete),
        nameof(AppSettings.ExternalEditorPath), nameof(AppSettings.JournalDurability), nameof(AppSettings.MoveCopyReuseLastFolder),
    ];

    // ---- thuộc tính -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryProperty_IsOwnedByTheFieldMap_And_TheNonDefaultSampleDiffersOnEachOfThem()
    {
        Assert.Equal(11, OwnedProperties.Length);
        Assert.Equal(OwnedProperties.Length, OwnedProperties.Distinct(StringComparer.Ordinal).Count());
        Assert.All(OwnedProperties, property => Assert.Contains(property, SettingsFieldMap.CoveredProperties));
        var defaults = new AppSettings();
        var sample = NonDefault();
        foreach (var name in OwnedProperties)
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            Assert.NotEqual(property.GetValue(defaults), property.GetValue(sample));
        }
    }

    [Fact]
    public void Files_LoadsEveryProperty_IntoItsControl_AndSavesThemBackUnchanged()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var source = NonDefault();
        Show(source, (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            var ids = page.Ids;
            Assert.False(Checked(d, ids.CompareHash));
            Assert.False(Checked(d, ids.CompareSize));
            Assert.Equal(source.RawSupportEnabled, Checked(d, ids.RawSupport));
            Assert.Equal(1, ComboIndex(d, ids.RawFullDecode));
            Assert.Equal(2, ComboIndex(d, ids.RawPairMode));
            Assert.Equal(source.WebpHeicSupportEnabled, Checked(d, ids.WebpHeic));
            Assert.True(Checked(d, ids.AllowPermanentDelete));
            Assert.True(Checked(d, ids.ConfirmBeforeDelete));
            Assert.Equal(@"C:\Tools\Edit Photo\editor.exe", Txt(d, ids.EditorPath));
            Assert.False(Checked(d, page.JournalRadios[0]));
            Assert.True(Checked(d, page.JournalRadios[1]));
            Assert.True(Checked(d, ids.ReuseLastFolder));
            d.Close();
        });

        var saved = Saved(NonDefault());
        var expected = NonDefault();
        Assert.Equal(expected.CompareHashEnabled, saved.CompareHashEnabled);
        Assert.Equal(expected.CompareSizeEnabled, saved.CompareSizeEnabled);
        Assert.Equal(expected.RawSupportEnabled, saved.RawSupportEnabled);
        Assert.Equal(expected.RawFullDecode, saved.RawFullDecode);
        Assert.Equal(expected.RawPairMode, saved.RawPairMode);
        Assert.Equal(expected.WebpHeicSupportEnabled, saved.WebpHeicSupportEnabled);
        Assert.Equal(expected.AllowPermanentDeleteWithoutRecycleBin, saved.AllowPermanentDeleteWithoutRecycleBin);
        Assert.Equal(expected.ConfirmBeforeDelete, saved.ConfirmBeforeDelete);
        Assert.Equal(expected.ExternalEditorPath, saved.ExternalEditorPath);
        Assert.Equal(expected.JournalDurability, saved.JournalDurability);
        Assert.Equal(expected.MoveCopyReuseLastFolder, saved.MoveCopyReuseLastFolder);
    }

    [Fact]
    public void Files_TheDefaults_LoadAsTheWpfPageDid_AndSaveBackIdentically()
    {
        var defaults = new AppSettings();
        Show(defaults, (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            var ids = page.Ids;
            Assert.Equal(defaults.CompareHashEnabled, Checked(d, ids.CompareHash));
            Assert.Equal(defaults.CompareSizeEnabled, Checked(d, ids.CompareSize));
            Assert.False(Checked(d, ids.AllowPermanentDelete), "permanent delete is opt-in");
            Assert.False(Checked(d, ids.ConfirmBeforeDelete));
            Assert.Equal(string.Empty, Txt(d, ids.EditorPath));
            Assert.True(Checked(d, page.JournalRadios[0]));
            Assert.False(Checked(d, page.JournalRadios[1]));
            d.Close();
        });
        var saved = Saved(new AppSettings());
        Assert.False(saved.AllowPermanentDeleteWithoutRecycleBin);
        Assert.Equal(JournalDurability.Fast, saved.JournalDurability);
        Assert.Equal(string.Empty, saved.ExternalEditorPath);
    }

    private static readonly bool[] TickBaseline = [true, true, true, true, false, false, false];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Files_EachTickBox_WritesOnlyItsOwnProperty(int which)
    {
        // The seven tick boxes in page order; flipping ONE must change exactly that property of the saved settings.
        var baseline = new AppSettings
        {
            CompareHashEnabled = true,
            CompareSizeEnabled = true,
            RawSupportEnabled = true,
            WebpHeicSupportEnabled = true,
            AllowPermanentDeleteWithoutRecycleBin = false,
            ConfirmBeforeDelete = false,
            MoveCopyReuseLastFolder = false,
        };
        var saved = Saved(baseline, d =>
        {
            var ids = FilesPage(d).Ids;
            int[] boxes = [ids.CompareHash, ids.CompareSize, ids.RawSupport, ids.WebpHeic, ids.AllowPermanentDelete, ids.ConfirmBeforeDelete, ids.ReuseLastFolder];
            var box = boxes[which];
            Tick(d, box, !Checked(d, box));
        });

        bool[] values =
        [
            saved.CompareHashEnabled, saved.CompareSizeEnabled, saved.RawSupportEnabled, saved.WebpHeicSupportEnabled,
            saved.AllowPermanentDeleteWithoutRecycleBin, saved.ConfirmBeforeDelete, saved.MoveCopyReuseLastFolder,
        ];
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(TickBaseline[i] != (i == which), values[i]);
        }
    }

    [Theory]
    [InlineData(0, RawFullDecode.Never)]
    [InlineData(1, RawFullDecode.OnZoom)]
    public void Files_RawFullDecode_ComboOrder_MatchesTheEnum(int index, RawFullDecode expected)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var saved = Saved(new AppSettings { RawFullDecode = RawFullDecode.Never }, d =>
        {
            var ids = FilesPage(d).Ids;
            Assert.Equal(2, ComboCount(d, ids.RawFullDecode));
            Assert.Equal(index == 0 ? Tr.EnumRawFullDecodeNever : Tr.EnumRawFullDecodeOnZoom, ComboText(d, ids.RawFullDecode, index));
            Choose(d, ids.RawFullDecode, index);
        });
        Assert.Equal(expected, saved.RawFullDecode);
    }

    [Theory]
    [InlineData(0, RawPairMode.Separate)]
    [InlineData(1, RawPairMode.PreferJpeg)]
    [InlineData(2, RawPairMode.PreferRaw)]
    public void Files_RawPairMode_ComboOrder_MatchesTheEnum_AndTheLoadedIndex(int index, RawPairMode expected)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var saved = Saved(new AppSettings { RawPairMode = RawPairMode.Separate }, d =>
        {
            var ids = FilesPage(d).Ids;
            Assert.Equal(3, ComboCount(d, ids.RawPairMode));
            string[] texts = [Tr.EnumRawPairModeSeparate, Tr.EnumRawPairModePreferJpeg, Tr.EnumRawPairModePreferRaw];
            var expectedText = texts[index];
            Assert.Equal(expectedText, ComboText(d, ids.RawPairMode, index));
            Choose(d, ids.RawPairMode, index);
        });
        Assert.Equal(expected, saved.RawPairMode);

        Show(new AppSettings { RawPairMode = expected }, (d, _) =>
        {
            GoTo(d, FilesIndex);
            Assert.Equal(index, ComboIndex(d, FilesPage(d).Ids.RawPairMode));
            Assert.Equal(index, d.CollectAll().RawPairModeIndex);
            d.Close();
        });
    }

    [Fact]
    public void Files_TheCollectedForm_CarriesOnlyThisPagesProperties_TheRestKeepsTheLoadedValues()
    {
        var settings = new AppSettings { ShowZoomIndicator = false, LoggingEnabled = true, ClickZoomPercent = 350, ImageSortMode = ImageSortMode.SizeAscending };
        var saved = Saved(settings, d => Tick(d, FilesPage(d).Ids.ConfirmBeforeDelete, true));
        Assert.True(saved.ConfirmBeforeDelete);
        Assert.False(saved.ShowZoomIndicator);
        Assert.True(saved.LoggingEnabled);
        Assert.Equal(350, saved.ClickZoomPercent);
        Assert.Equal(ImageSortMode.SizeAscending, saved.ImageSortMode);
    }

    // ---- xoá vĩnh viễn (an toàn dữ liệu) --------------------------------------------------------------------------------------------

    [Fact]
    public void PermanentDelete_IsOptIn_KeepsTheWarningWording_AndDefaultsAndResetTurnItOff()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true, ConfirmBeforeDelete = true }, (d, _) =>
        {
            GoTo(d, FilesIndex);
            var ids = FilesPage(d).Ids;
            Assert.True(Checked(d, ids.AllowPermanentDelete));
            // The wording of the WPF page (label + the "cannot be restored, not even with Undo" warning) is shown unchanged.
            Assert.Equal(Tr.SettingsAllowPermanentDeleteLabel, Txt(d, ids.AllowPermanentDelete));
            var texts = d.PageItems(FilesIndex).Select(i => i.Text).ToList();
            Assert.Contains(Tr.SettingsAllowPermanentDeleteHint, texts);
            Assert.Contains("cannot be restored", Tr.SettingsAllowPermanentDeleteHint, StringComparison.Ordinal);
            Assert.Contains(Tr.SettingsConfirmBeforeDeleteHint, texts);

            // Reset page = built-in defaults of this page only (working copy unchanged until Save).
            d.ResetActivePage();
            Assert.False(Checked(d, ids.AllowPermanentDelete));
            Assert.False(Checked(d, ids.ConfirmBeforeDelete));
            Assert.True(d.Settings.AllowPermanentDeleteWithoutRecycleBin, "Reset page does not write the working copy");
            Tick(d, ids.AllowPermanentDelete, true);
            d.Close();
        });

        var afterDefaults = Saved(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true }, d =>
        {
            Command(d, SettingsDialog.DefaultsId);
            Assert.False(Checked(d, FilesPage(d).Ids.AllowPermanentDelete));
        });
        Assert.False(afterDefaults.AllowPermanentDeleteWithoutRecycleBin);
    }

    [Fact]
    public void PermanentDelete_NeverTurnsOnByItself_WhenAnotherPropertyIsEdited()
    {
        var saved = Saved(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false }, d =>
        {
            var ids = FilesPage(d).Ids;
            Tick(d, ids.ConfirmBeforeDelete, true);
            Tick(d, ids.CompareHash, false);
            Click(d, FilesPage(d).JournalRadios[1]);
        });
        Assert.False(saved.AllowPermanentDeleteWithoutRecycleBin);
        Assert.True(saved.ConfirmBeforeDelete);
        Assert.Equal(JournalDurability.PowerLossSafe, saved.JournalDurability);
    }

    [Fact]
    public void ResetPage_OnFiles_RestoresEveryProperty_OfThisPageAndNoOtherPage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = NonDefault();
        settings.ShowZoomIndicator = false;
        Show(settings, (d, _) =>
        {
            GoTo(d, FilesIndex);
            d.ResetActivePage();
            var ids = FilesPage(d).Ids;
            var defaults = new AppSettings();
            Assert.Equal(defaults.CompareHashEnabled, Checked(d, ids.CompareHash));
            Assert.Equal(defaults.CompareSizeEnabled, Checked(d, ids.CompareSize));
            Assert.Equal(defaults.RawSupportEnabled, Checked(d, ids.RawSupport));
            Assert.Equal((int)(defaults.RawFullDecode == RawFullDecode.OnZoom ? 1 : 0), ComboIndex(d, ids.RawFullDecode));
            Assert.Equal(defaults.WebpHeicSupportEnabled, Checked(d, ids.WebpHeic));
            Assert.Equal(string.Empty, Txt(d, ids.EditorPath));
            Assert.True(Checked(d, FilesPage(d).JournalRadios[0]));
            Assert.False(Checked(d, FilesPage(d).JournalRadios[1]));
            Assert.False(Checked(d, ids.ReuseLastFolder));
            // Another page keeps what the working copy had.
            Assert.False(d.CollectAll().ShowZoomIndicator);
            d.Close();
        });
    }

    // ---- đường dẫn trình soạn thảo ---------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> EditorPaths => new()
    {
        { string.Empty, string.Empty },
        { "   ", string.Empty },
        { @"  C:\Tools\editor.exe  ", @"C:\Tools\editor.exe" },
        { @"C:\Program Files\Photo Editor\editor.exe", @"C:\Program Files\Photo Editor\editor.exe" },
        { @"D:\Ứng dụng\Trình soạn thảo ảnh\Sửa ảnh.exe", @"D:\Ứng dụng\Trình soạn thảo ảnh\Sửa ảnh.exe" },
        { @"\\máy-chủ\chia sẻ\Công cụ\ps.exe", @"\\máy-chủ\chia sẻ\Công cụ\ps.exe" },
    };

    [Theory]
    [MemberData(nameof(EditorPaths))]
    public void ExternalEditorPath_RoundTrips_TrimmedAtTheEdges_AndKeepsSpacesAndVietnamese(string typed, string expected)
    {
        var saved = Saved(new AppSettings(), d => SetText(d, FilesPage(d).Ids.EditorPath, typed));
        Assert.Equal(expected, saved.ExternalEditorPath);

        // Re-open with the saved value: the field shows it unchanged.
        Show(saved, (d, _) =>
        {
            GoTo(d, FilesIndex);
            Assert.Equal(expected, Txt(d, FilesPage(d).Ids.EditorPath));
            d.Close();
        });
    }

    [Fact]
    public void ExternalEditorPath_AnInvalidOrVeryLongPath_IsStoredAsTyped_WithoutAValidationRefusal()
    {
        // As the WPF page: the path is not validated at Save (a missing editor is reported when the menu item runs).
        var invalid = @"C:\nope|?*<>\x.exe";
        Assert.Equal(invalid, Saved(new AppSettings(), d => SetText(d, FilesPage(d).Ids.EditorPath, invalid)).ExternalEditorPath);
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 40), 8)) + @"\e.exe";
        Assert.Equal(longPath, Saved(new AppSettings(), d => SetText(d, FilesPage(d).Ids.EditorPath, longPath)).ExternalEditorPath);
    }

    // ---- Browse -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Browse_ShowsTheExecutableFilter_AndAChosenFileFillsTheField_ButOnlySaveWritesIt()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var files = new RecordingFiles { OpenResult = @"D:\Ảnh\Trình sửa\Sửa ảnh.exe" };
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var ids = FilesPage(d).Ids;
            Command(d, ids.Browse);
            var call = Assert.Single(files.Opens);
            Assert.Equal(Tr.SettingsExternalEditorPathLabel, call.Title);
            Assert.Equal(FileFilterText.Parse(Tr.DialogFileFilterExecutable), call.Filters);
            Assert.Contains(call.Filters, f => f.Pattern.Contains("*.exe", StringComparison.Ordinal));
            Assert.Null(call.InitialFolder);
            Assert.Equal(@"D:\Ảnh\Trình sửa\Sửa ảnh.exe", Txt(d, ids.EditorPath));
            Assert.Equal(string.Empty, d.Settings.ExternalEditorPath); // not saved yet
            Assert.True(d.TrySaveAll());
            Assert.Equal(@"D:\Ảnh\Trình sửa\Sửa ảnh.exe", d.Settings.ExternalEditorPath);
            d.Close();
        }, Options(new FakeWarnings(), null) with { Files = files });
    }

    [Fact]
    public void Browse_Cancelled_LeavesTheFieldExactlyAsTyped()
    {
        var files = new RecordingFiles { OpenResult = null };
        Show(new AppSettings { ExternalEditorPath = @"C:\Tools\old.exe" }, (d, _) =>
        {
            GoTo(d, FilesIndex);
            var ids = FilesPage(d).Ids;
            SetText(d, ids.EditorPath, @"C:\Tools\typed here.exe");
            Command(d, ids.Browse);
            Assert.Single(files.Opens);
            Assert.Equal(@"C:\Tools\typed here.exe", Txt(d, ids.EditorPath));
            files.OpenResult = string.Empty; // an empty pick is also "nothing chosen"
            Command(d, ids.Browse);
            Assert.Equal(@"C:\Tools\typed here.exe", Txt(d, ids.EditorPath));
            d.Close();
        }, Options(new FakeWarnings(), null) with { Files = files });
    }

    [Fact]
    public void Browse_StartsInTheFolderOfAnExistingEditor_AndInTheDefaultFolderOtherwise()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PRFiles Thư mục " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "Sửa ảnh.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        try
        {
            var files = new RecordingFiles();
            Show(new AppSettings { ExternalEditorPath = exe }, (d, _) =>
            {
                GoTo(d, FilesIndex);
                var ids = FilesPage(d).Ids;
                Command(d, ids.Browse);
                Assert.Equal(folder, files.Opens[0].InitialFolder);
                SetText(d, ids.EditorPath, Path.Combine(folder, "không có.exe"));
                Command(d, ids.Browse);
                Assert.Null(files.Opens[1].InitialFolder);
                SetText(d, ids.EditorPath, "bad|path?.exe");
                Command(d, ids.Browse);
                Assert.Null(files.Opens[2].InitialFolder);
                SetText(d, ids.EditorPath, "   ");
                Command(d, ids.Browse);
                Assert.Null(files.Opens[3].InitialFolder);
                d.Close();
            }, Options(new FakeWarnings(), null) with { Files = files });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Browse_AFailingFileDialog_WarnsAndKeepsTheField_AndWithoutAFileDialogNothingHappens()
    {
        var warnings = new FakeWarnings();
        var files = new RecordingFiles { Throw = new InvalidOperationException("picker exploded") };
        Show(new AppSettings { ExternalEditorPath = @"C:\a.exe" }, (d, _) =>
        {
            GoTo(d, FilesIndex);
            var ids = FilesPage(d).Ids;
            Command(d, ids.Browse);
            Assert.Equal(@"C:\a.exe", Txt(d, ids.EditorPath));
            var shown = Assert.Single(warnings.Shown);
            Assert.Equal(Tr.DialogSettingsOpenFailedTitle, shown.Title);
            Assert.Contains("picker exploded", shown.Message, StringComparison.Ordinal);
            d.Close();
        }, Options(warnings, null) with { Files = files });

        Show(new AppSettings { ExternalEditorPath = @"C:\a.exe" }, (d, _) =>
        {
            GoTo(d, FilesIndex);
            Command(d, FilesPage(d).Ids.Browse);
            Assert.Equal(@"C:\a.exe", Txt(d, FilesPage(d).Ids.EditorPath));
            d.Close();
        }, Options(new FakeWarnings(), null));
    }

    // ---- hook FocusExternalEditor -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExternalEditorTarget_FocusesTheEditorPathField_OnTheFilesPage_WithoutChangingTheRememberedPage()
    {
        nint focused = 0;
        nint editor = 0;
        string? page = null;
        Show(new AppSettings(), (d, _) =>
        {
            // The framework focuses the navigation list first; the hook posts the editor focus AFTER it, so queue the check behind it.
            d.PostToUi(() =>
            {
                focused = User32.GetFocus();
                editor = H(d, FilesPage(d).Ids.EditorPath);
                page = d.ActivePageKey;
                d.Close();
            });
        }, Options(new FakeWarnings(), new FakeFiles(), target: SettingsTarget.ExternalEditor), realizeAll: false);

        Assert.Equal("Files", page);
        Assert.NotEqual(0, editor);
        Assert.Equal(editor, focused);
        Assert.Equal("General", SettingsPresenter.RememberedPageKey);
    }

    [Fact]
    public void FocusExternalEditor_FromAnotherPage_BringsTheFilesPageBackWithTheFieldFocused()
    {
        Show(new AppSettings(), (d, _) =>
        {
            Assert.Equal("General", d.ActivePageKey);
            Assert.True(d.FocusExternalEditor());
            Assert.Equal("Files", d.ActivePageKey);
            Assert.Equal(H(d, FilesPage(d).Ids.EditorPath), User32.GetFocus());
            Assert.Equal("General", SettingsPresenter.RememberedPageKey); // a one-off jump is not "the page the user last used"
            d.Close();
        });
    }

    [Fact]
    public void FocusExternalEditor_InTheFramework_ReturnsFalse_ForPagesThatDoNotHandleIt()
    {
        var page = new DiagnosticsSettingsPage();
        Assert.False(((ISettingsPageView)page).FocusExternalEditor());
        Assert.False(new GeneralSettingsPage().FocusExternalEditor());
        Assert.False(new PendingSettingsPage("Mouse").FocusExternalEditor());
    }

    // ---- radio -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void JournalRadios_ClickingOneUnchecksTheOther_AndTheChoiceIsSaved()
    {
        var saved = Saved(new AppSettings(), d =>
        {
            var radios = FilesPage(d).JournalRadios;
            Assert.True(Checked(d, radios[0]));
            Click(d, radios[1]);
            Assert.False(Checked(d, radios[0]));
            Assert.True(Checked(d, radios[1]));
            Assert.True(d.CollectAll().JournalSafe);
            Click(d, radios[0]);
            Assert.True(Checked(d, radios[0]));
            Assert.False(Checked(d, radios[1]));
            Assert.False(d.CollectAll().JournalSafe);
            Click(d, radios[1]);
        });
        Assert.Equal(JournalDurability.PowerLossSafe, saved.JournalDurability);
    }

    [Fact]
    public void JournalRadios_CarryTheGroupStyles_AndTheControlAfterTheGroupStartsANewOne()
    {
        Show(new AppSettings(), (d, _) =>
        {
            var radios = FilesPage(d).JournalRadios;
            var items = d.PageItems(FilesIndex).ToDictionary(i => i.Id);
            Assert.Equal(2, radios.Count);
            Assert.All(radios, id => Assert.Equal(9u, items[id].Style & 0xF)); // BS_AUTORADIOBUTTON
            Assert.NotEqual(0u, items[radios[0]].Style & DialogConstants.WsGroup);
            Assert.NotEqual(0u, items[radios[0]].Style & DialogConstants.WsTabStop);
            Assert.Equal(0u, items[radios[1]].Style & DialogConstants.WsGroup);
            // The element declared right after the radios (their first hint) starts a new group, so the arrows cannot leave the pair.
            var all = d.PageItems(FilesIndex);
            var after = all[all.ToList().FindIndex(i => i.Id == radios[1]) + 1];
            Assert.NotEqual(0u, after.Style & DialogConstants.WsGroup);
            d.Close();
        });
    }

    [Fact]
    public void JournalRadios_Tab_VisitsOnlyTheCheckedRadio_AndTheArrowKeysMoveTheChoice()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var page = FilesPage(d);
            var radios = page.JournalRadios;
            var browse = H(d, page.Ids.Browse);
            User32.SetFocus(browse);
            Assert.True(Key(d, browse, VkTab));
            Drain(d);
            Assert.Equal(H(d, radios[0]), User32.GetFocus()); // Fast is checked: the pair is ONE tab stop
            Assert.True(Key(d, User32.GetFocus(), VkTab));
            Drain(d);
            Assert.Equal(H(d, page.Ids.ReuseLastFolder), User32.GetFocus());

            // Down arrow: the dialog manager moves to the second radio and selects it.
            User32.SetFocus(H(d, radios[0]));
            Assert.True(Key(d, H(d, radios[0]), VkDown));
            Drain(d);
            Assert.Equal(H(d, radios[1]), User32.GetFocus());
            Assert.True(Checked(d, radios[1]));
            Assert.False(Checked(d, radios[0]));
            Assert.True(d.CollectAll().JournalSafe);

            // Now the SAFE radio is the tab stop (Tab from Browse lands on it) and Up returns to Fast.
            User32.SetFocus(browse);
            Assert.True(Key(d, browse, VkTab));
            Drain(d);
            Assert.Equal(H(d, radios[1]), User32.GetFocus());
            Assert.True(Key(d, H(d, radios[1]), VkUp));
            Drain(d);
            Assert.True(Checked(d, radios[0]));
            Assert.False(d.CollectAll().JournalSafe);
            d.Close();
        });
    }

    [Fact]
    public void JournalRadios_ShowTheirOwnHints_InBothLanguages()
    {
        using (TestLocalization.Use(TestLocalization.English))
        {
            Show(new AppSettings(), (d, _) =>
            {
                var radios = FilesPage(d).JournalRadios;
                Assert.Equal(Tr.SettingsJournalDurabilityFast, Txt(d, radios[0]));
                Assert.Equal(Tr.SettingsJournalDurabilitySafe, Txt(d, radios[1]));
                var texts = d.PageItems(FilesIndex).Select(i => i.Text).ToList();
                Assert.Contains(Tr.SettingsJournalDurabilityFastHint, texts);
                Assert.Contains(Tr.SettingsJournalDurabilitySafeHint, texts);
                d.Close();
            });
        }

        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            Show(new AppSettings(), (d, _) =>
            {
                var radios = FilesPage(d).JournalRadios;
                Assert.Equal(TestLocalization.Vietnamese.Get("settings.journalDurability.fast"), Txt(d, radios[0]));
                Assert.NotEqual("Fast (default)", Txt(d, radios[0]));
                d.Close();
            });
        }
    }

    // ---- ngôn ngữ / trạng thái codec / bố cục / handle ---------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheLanguage_RetranslatesThePage_IncludingTheCodecStatusLine_AndKeepsTheValues()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var codecs = new WicCodecSupport(false, true, false, "t");
        var pages = SettingsPageCatalog.CreatePages().ToArray();
        pages[FilesIndex] = new FilesSettingsPage(() => codecs);
        Show(NonDefault(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var ids = FilesPage(d).Ids;
            var english = (Status: Txt(d, ids.CodecStatus), Raw: Txt(d, ids.RawSupport), Browse: Txt(d, ids.Browse));
            Assert.Equal(SettingsPresenter.FormatWebpHeicCodecStatus(codecs), english.Status);
            Assert.Contains("HEVC Video Extensions", english.Status, StringComparison.Ordinal);

            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            Assert.Equal(SettingsPresenter.FormatWebpHeicCodecStatus(codecs), Txt(d, ids.CodecStatus));
            Assert.NotEqual(english.Status, Txt(d, ids.CodecStatus));
            Assert.NotEqual(english.Raw, Txt(d, ids.RawSupport));
            Assert.Equal(Tr.SettingsExternalEditorPathBrowse, Txt(d, ids.Browse));
            Assert.Equal(Tr.EnumRawPairModePreferRaw, ComboText(d, ids.RawPairMode, 2));
            // Values survive the switch.
            Assert.Equal(@"C:\Tools\Edit Photo\editor.exe", Txt(d, ids.EditorPath));
            Assert.True(Checked(d, FilesPage(d).JournalRadios[1]));
            Assert.Equal(2, ComboIndex(d, ids.RawPairMode));
            Assert.Equal(string.Empty, string.Join(';', Problems(d)));
            Localizer.SetCurrent(TestLocalization.English);
            Drain(d);
            Assert.Equal(english.Status, Txt(d, ids.CodecStatus));
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), pages: pages));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void CodecStatus_ShowsWhatThisPcHas(bool webp, bool heifContainer, bool hevc)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var codecs = new WicCodecSupport(webp, heifContainer, hevc, "t");
        var pages = SettingsPageCatalog.CreatePages().ToArray();
        pages[FilesIndex] = new FilesSettingsPage(() => codecs);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            var status = Txt(d, FilesPage(d).Ids.CodecStatus);
            Assert.Equal(SettingsPresenter.FormatWebpHeicCodecStatus(codecs), status);
            Assert.Equal(webp, status.Contains("WebP available", StringComparison.Ordinal));
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), pages: pages));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void FilesPage_DoesNotOverlapOrCutAnything_InBothLanguages_AndAtOneAndAHalfScale(string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var problems = new List<string>();
        Show(NonDefault(), (d, _) =>
        {
            GoTo(d, FilesIndex);
            problems.AddRange(Problems(d).Select(p => $"{language} 96 dpi: {p}"));
            ChangeDpi(d, 144);
            GoTo(d, FilesIndex);
            problems.AddRange(Problems(d).Select(p => $"{language} 144 dpi: {p}"));
            d.Close();
        });
        Assert.Empty(problems);
    }

    [Fact]
    public void OpeningTheFilesPage_RepeatedlyDoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            void One()
            {
                var dialog = new SettingsDialog(NonDefault(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
                dialog.Presenter.ShellIntegration = new FakeShell();
                ShowOnOwner(dialog, (d, _) =>
                {
                    GoTo((SettingsDialog)d, FilesIndex);
                    Click((SettingsDialog)d, FilesPage((SettingsDialog)d).JournalRadios[0]);
                    d.Close();
                });
            }

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

    private static IEnumerable<string> Problems(SettingsDialog dialog)
    {
        var width = Width(ClientRect(H(dialog, SettingsDialog.ViewportId(dialog.ActivePageIndex))));
        var rects = new List<(int Id, Rect Rect)>();
        foreach (var item in dialog.PageItems(dialog.ActivePageIndex))
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
                yield return $"control {item.Id} leaves the page horizontally ({rect.Left}..{rect.Right} in {width})";
            }

            var kind = item.Style & 0xF;
            var isText = item.Class == DialogControlClass.Static || (item.Class == DialogControlClass.Button && kind is 2 or 3 or 9);
            if (isText && Height(rect) < NeededTextHeight(control) - 1)
            {
                yield return $"control {item.Id} ('{item.Text}') is shorter than its text";
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
                    yield return $"controls {rects[i].Id} and {rects[j].Id} overlap";
                }
            }
        }
    }

    // ---- SettingsPageBuilder.Radio ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Builder_Radio_GivesTheFirstRadioGroupAndTabStop_TheOthersNeither_AndCutsTheGroupAfterIt()
    {
        var builder = new SettingsPageBuilder(5000);
        var ids = builder.Radio([() => "A", () => "B", () => "C"]);
        var after = builder.Check(() => "after");
        var afterAfter = builder.Check(() => "after2");

        Assert.Equal(3, ids.Count);
        Assert.Equal<int>([5001, 5002, 5003], ids);
        var byId = builder.Items.ToDictionary(i => i.Id);
        Assert.NotEqual(0u, byId[ids[0]].Style & DialogConstants.WsGroup);
        Assert.NotEqual(0u, byId[ids[0]].Style & DialogConstants.WsTabStop);
        foreach (var id in ids.Skip(1))
        {
            Assert.Equal(0u, byId[id].Style & DialogConstants.WsGroup);
            Assert.Equal(0u, byId[id].Style & DialogConstants.WsTabStop);
        }

        Assert.All(ids, id => Assert.Equal(9u, byId[id].Style & 0xF));
        Assert.NotEqual(0u, byId[after].Style & DialogConstants.WsGroup); // the next control opens a new group
        Assert.Equal(0u, byId[afterAfter].Style & DialogConstants.WsGroup); // and only that one
    }

    [Fact]
    public void Builder_Radio_CreatesTheHintsAfterEveryRadio_AsDimmedNotes_AndTheFirstHintOpensTheNextGroup()
    {
        var builder = new SettingsPageBuilder(7000);
        var ids = builder.Radio([() => "A", () => "B"], [() => "hint A", null]);
        var items = builder.Items.ToList();
        var hint = items.Single(i => i.Text == "hint A");

        Assert.True(items.IndexOf(hint) > items.FindIndex(i => i.Id == ids[1]), "hints come after all radios in z-order");
        Assert.Contains(hint.Id, builder.Notes);
        Assert.NotEqual(0u, hint.Style & DialogConstants.WsGroup);
        Assert.Equal(3, items.Count); // two radios and the single hint (the null hint adds no control)
        Assert.Equal(2, builder.Elements.Count);
    }

    [Fact]
    public void Builder_Radio_RejectsFewerThanTwoOptions_AndAHintListOfTheWrongLength()
    {
        var builder = new SettingsPageBuilder(9000);
        Assert.Throws<ArgumentException>(() => builder.Radio([() => "only"]));
        Assert.Throws<ArgumentException>(() => builder.Radio([() => "A", () => "B"], [() => "just one"]));
        Assert.Throws<ArgumentNullException>(() => builder.Radio(null!));
    }

    // ---- bản gốc nhóm trong một hộp thoại thật: hai nhóm radio độc lập -----------------------------------------------------------------

    private static string States(SettingsDialog d, IReadOnlyList<int> ids) => string.Concat(ids.Select(id => Checked(d, id) ? '1' : '0'));

    private sealed class TwoGroupsPage : SettingsPageBase
    {
        public IReadOnlyList<int> First { get; private set; } = [];

        public IReadOnlyList<int> Second { get; private set; } = [];

        public int Tick { get; private set; }

        public override string Key => "Diagnostics";

        public override void Declare(SettingsPageBuilder page)
        {
            page.Group(() => "G1", g => First = g.Radio([() => "one", () => "two", () => "three"]));
            Tick = page.Check(() => "tick");
            page.Group(() => "G2", g => Second = g.Radio([() => "uno", () => "dos"]));
        }

        public override void Load(SettingsFormState state)
        {
            Context.SetRadioIndex(First, 0);
            Context.SetRadioIndex(Second, 1);
        }

        public override void Collect(SettingsFormState state)
        {
        }
    }

    [Fact]
    public void TwoRadioGroups_OnOnePage_AreIndependent_AndSetRadioIndexCheckExactlyOne()
    {
        var page = new TwoGroupsPage();
        var pages = SettingsPageCatalog.CreatePages().ToArray();
        pages[6] = page;
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, 6);
            Assert.Equal("100", States(d, page.First));
            Assert.Equal("01", States(d, page.Second));

            Click(d, page.First[2]);
            Assert.Equal("001", States(d, page.First));
            Assert.Equal("01", States(d, page.Second)); // the other group is untouched

            Click(d, page.Second[0]);
            Assert.Equal("10", States(d, page.Second));
            Assert.Equal("001", States(d, page.First));
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), pages: pages));
    }
}
