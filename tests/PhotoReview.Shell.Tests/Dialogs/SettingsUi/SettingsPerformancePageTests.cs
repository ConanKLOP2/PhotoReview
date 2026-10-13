using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3b: trang Performance (4 thuộc tính: DecoderBackend, ImageCacheRamPercent, PreloadForwardCount, PreloadBackwardCount).
/// RAM máy được cố định bằng <see cref="SettingsDialogOptions.PhysicalMemoryBytes"/> nên khoảng thanh trượt tất định.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsPerformancePageTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private const int PageIndex = 3;

    private readonly DataRootFixture _data = new();

    public SettingsPerformancePageTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    private sealed class FakeFactory(bool turbo) : IImageDecoderFactory
    {
        public IImageDecoder Create(DecoderBackend backend) => throw new NotSupportedException();

        public bool IsRegistered(DecoderBackend backend) => backend != DecoderBackend.TurboJpeg || turbo;
    }

    private static PerformanceSettingsPage Perf(SettingsDialog d) => (PerformanceSettingsPage)d.Pages[PageIndex];

    private static SettingsDialogOptions Opts(FakeWarnings? warnings = null, long ram = 16 * Gib)
        => Options(warnings ?? new FakeWarnings(), new FakeFiles()) with { PhysicalMemoryBytes = ram };

    private static void Open(AppSettings settings, Action<SettingsDialog> ready, SettingsDialogOptions? options = null, bool? turbo = null,
        SettingsStore? store = null)
    {
        OnSta(() =>
        {
            var factory = turbo is { } available ? new FakeFactory(available) : null;
            var dialog = new SettingsDialog(settings, store, factory, null, null, options ?? Opts());
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                var settingsDialog = (SettingsDialog)d;
                settingsDialog.EnsureAllPagesCreated();
                GoTo(settingsDialog, PageIndex);
                ready(settingsDialog);
            });
            return 0;
        });
    }

    private static AppSettings Saved(AppSettings settings, Action<SettingsDialog>? edit = null)
    {
        AppSettings? result = null;
        Open(settings, d =>
        {
            edit?.Invoke(d);
            Assert.True(d.TrySaveAll(), "the values must be accepted");
            result = d.Settings;
            d.Close();
        });
        return result!;
    }

    private static string S(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int SliderPos(SettingsDialog d, int id) => (int)User32.SendMessage(H(d, id), ControlConstants.TbmGetPos, 0, 0);

    private static int SliderMin(SettingsDialog d, int id) => (int)User32.SendMessage(H(d, id), ControlConstants.TbmGetRangeMin, 0, 0);

    private static int SliderMax(SettingsDialog d, int id) => (int)User32.SendMessage(H(d, id), ControlConstants.TbmGetRangeMax, 0, 0);

    private static void Drag(SettingsDialog d, int id, int value)
    {
        User32.SendMessage(H(d, id), ControlConstants.TbmSetPos, 1, value);
        User32.SendMessage(d.Hwnd, ControlConstants.WmHScroll, ControlConstants.TbEndTrack, H(d, id));
    }

    // ---- guard ----------------------------------------------------------------------------------------------------------------------

    private static readonly string[] PerformanceProperties =
    [
        nameof(AppSettings.DecoderBackend), nameof(AppSettings.ImageCacheRamPercent),
        nameof(AppSettings.PreloadForwardCount), nameof(AppSettings.PreloadBackwardCount),
    ];

    [Fact]
    public void EveryPerformanceProperty_IsOwnedByTheFieldMap_AndTheInternalOnesStayHandEdited()
    {
        Assert.All(PerformanceProperties, property => Assert.Contains(property, SettingsFieldMap.CoveredProperties));
        Assert.Equal(PerformanceProperties.Length, PerformanceProperties.Distinct(StringComparer.Ordinal).Count());
        foreach (var internalOnly in new[]
                 {
                     nameof(AppSettings.ImageCacheCapacityBytes), nameof(AppSettings.MemoryReserveBytes), nameof(AppSettings.PreloadWorkerCount),
                     nameof(AppSettings.PreloadMemoryLoadLimit), nameof(AppSettings.PreviewDiskCacheCapacityBytes),
                     nameof(AppSettings.UseSourceBytesCache), nameof(AppSettings.SourceBytesCapacityBytes),
                 })
        {
            Assert.Contains(internalOnly, SettingsFieldMap.NotEditedInDialog.Keys);
            Assert.DoesNotContain(internalOnly, SettingsFieldMap.CoveredProperties);
        }
    }

    [Fact]
    public void ThePageIsInTheCatalog_AndIsTheFourthEntry()
    {
        Assert.Contains("Performance", SettingsPageCatalog.ImplementedKeys);
        Assert.Equal("Performance", SettingsPresenter.Pages[PageIndex].Key);
        Open(new AppSettings(), d =>
        {
            Assert.IsType<PerformanceSettingsPage>(d.Pages[PageIndex]);
            Assert.Equal("Performance", d.ActivePageKey);
            d.Close();
        });
    }

    // ---- DecoderBackend -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DecoderBackend.Wpf, 0)]
    [InlineData(DecoderBackend.WicDirect, 1)]
    [InlineData(DecoderBackend.TurboJpeg, 2)]
    public void DecoderBackend_RoundTrips_ThroughTheComboIndex(DecoderBackend backend, int index)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Open(new AppSettings { DecoderBackend = backend }, d =>
        {
            var ids = Perf(d).Ids;
            Assert.Equal(index, ComboIndex(d, ids.Decoder));
            Assert.Equal(index, d.CollectAll().DecoderBackendIndex);
            Assert.Equal(3, ComboCount(d, ids.Decoder));
            Assert.Equal(Tr.EnumDecoderBackendWpf, ComboText(d, ids.Decoder, 0));
            Assert.Equal(Tr.EnumDecoderBackendWicDirect, ComboText(d, ids.Decoder, 1));
            Assert.Equal(Tr.EnumDecoderBackendTurboJpeg, ComboText(d, ids.Decoder, 2));
            d.Close();
        });
        Assert.Equal(backend, Saved(new AppSettings { DecoderBackend = backend }).DecoderBackend);
    }

    [Theory]
    [InlineData(0, DecoderBackend.Wpf)]
    [InlineData(1, DecoderBackend.WicDirect)]
    [InlineData(2, DecoderBackend.TurboJpeg)]
    public void DecoderBackend_ChoosingAnItem_SavesTheBackendItsPositionNames(int index, DecoderBackend expected)
    {
        var start = expected == DecoderBackend.Wpf ? DecoderBackend.WicDirect : DecoderBackend.Wpf;
        var saved = Saved(new AppSettings { DecoderBackend = start }, d => Choose(d, Perf(d).Ids.Decoder, index));
        Assert.Equal(expected, saved.DecoderBackend);
    }

    [Fact]
    public void DecoderBackend_LibRawIsRepairedByTheNormalizer_BeforeTheDialogShowsIt()
    {
        var settings = new AppSettings { DecoderBackend = DecoderBackend.LibRaw };
        Assert.Contains(nameof(AppSettings.DecoderBackend), SettingsNormalizer.Normalize(settings));
        Assert.Equal(DecoderBackend.WicDirect, settings.DecoderBackend);
        Open(settings, d =>
        {
            Assert.Equal(1, ComboIndex(d, Perf(d).Ids.Decoder));
            d.Close();
        });
    }

    [Fact]
    public void TurboJpegMissing_ShowsTheNote_RefusesTheItem_AndKeepsTheSavedChoiceUntouched()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Open(new AppSettings { DecoderBackend = DecoderBackend.WicDirect }, d =>
        {
            var ids = Perf(d).Ids;
            Assert.True(Visible(d, ids.TurboNote));
            Assert.Equal(Tr.SettingsDecoderBackendTurboJpegUnavailable, Txt(d, ids.TurboNote));
            Choose(d, ids.Decoder, 2);
            Assert.Equal(1, ComboIndex(d, ids.Decoder));
            Choose(d, ids.Decoder, 0);
            Assert.Equal(0, ComboIndex(d, ids.Decoder));
            Choose(d, ids.Decoder, 2);
            Assert.Equal(0, ComboIndex(d, ids.Decoder));
            Assert.Equal(0, d.CollectAll().DecoderBackendIndex);
            d.Close();
        }, turbo: false);

        // A config that already holds TurboJpeg keeps showing it (as the WPF page does) so Save does not silently change it.
        Open(new AppSettings { DecoderBackend = DecoderBackend.TurboJpeg }, d =>
        {
            Assert.Equal(2, ComboIndex(d, Perf(d).Ids.Decoder));
            Assert.Equal(2, d.CollectAll().DecoderBackendIndex);
            d.Close();
        }, turbo: false);
    }

    [Fact]
    public void TurboJpegAvailable_HidesTheNote_AndAcceptsTheItem()
    {
        foreach (var available in new bool?[] { true, null })
        {
            Open(new AppSettings { DecoderBackend = DecoderBackend.Wpf }, d =>
            {
                var ids = Perf(d).Ids;
                Assert.False(Visible(d, ids.TurboNote));
                Choose(d, ids.Decoder, 2);
                Assert.Equal(2, ComboIndex(d, ids.Decoder));
                d.Close();
            }, turbo: available);
        }
    }

    [Fact]
    public void TurboJpegNote_FollowsTheLanguage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Open(new AppSettings(), d =>
        {
            var ids = Perf(d).Ids;
            var english = Txt(d, ids.TurboNote);
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            Assert.NotEqual(english, Txt(d, ids.TurboNote));
            Assert.Equal(Tr.SettingsDecoderBackendTurboJpegUnavailable, Txt(d, ids.TurboNote));
            Localizer.SetCurrent(TestLocalization.English);
            d.Close();
        }, turbo: false);
    }

    // ---- preload window ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Preload_LoadsTheSavedCounts_AndSavesWhatWasTyped()
    {
        Open(new AppSettings { PreloadForwardCount = 21, PreloadBackwardCount = 5 }, d =>
        {
            var ids = Perf(d).Ids;
            Assert.Equal("21", Txt(d, ids.Forward));
            Assert.Equal("5", Txt(d, ids.Backward));
            d.Close();
        });

        var saved = Saved(new AppSettings(), d =>
        {
            var ids = Perf(d).Ids;
            SetText(d, ids.Forward, "47");
            SetText(d, ids.Backward, "13");
        });
        Assert.Equal(47, saved.PreloadForwardCount);
        Assert.Equal(13, saved.PreloadBackwardCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(500, 500)]
    [InlineData(1, 500)]
    [InlineData(500, 0)]
    public void Preload_BoundaryValues_AreAcceptedAndSavedExactly(int forward, int backward)
    {
        Assert.Equal(PerformanceOptions.MinPreloadForwardCount, 1);
        Assert.Equal(PerformanceOptions.MinPreloadBackwardCount, 0);
        Assert.Equal(PerformanceOptions.MaxPreloadCount, 500);
        var saved = Saved(new AppSettings(), d =>
        {
            SetText(d, Perf(d).Ids.Forward, S(forward));
            SetText(d, Perf(d).Ids.Backward, S(backward));
        });
        Assert.Equal(forward, saved.PreloadForwardCount);
        Assert.Equal(backward, saved.PreloadBackwardCount);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("501", true)]
    [InlineData("-3", true)]
    [InlineData("abc", true)]
    [InlineData("", true)]
    [InlineData("4.5", true)]
    [InlineData("0", false)]
    public void Preload_OutOfRange_IsRefused_WithTheMessageAndTheFocusOnTheRightBox(string text, bool forwardBox)
    {
        // Backward may be 0, so "0" is only invalid for the forward box.
        var warnings = new FakeWarnings();
        var (accepted, focusIsBox) = (false, false);
        Open(new AppSettings(), d =>
        {
            var ids = Perf(d).Ids;
            SetText(d, forwardBox ? ids.Forward : ids.Backward, text);
            GoTo(d, 0);
            accepted = d.TrySaveAll();
            focusIsBox = User32.GetFocus() == H(d, forwardBox ? ids.Forward : ids.Backward);
            d.Close();
        }, Opts(warnings));

        if (!forwardBox && text == "0")
        {
            Assert.True(accepted);
            Assert.Empty(warnings.Shown);
            return;
        }

        Assert.False(accepted);
        Assert.True(focusIsBox, "the refused box must get the focus (page switched back to Performance)");
        var message = Assert.Single(warnings.Shown);
        Assert.Equal(Tr.DialogSettingsInvalidTitle, message.Title);
        Assert.Equal(
            forwardBox
                ? Tr.DialogSettingsInvalidPreloadForward(PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount)
                : Tr.DialogSettingsInvalidPreloadBackward(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount),
            message.Message);
    }

    [Fact]
    public void Preload_OutOfRangeInTheFile_IsNormalizedBeforeTheDialogShowsIt()
    {
        var settings = new AppSettings
        {
            PreloadForwardCount = 99999, PreloadBackwardCount = -4, ImageCacheRamPercent = 5000, DecoderBackend = (DecoderBackend)99,
        };
        var fixedNames = SettingsNormalizer.Normalize(settings);
        Assert.Contains(nameof(AppSettings.PreloadForwardCount), fixedNames);
        Assert.Contains(nameof(AppSettings.PreloadBackwardCount), fixedNames);
        Assert.Contains(nameof(AppSettings.ImageCacheRamPercent), fixedNames);
        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            Assert.Equal("500", Txt(d, ids.Forward));
            Assert.Equal("0", Txt(d, ids.Backward));
            Assert.Equal(PerformanceOptions.MaxImageCacheRamPercent, SliderPos(d, ids.RamSlider));
            Assert.Equal(1, ComboIndex(d, ids.Decoder));
            d.Close();
        });
    }

    // ---- RAM cache slider -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RamSlider_LoadsTheSavedPercent_TheRangeFollowsTheWindow_AndTheTextsMatchThePresenter()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = new AppSettings { ImageCacheRamPercent = 40, PreloadForwardCount = 10, PreloadBackwardCount = 2 };
        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            var (min, max) = d.Presenter.RamCacheRange("10", "2");
            Assert.Equal(min, SliderMin(d, ids.RamSlider));
            Assert.Equal(max, SliderMax(d, ids.RamSlider));
            Assert.Equal(PerformanceOptions.MaxImageCacheRamPercent, max);
            Assert.Equal(40, SliderPos(d, ids.RamSlider));
            Assert.Equal(d.Presenter.RamCacheValueText(40), Txt(d, ids.RamValue));
            Assert.Contains("40", Txt(d, ids.RamValue));
            Assert.Equal(d.Presenter.RamCacheHintText(min, max), Txt(d, ids.RamHint));
            Assert.Contains("16.0", Txt(d, ids.RamHint)); // 16 GiB of RAM shown in the hint
            Assert.Contains("6.4", Txt(d, ids.RamValue)); // 40 % of 16 GiB
            d.Close();
        });
    }

    [Fact]
    public void RamSlider_MovingIt_UpdatesTheValueText_AndTheSavedPercent()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Open(new AppSettings { ImageCacheRamPercent = 40 }, d =>
        {
            var ids = Perf(d).Ids;
            Drag(d, ids.RamSlider, 65);
            Assert.Equal(d.Presenter.RamCacheValueText(65), Txt(d, ids.RamValue));
            Assert.Equal(65, d.CollectAll().RamCachePercent);
            Assert.True(d.TrySaveAll());
            Assert.Equal(65, d.Settings.ImageCacheRamPercent);
            d.Close();
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    public void RamSlider_SavesTheBoundaryValues(int percent)
    {
        var settings = new AppSettings { PreloadForwardCount = 1, PreloadBackwardCount = 0, ImageCacheRamPercent = 50 };
        var saved = Saved(settings, d =>
        {
            var ids = Perf(d).Ids;
            var min = SliderMin(d, ids.RamSlider);
            Drag(d, ids.RamSlider, percent);
            Assert.Equal(Math.Max(min, percent), SliderPos(d, ids.RamSlider));
        });
        Assert.Equal(Math.Max(PerformanceOptions.MinImageCacheRamPercent, percent), saved.ImageCacheRamPercent);
    }

    [Fact]
    public void RamSlider_BiggerWindow_RaisesTheMinimum_AndPushesALowerValueUp()
    {
        // 4 GiB of RAM: a 500 + 500 window cannot fit below 90 %, a 1 + 0 window fits in a few percent.
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = new AppSettings { PreloadForwardCount = 1, PreloadBackwardCount = 0, ImageCacheRamPercent = 10 };
        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            var (smallMin, _) = d.Presenter.RamCacheRange("1", "0");
            var (bigMin, _) = d.Presenter.RamCacheRange("500", "500");
            Assert.True(bigMin > smallMin, "the fixture must make the window matter");
            Assert.Equal(smallMin, SliderMin(d, ids.RamSlider));
            Assert.Equal(10, SliderPos(d, ids.RamSlider));

            SetText(d, ids.Forward, "500");
            SetText(d, ids.Backward, "500");
            Assert.Equal(bigMin, SliderMin(d, ids.RamSlider));
            Assert.Equal(bigMin, SliderPos(d, ids.RamSlider));
            Assert.Equal(d.Presenter.RamCacheHintText(bigMin, PerformanceOptions.MaxImageCacheRamPercent), Txt(d, ids.RamHint));
            Assert.Equal(d.Presenter.RamCacheValueText(bigMin), Txt(d, ids.RamValue));

            // an unparsable box falls back to the SAVED count (1 / 0): the range drops again, the value stays
            SetText(d, ids.Forward, "xyz");
            SetText(d, ids.Backward, "");
            Assert.Equal(smallMin, SliderMin(d, ids.RamSlider));
            Assert.Equal(bigMin, SliderPos(d, ids.RamSlider));
            d.Close();
        }, Opts(ram: 4 * Gib));
    }

    [Fact]
    public void RamSlider_SavedPercentBelowTheWindowMinimum_IsShownAtTheMinimum()
    {
        var settings = new AppSettings { PreloadForwardCount = 500, PreloadBackwardCount = 500, ImageCacheRamPercent = 2 };
        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            var (min, _) = d.Presenter.RamCacheRange("500", "500");
            Assert.True(min > 2);
            Assert.Equal(min, SliderPos(d, ids.RamSlider));
            d.Close();
        }, Opts(ram: 4 * Gib));
    }

    [Fact]
    public void RamTexts_FollowTheLanguage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Open(new AppSettings { ImageCacheRamPercent = 30 }, d =>
        {
            var ids = Perf(d).Ids;
            var label = Txt(d, ids.RamValue);
            var hint = Txt(d, ids.RamHint);
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            Assert.NotEqual(label, Txt(d, ids.RamValue));
            Assert.NotEqual(hint, Txt(d, ids.RamHint));
            Assert.Equal(d.Presenter.RamCacheValueText(30), Txt(d, ids.RamValue));
            Assert.Equal(Tr.SettingsNavPerformance, Txt(d, SettingsDialog.PageTitleId));
            Localizer.SetCurrent(TestLocalization.English);
            d.Close();
        });
    }

    // ---- Save keeps everything else ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Save_OnlyTouchesThePageProperties_AndKeepsTheHandEditedInternals()
    {
        var settings = new AppSettings
        {
            PreloadWorkerCount = 3, ImageCacheCapacityBytes = 123_456_789, MemoryReserveBytes = 77_000_000, PreloadMemoryLoadLimit = 0.7,
            PreviewDiskCacheCapacityBytes = 55_000_000, UseSourceBytesCache = false, SourceBytesCapacityBytes = 33_000_000,
            RawSupportEnabled = false, WebpHeicSupportEnabled = false, CompareHashEnabled = false, LoggingEnabled = true,
        };
        var saved = Saved(settings, d => Choose(d, Perf(d).Ids.Decoder, 0));
        Assert.Equal(3, saved.PreloadWorkerCount);
        Assert.Equal(123_456_789, saved.ImageCacheCapacityBytes);
        Assert.Equal(77_000_000, saved.MemoryReserveBytes);
        Assert.Equal(0.7, saved.PreloadMemoryLoadLimit);
        Assert.Equal(55_000_000, saved.PreviewDiskCacheCapacityBytes);
        Assert.False(saved.UseSourceBytesCache);
        Assert.Equal(33_000_000, saved.SourceBytesCapacityBytes);
        Assert.False(saved.RawSupportEnabled);
        Assert.False(saved.WebpHeicSupportEnabled);
        Assert.False(saved.CompareHashEnabled);
        Assert.True(saved.LoggingEnabled);
        Assert.Equal(DecoderBackend.Wpf, saved.DecoderBackend);
    }

    [Fact]
    public void ResetPage_AndDefaults_RestoreThePerformanceDefaults()
    {
        var settings = new AppSettings
        {
            DecoderBackend = DecoderBackend.Wpf, ImageCacheRamPercent = 77, PreloadForwardCount = 60, PreloadBackwardCount = 30,
        };
        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            Assert.Equal("60", Txt(d, ids.Forward));
            Command(d, SettingsDialog.ResetPageId);
            var defaults = new AppSettings();
            Assert.Equal(S(PerformanceOptions.PreloadForwardCount), Txt(d, ids.Forward));
            Assert.Equal(S(PerformanceOptions.PreloadBackwardCount), Txt(d, ids.Backward));
            var (min, max) = d.Presenter.RamCacheRange(Txt(d, ids.Forward), Txt(d, ids.Backward));
            Assert.Equal(Math.Clamp(PerformanceOptions.ImageCacheRamPercent, min, max), SliderPos(d, ids.RamSlider));
            Assert.Equal(d.Presenter.RamCacheValueText(SliderPos(d, ids.RamSlider)), Txt(d, ids.RamValue));
            Assert.Equal(DecoderIndexOf(defaults.DecoderBackend), ComboIndex(d, ids.Decoder));
            // the working copy changes only on Save
            Assert.Equal(60, d.Settings.PreloadForwardCount);
            d.Close();
        });

        Open(settings, d =>
        {
            var ids = Perf(d).Ids;
            Command(d, SettingsDialog.DefaultsId);
            Assert.Equal(S(PerformanceOptions.PreloadForwardCount), Txt(d, ids.Forward));
            Assert.Equal(S(PerformanceOptions.PreloadBackwardCount), Txt(d, ids.Backward));
            d.Close();
        });
    }

    private static int DecoderIndexOf(DecoderBackend backend) => backend switch
    {
        DecoderBackend.WicDirect => 1,
        DecoderBackend.TurboJpeg => 2,
        _ => 0,
    };

    // ---- Tab / layout / handles -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tab_VisitsDecoder_Slider_Forward_Backward_InThatOrder()
    {
        Open(new AppSettings(), d =>
        {
            var ids = Perf(d).Ids;
            User32.SetFocus(H(d, SettingsDialog.NavId));
            var visited = new List<int>();
            for (var step = 0; step < 12; step++)
            {
                Key(d, User32.GetFocus(), VkTab);
                var id = User32.GetDlgCtrlID(User32.GetFocus());
                if (id == SettingsDialog.SaveId || id == SettingsDialog.ApplyId || id == SettingsDialog.CancelId || id == SettingsDialog.DefaultsId
                    || id == SettingsDialog.ResetPageId || id == SettingsDialog.ExportId || id == SettingsDialog.ImportId)
                {
                    break;
                }

                visited.Add(id);
            }

            Assert.Equal([ids.Decoder, ids.RamSlider, ids.Forward, ids.Backward], visited);
            d.Close();
        });
    }

    private static List<string> Problems(SettingsDialog d, string label)
    {
        var problems = new List<string>();
        var width = Width(ClientRect(H(d, SettingsDialog.ViewportId(d.ActivePageIndex))));
        var rects = new List<(int Id, Rect Rect)>();
        foreach (var item in d.PageItems(d.ActivePageIndex))
        {
            var control = H(d, item.Id);
            if (control == 0 || !User32.IsWindowVisible(control) || IsGroupBox(control))
            {
                continue;
            }

            var rect = RectInViewport(d, item.Id);
            rects.Add((item.Id, rect));
            if (rect.Left < 0 || rect.Right > width + 1)
            {
                problems.Add($"{label}: control {item.Id} leaves the page ({rect.Left}..{rect.Right} in {width})");
            }

            var isText = item.Class == DialogControlClass.Static;
            if (isText && Height(rect) < NeededTextHeight(control) - 1)
            {
                problems.Add($"{label}: control {item.Id} is shorter than its text");
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
                    problems.Add($"{label}: controls {rects[i].Id} and {rects[j].Id} overlap");
                }
            }
        }

        return problems;
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("vi", false)]
    [InlineData("en", true)]
    [InlineData("vi", true)]
    public void Layout_DoesNotOverlapOrCut_InBothLanguages_AtOneAndOneAndAHalfScale_WithTheTurboNoteShown(string language, bool turboMissing)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var problems = new List<string>();
        Open(new AppSettings { ImageCacheRamPercent = 45 }, d =>
        {
            problems.AddRange(Problems(d, $"{language} 96 dpi"));
            ChangeDpi(d, 144);
            GoTo(d, PageIndex);
            problems.AddRange(Problems(d, $"{language} 144 dpi"));
            d.Close();
        }, turbo: turboMissing ? false : null);
        Assert.Empty(problems);
    }

    [Fact]
    public void OpeningAndClosing_TheSliderPage_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            void One() => ShowOnOwner(NewDialog(), (d, _) =>
            {
                GoTo((SettingsDialog)d, PageIndex);
                Drag((SettingsDialog)d, Perf((SettingsDialog)d).Ids.RamSlider, 50);
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
        var dialog = new SettingsDialog(new AppSettings(), null, new FakeFactory(false), null, null, Opts());
        dialog.Presenter.ShellIntegration = new FakeShell();
        return dialog;
    }
}

/// <summary>Ảnh chụp thủ công của trang Performance: <c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;thư mục&gt;</c>; không có biến thì bỏ qua.</summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class SettingsPerformanceSnapshotTests
{
    [Fact]
    public void WriteSnapshots_OfThePerformancePage()
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
                foreach (var (suffix, dpi) in new[] { ("", 0u), ("-150", 144u) })
                {
                    var settings = new AppSettings { ImageCacheRamPercent = 45, PreloadForwardCount = 32, PreloadBackwardCount = 8 };
                    var options = Options(new FakeWarnings(), new FakeFiles()) with { PhysicalMemoryBytes = 16L * 1024 * 1024 * 1024 };
                    var dialog = new SettingsDialog(settings, null, null, null, null, options);
                    dialog.Presenter.ShellIntegration = new FakeShell();
                    ShowOnOwner(dialog, (d, _) =>
                    {
                        GoTo((SettingsDialog)d, 3);
                        if (dpi != 0)
                        {
                            ChangeDpi(d, dpi);
                        }

                        Drain(d);
                        NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, $"settings-performance-{code}{suffix}.png"), d.Hwnd);
                        d.Close();
                    });
                }

                return 0;
            });
        }
    }
}
