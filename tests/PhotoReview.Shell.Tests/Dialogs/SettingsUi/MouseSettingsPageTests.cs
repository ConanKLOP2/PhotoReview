using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
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
/// WP-35 GĐ3b: trang Mouse của Settings Win32 (20 thuộc tính: 17 giá trị + ImageTransition(+Ms) + Touchpad(+Distance) + menu chuột phải).
/// Mỗi thuộc tính có ca Load -&gt; Collect -&gt; Save (qua <c>SettingsFieldMap</c>/<c>SettingsPresenter</c> thật), thứ tự mục combobox theo
/// chỉ số, 19 ô menu (bật/tắt từng mục, thứ tự, mục khoá), kiểm hợp lệ theo trường, phụ thuộc, tóm tắt phím zoom, ngôn ngữ, Reset trang,
/// Tab, rò handle. Bố cục/DPI/cuộn của khung đã được <see cref="SettingsDialogTests"/> duyệt qua mọi trang của <c>SettingsPresenter.Pages</c>.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class MouseSettingsPageTests : IDisposable
{
    private static readonly int MouseIndex = SettingsPresenter.Pages.ToList().FindIndex(p => p.Key == "Mouse");

    private readonly DataRootFixture _data = new();

    public MouseSettingsPageTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    private static MouseSettingsPage Page(SettingsDialog d) => (MouseSettingsPage)d.Pages[MouseIndex];

    /// <summary>Mở trang Mouse, chạy <paramref name="edit"/>, Save (TrySaveAll) và trả bản làm việc đã ghi.</summary>
    private static AppSettings Saved(AppSettings settings, Action<SettingsDialog, MouseSettingsPage>? edit = null)
    {
        AppSettings? result = null;
        Show(settings, (d, _) =>
        {
            GoTo(d, MouseIndex);
            edit?.Invoke(d, Page(d));
            Assert.True(d.TrySaveAll(), "the values must be accepted");
            result = d.Settings;
            d.Close();
        });
        return result!;
    }

    private static readonly string[] MouseProperties =
    [
        nameof(AppSettings.ClickZoomPercent), nameof(AppSettings.ClickToZoomEnabled), nameof(AppSettings.ArrowPanStepPercent),
        nameof(AppSettings.KeyboardZoomAnchor), nameof(AppSettings.FitWidthAnchor), nameof(AppSettings.FitWidthAnchor2),
        nameof(AppSettings.KeepZoomAcrossImages), nameof(AppSettings.KeyboardZoomStepPercent), nameof(AppSettings.MiddleClickAction),
        nameof(AppSettings.ClickZoomKeyTogglesFit), nameof(AppSettings.MouseWheelAction), nameof(AppSettings.KineticPanEnabled),
        nameof(AppSettings.KineticGlideSmoothing), nameof(AppSettings.ArrowKeyNavigatesAtZoomEdge), nameof(AppSettings.ImageTransition),
        nameof(AppSettings.ImageTransitionMs), nameof(AppSettings.TouchpadSwipeEnabled), nameof(AppSettings.TouchpadSwipeDistancePerImage),
        nameof(AppSettings.HiddenContextMenuItems), nameof(AppSettings.ShowZoomMenuItems), nameof(AppSettings.ShowFolderMenuItems),
        nameof(AppSettings.ShowRecycleMenuItem),
    ];

    [Fact]
    public void EveryMousePageProperty_IsOwnedByTheFieldMap()
    {
        Assert.All(MouseProperties, property => Assert.Contains(property, SettingsFieldMap.CoveredProperties));
        Assert.Equal(MouseProperties.Length, MouseProperties.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Mouse", SettingsPresenter.Pages[MouseIndex].Key);
        Assert.IsType<MouseSettingsPage>(SettingsPageCatalog.CreatePages()[MouseIndex]);
        Assert.DoesNotContain("Mouse", SettingsPageCatalog.PendingKeys);
    }

    // ---- Load -> Collect -> Save, one property at a time ----------------------------------------------------------------------

    private static AppSettings NonDefault() => new()
    {
        ClickZoomPercent = 250,
        ClickToZoomEnabled = true,
        ArrowPanStepPercent = 35,
        KeyboardZoomAnchor = KeyboardZoomAnchor.Pointer,
        FitWidthAnchor = FitWidthAnchor.BottomThird,
        FitWidthAnchor2 = FitWidthAnchor.TopThird,
        KeepZoomAcrossImages = true,
        KeyboardZoomStepPercent = 15,
        MiddleClickAction = MiddleClickAction.OpenFolder,
        ClickZoomKeyTogglesFit = true,
        MouseWheelAction = MouseWheelAction.Navigate,
        KineticPanEnabled = true,
        KineticGlideSmoothing = KineticGlideSmoothing.Off,
        ArrowKeyNavigatesAtZoomEdge = true,
        ImageTransition = ImageTransition.Fade,
        ImageTransitionMs = 333,
        TouchpadSwipeEnabled = false,
        TouchpadSwipeDistancePerImage = 345,
        HiddenContextMenuItems = [nameof(ContextMenuItemId.Undo), nameof(ContextMenuItemId.CopyFullPath), nameof(ContextMenuItemId.ZoomPresets)],
    };

    [Fact]
    public void Mouse_LoadsEveryProperty_AndSaveWritesThemBackUnchanged()
    {
        Show(NonDefault(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            Assert.Equal("250", Txt(d, ids.ClickZoomPercent));
            Assert.True(Checked(d, ids.ClickToZoom));
            Assert.Equal("35", Txt(d, ids.ArrowPanStep));
            Assert.Equal(0, ComboIndex(d, ids.KeyboardZoomAnchor));
            Assert.Equal(2, ComboIndex(d, ids.FitWidthAnchor));
            Assert.Equal(1, ComboIndex(d, ids.FitWidthAnchor2));
            Assert.True(Checked(d, ids.KeepZoom));
            Assert.Equal("15", Txt(d, ids.KeyboardZoomStep));
            Assert.Equal(10, ComboIndex(d, ids.MiddleClick));
            Assert.True(Checked(d, ids.ClickZoomKeyTogglesFit));
            Assert.Equal(1, ComboIndex(d, ids.MouseWheel));
            Assert.True(Checked(d, ids.KineticPan));
            Assert.Equal(0, ComboIndex(d, ids.KineticSmoothing));
            Assert.True(Checked(d, ids.ArrowKeyAtEdge));
            Assert.Equal(1, ComboIndex(d, ids.ImageTransition));
            Assert.Equal("333", Txt(d, ids.ImageTransitionMs));
            Assert.False(Checked(d, ids.TouchpadSwipe));
            Assert.Equal("345", Txt(d, ids.TouchpadDistance));
            var hidden = new[] { ContextMenuItemId.Undo, ContextMenuItemId.CopyFullPath, ContextMenuItemId.ZoomPresets };
            foreach (var (item, id) in Page(d).MenuChecks)
            {
                Assert.Equal(!hidden.Contains(item), Checked(d, id));
            }

            Assert.True(d.TrySaveAll());
            var s = d.Settings;
            Assert.Equal(250, s.ClickZoomPercent);
            Assert.True(s.ClickToZoomEnabled);
            Assert.Equal(35, s.ArrowPanStepPercent);
            Assert.Equal(KeyboardZoomAnchor.Pointer, s.KeyboardZoomAnchor);
            Assert.Equal(FitWidthAnchor.BottomThird, s.FitWidthAnchor);
            Assert.Equal(FitWidthAnchor.TopThird, s.FitWidthAnchor2);
            Assert.True(s.KeepZoomAcrossImages);
            Assert.Equal(15, s.KeyboardZoomStepPercent);
            Assert.Equal(MiddleClickAction.OpenFolder, s.MiddleClickAction);
            Assert.True(s.ClickZoomKeyTogglesFit);
            Assert.Equal(MouseWheelAction.Navigate, s.MouseWheelAction);
            Assert.True(s.KineticPanEnabled);
            Assert.Equal(KineticGlideSmoothing.Off, s.KineticGlideSmoothing);
            Assert.True(s.ArrowKeyNavigatesAtZoomEdge);
            Assert.Equal(ImageTransition.Fade, s.ImageTransition);
            Assert.Equal(333, s.ImageTransitionMs);
            Assert.False(s.TouchpadSwipeEnabled);
            Assert.Equal(345, s.TouchpadSwipeDistancePerImage);
            Assert.Equal(hidden.ToHashSet(), ContextMenuItems.EffectiveHidden(s).ToHashSet());
            d.Close();
        });
    }

    [Fact]
    public void Mouse_EditingEveryControl_WritesItsOwnProperty_AndNothingElse()
    {
        // Start from the opposite of NonDefault() so every edit is a real change with a distinct value (catches crossed fields).
        var start = new AppSettings
        {
            ClickZoomPercent = 100,
            ClickToZoomEnabled = false,
            ArrowPanStepPercent = 10,
            KeyboardZoomAnchor = KeyboardZoomAnchor.ViewportCentre,
            FitWidthAnchor = FitWidthAnchor.Centre,
            FitWidthAnchor2 = FitWidthAnchor.BottomThird,
            KeepZoomAcrossImages = false,
            KeyboardZoomStepPercent = 10,
            MiddleClickAction = MiddleClickAction.None,
            ClickZoomKeyTogglesFit = false,
            MouseWheelAction = MouseWheelAction.Zoom,
            KineticPanEnabled = false,
            KineticGlideSmoothing = KineticGlideSmoothing.Predict,
            ArrowKeyNavigatesAtZoomEdge = false,
            ImageTransition = ImageTransition.None,
            ImageTransitionMs = 120,
            TouchpadSwipeEnabled = true,
            TouchpadSwipeDistancePerImage = 200,
            HiddenContextMenuItems = [],
        };
        var saved = Saved(start, (d, page) =>
        {
            var ids = page.Ids;
            SetText(d, ids.ClickZoomPercent, "250");
            Tick(d, ids.ClickToZoom, true);
            SetText(d, ids.ArrowPanStep, "35");
            Choose(d, ids.KeyboardZoomAnchor, 0);
            Choose(d, ids.FitWidthAnchor, 2);
            Choose(d, ids.FitWidthAnchor2, 1);
            Tick(d, ids.KeepZoom, true);
            SetText(d, ids.KeyboardZoomStep, "15");
            Choose(d, ids.MiddleClick, 10);
            Tick(d, ids.ClickZoomKeyTogglesFit, true);
            Choose(d, ids.MouseWheel, 1);
            Tick(d, ids.KineticPan, true);
            Choose(d, ids.KineticSmoothing, 0);
            Tick(d, ids.ArrowKeyAtEdge, true);
            Choose(d, ids.ImageTransition, 1);
            SetText(d, ids.ImageTransitionMs, "333");
            Tick(d, ids.TouchpadSwipe, false);
            SetText(d, ids.TouchpadDistance, "345");
        });

        Assert.Equal(250, saved.ClickZoomPercent);
        Assert.True(saved.ClickToZoomEnabled);
        Assert.Equal(35, saved.ArrowPanStepPercent);
        Assert.Equal(KeyboardZoomAnchor.Pointer, saved.KeyboardZoomAnchor);
        Assert.Equal(FitWidthAnchor.BottomThird, saved.FitWidthAnchor);
        Assert.Equal(FitWidthAnchor.TopThird, saved.FitWidthAnchor2);
        Assert.True(saved.KeepZoomAcrossImages);
        Assert.Equal(15, saved.KeyboardZoomStepPercent);
        Assert.Equal(MiddleClickAction.OpenFolder, saved.MiddleClickAction);
        Assert.True(saved.ClickZoomKeyTogglesFit);
        Assert.Equal(MouseWheelAction.Navigate, saved.MouseWheelAction);
        Assert.True(saved.KineticPanEnabled);
        Assert.Equal(KineticGlideSmoothing.Off, saved.KineticGlideSmoothing);
        Assert.True(saved.ArrowKeyNavigatesAtZoomEdge);
        Assert.Equal(ImageTransition.Fade, saved.ImageTransition);
        Assert.Equal(333, saved.ImageTransitionMs);
        Assert.False(saved.TouchpadSwipeEnabled);
        Assert.Equal(345, saved.TouchpadSwipeDistancePerImage);
        Assert.Empty(ContextMenuItems.EffectiveHidden(saved));
    }

    [Fact]
    public void Mouse_EachTickBox_WritesOnlyItsOwnProperty()
    {
        // One box at a time (the all-at-once edit cannot tell two crossed boxes apart).
        var getters = new Func<AppSettings, bool>[]
        {
            s => s.ClickToZoomEnabled, s => s.KeepZoomAcrossImages, s => s.ClickZoomKeyTogglesFit, s => s.KineticPanEnabled,
            s => s.ArrowKeyNavigatesAtZoomEdge, s => s.TouchpadSwipeEnabled,
        };
        for (var which = 0; which < getters.Length; which++)
        {
            var saved = Saved(new AppSettings { TouchpadSwipeEnabled = false }, (d, page) =>
            {
                var ids = page.Ids;
                var boxes = new[] { ids.ClickToZoom, ids.KeepZoom, ids.ClickZoomKeyTogglesFit, ids.KineticPan, ids.ArrowKeyAtEdge, ids.TouchpadSwipe };
                foreach (var box in boxes)
                {
                    Tick(d, box, false);
                }

                Tick(d, boxes[which], true);
            });
            for (var other = 0; other < getters.Length; other++)
            {
                Assert.Equal(other == which, getters[other](saved));
            }
        }
    }

    [Fact]
    public void Mouse_DoesNotTouchThePropertiesOfTheOtherPages()
    {
        var original = new AppSettings
        {
            LoggingEnabled = true,
            InitialViewMode = InitialViewMode.FitHeight,
            ToolbarOpacityPercent = 61,
            PreloadForwardCount = 7,
            ConfirmBeforeDelete = true,
            ExternalEditorPath = @"C:\Tools\edit.exe",
        };
        original.Shortcuts.Next = "N";
        var saved = Saved(original, (d, page) => SetText(d, page.Ids.ClickZoomPercent, "321"));
        Assert.Equal(321, saved.ClickZoomPercent);
        Assert.True(saved.LoggingEnabled);
        Assert.Equal(InitialViewMode.FitHeight, saved.InitialViewMode);
        Assert.Equal(61, saved.ToolbarOpacityPercent);
        Assert.Equal(7, saved.PreloadForwardCount);
        Assert.True(saved.ConfirmBeforeDelete);
        Assert.Equal(@"C:\Tools\edit.exe", saved.ExternalEditorPath);
        Assert.Equal("N", saved.Shortcuts.Next);
    }

    // ---- combo order = form index ------------------------------------------------------------------------------------------------

    private static string Label(string keyPrefix, string enumName) => Localizer.Current.Get(keyPrefix + char.ToLowerInvariant(enumName[0]) + enumName[1..]);

    [Fact]
    public void Mouse_MiddleClick_HasElevenItems_InEnumOrder_AndEveryIndexRoundTrips()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var names = Enum.GetNames<MiddleClickAction>();
        Assert.Equal(11, names.Length);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var combo = Page(d).Ids.MiddleClick;
            Assert.Equal(names.Length, ComboCount(d, combo));
            for (var i = 0; i < names.Length; i++)
            {
                Assert.Equal((int)Enum.Parse<MiddleClickAction>(names[i]), i);
                Assert.Equal(Label("enum.middleClickAction.", names[i]), ComboText(d, combo, i));
            }

            d.Close();
        });

        foreach (var value in Enum.GetValues<MiddleClickAction>())
        {
            var saved = Saved(new AppSettings { MiddleClickAction = MiddleClickAction.None }, (d, page) => Choose(d, page.Ids.MiddleClick, (int)value));
            Assert.Equal(value, saved.MiddleClickAction);
            Show(new AppSettings { MiddleClickAction = value }, (d, _) =>
            {
                GoTo(d, MouseIndex);
                Assert.Equal((int)value, ComboIndex(d, Page(d).Ids.MiddleClick));
                d.Close();
            });
        }
    }

    [Fact]
    public void Mouse_FitWidthAnchorCombos_AreIndependent_AndListCentreTopBottom()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        foreach (var first in Enum.GetValues<FitWidthAnchor>())
        {
            foreach (var second in Enum.GetValues<FitWidthAnchor>())
            {
                var saved = Saved(new AppSettings(), (d, page) =>
                {
                    Choose(d, page.Ids.FitWidthAnchor, (int)first);
                    Choose(d, page.Ids.FitWidthAnchor2, (int)second);
                });
                Assert.Equal(first, saved.FitWidthAnchor);
                Assert.Equal(second, saved.FitWidthAnchor2);
            }
        }

        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            foreach (var combo in new[] { Page(d).Ids.FitWidthAnchor, Page(d).Ids.FitWidthAnchor2 })
            {
                Assert.Equal(3, ComboCount(d, combo));
                Assert.Equal(Tr.EnumFitWidthAnchorCentre, ComboText(d, combo, 0));
                Assert.Equal(Tr.EnumFitWidthAnchorTopThird, ComboText(d, combo, 1));
                Assert.Equal(Tr.EnumFitWidthAnchorBottomThird, ComboText(d, combo, 2));
            }

            d.Close();
        });
    }

    [Fact]
    public void Mouse_TwoItemCombos_FollowTheirIndices()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            Assert.Equal([Tr.EnumKeyboardZoomAnchorPointer, Tr.EnumKeyboardZoomAnchorViewportCentre], Items(d, ids.KeyboardZoomAnchor));
            Assert.Equal([Tr.EnumMouseWheelActionZoom, Tr.EnumMouseWheelActionNavigate], Items(d, ids.MouseWheel));
            Assert.Equal([Tr.EnumKineticGlideSmoothingOff, Tr.EnumKineticGlideSmoothingPredict], Items(d, ids.KineticSmoothing));
            Assert.Equal([Tr.EnumImageTransitionNone, Tr.EnumImageTransitionFade], Items(d, ids.ImageTransition));
            d.Close();
        });

        foreach (var anchor in Enum.GetValues<KeyboardZoomAnchor>())
        {
            Assert.Equal(anchor, Saved(new AppSettings(), (d, p) => Choose(d, p.Ids.KeyboardZoomAnchor, anchor == KeyboardZoomAnchor.Pointer ? 0 : 1)).KeyboardZoomAnchor);
        }

        foreach (var wheel in Enum.GetValues<MouseWheelAction>())
        {
            Assert.Equal(wheel, Saved(new AppSettings(), (d, p) => Choose(d, p.Ids.MouseWheel, wheel == MouseWheelAction.Zoom ? 0 : 1)).MouseWheelAction);
        }

        foreach (var smoothing in Enum.GetValues<KineticGlideSmoothing>())
        {
            Assert.Equal(smoothing, Saved(new AppSettings(), (d, p) => Choose(d, p.Ids.KineticSmoothing, smoothing == KineticGlideSmoothing.Off ? 0 : 1)).KineticGlideSmoothing);
        }

        foreach (var transition in Enum.GetValues<ImageTransition>())
        {
            Assert.Equal(transition, Saved(new AppSettings(), (d, p) => Choose(d, p.Ids.ImageTransition, transition == ImageTransition.None ? 0 : 1)).ImageTransition);
        }
    }

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Items(SettingsDialog d, int combo)
        => Enumerable.Range(0, ComboCount(d, combo)).Select(i => ComboText(d, combo, i)).ToArray();

    // ---- the right-click menu: 19 tick boxes -------------------------------------------------------------------------------------

    [Fact]
    public void ContextMenu_HasOneTickBoxPerItem_InMenuOrder_WithTheItemsLabel()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var items = ContextMenuItems.Items;
            Assert.Equal(19, items.Count); // FEAT-RENAME added RenameFile
            Assert.Equal(items.Select(i => i.Id).ToArray(), Page(d).MenuChecks.Keys.ToArray());
            var ids = Page(d).MenuChecks.Values.ToArray();
            Assert.Equal(ids.OrderBy(id => id).ToArray(), ids); // declared (= Tab) order is the menu order
            var previousTop = int.MinValue;
            foreach (var item in items)
            {
                var id = Page(d).MenuChecks[item.Id];
                Assert.Equal(SettingsPresenter.ContextMenuItemLabel(item.Id), Txt(d, id));
                Assert.NotEmpty(Txt(d, id));
                var rect = RectInViewport(d, id);
                Assert.True(rect.Top > previousTop, $"{item.Id} is laid out below the previous item");
                previousTop = rect.Top;
            }

            // Children of the Zoom submenu are indented relative to the top-level items.
            var topLeft = RectInViewport(d, Page(d).MenuChecks[ContextMenuItemId.OpenFolder]).Left;
            foreach (var child in items.Where(i => i.Parent is not null))
            {
                Assert.True(RectInViewport(d, Page(d).MenuChecks[child.Id]).Left > topLeft, $"{child.Id} is indented");
            }

            d.Close();
        });
    }

    [Fact]
    public void ContextMenu_TicksFollowTheStoredHiddenSet_AndTheDefaultHidesTheFourItems()
    {
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var effective = ContextMenuItems.EffectiveHidden(new AppSettings());
            foreach (var (item, id) in Page(d).MenuChecks)
            {
                Assert.Equal(!effective.Contains(item), Checked(d, id));
            }

            Assert.False(Checked(d, Page(d).MenuChecks[ContextMenuItemId.MoveToRecycleBin]));
            Assert.True(Checked(d, Page(d).MenuChecks[ContextMenuItemId.Settings]));
            d.Close();
        });
    }

    [Fact]
    public void ContextMenu_UntickingAnItem_HidesExactlyThatItem_AndTickingShowsItAgain()
    {
        foreach (var item in ContextMenuItems.Items.Where(i => !i.Locked))
        {
            var hidden = Saved(new AppSettings { HiddenContextMenuItems = [] }, (d, page) => Tick(d, page.MenuChecks[item.Id], false));
            Assert.Equal([item.Id], ContextMenuItems.EffectiveHidden(hidden).ToArray());

            var shown = Saved(new AppSettings { HiddenContextMenuItems = [item.Id.ToString()] }, (d, page) => Tick(d, page.MenuChecks[item.Id], true));
            Assert.DoesNotContain(item.Id, ContextMenuItems.EffectiveHidden(shown));
        }
    }

    [Fact]
    public void ContextMenu_LegacyFlagsAreDerivedFromTheTicks()
    {
        var saved = Saved(new AppSettings { HiddenContextMenuItems = [] }, (d, page) =>
        {
            foreach (var id in new[] { ContextMenuItemId.Fit, ContextMenuItemId.ZoomToLevel, ContextMenuItemId.ZoomSubmenu, ContextMenuItemId.MoveToRecycleBin })
            {
                Tick(d, page.MenuChecks[id], false);
            }
        });
        Assert.False(saved.ShowZoomMenuItems);
        Assert.True(saved.ShowFolderMenuItems);
        Assert.False(saved.ShowRecycleMenuItem);
    }

    [Fact]
    public void ContextMenu_SettingsItem_CanNeverBeUnticked_AndHasATooltip()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings { HiddenContextMenuItems = [] }, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var id = Page(d).MenuChecks[ContextMenuItemId.Settings];
            Tick(d, id, false); // the user clicks it: the box ticks itself again
            Assert.True(Checked(d, id));
            Assert.Equal(Tr.SettingsContextMenuSettingsLockedTooltip, d.ItemTooltipText(id, 0));
            Assert.True(d.TooltipToolCount >= 1);
            // only the locked item has a tooltip
            Assert.Null(d.ItemTooltipText(Page(d).MenuChecks[ContextMenuItemId.Undo], 0));
            Assert.True(d.TrySaveAll());
            Assert.DoesNotContain(ContextMenuItemId.Settings, ContextMenuItems.EffectiveHidden(d.Settings));
            d.Close();
        });
    }

    [Fact]
    public void ContextMenu_ChildrenOfTheZoomSubmenu_AreOwnedByTheirOwnTickBox()
    {
        var saved = Saved(new AppSettings { HiddenContextMenuItems = [] }, (d, page) =>
        {
            Tick(d, page.MenuChecks[ContextMenuItemId.ZoomPresets], false);
            Tick(d, page.MenuChecks[ContextMenuItemId.ZoomFitHeight], false);
        });
        Assert.Equal([ContextMenuItemId.ZoomFitHeight, ContextMenuItemId.ZoomPresets],
            ContextMenuItems.EffectiveHidden(saved).OrderBy(i => (int)i).ToArray());
    }

    // ---- validation ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("clickZoom", "5")]
    [InlineData("clickZoom", "abc")]
    [InlineData("arrowPan", "0")]
    [InlineData("arrowPan", "101")]
    [InlineData("keyboardStep", "4")]
    [InlineData("keyboardStep", "")]
    [InlineData("transitionMs", "10")]
    [InlineData("transitionMs", "9999")]
    [InlineData("touchpad", "29")]
    [InlineData("touchpad", "x")]
    public void Mouse_InvalidValues_AreRefused_WithThePresentersMessage_FocusTheField_AndSaveNothing(string field, string text)
    {
        var warnings = new FakeWarnings();
        // Fade keeps the duration box enabled (a disabled box cannot take the focus, as in the WPF window).
        var (_, dialog) = Show(new AppSettings { ImageTransition = ImageTransition.Fade }, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            var id = field switch
            {
                "clickZoom" => ids.ClickZoomPercent,
                "arrowPan" => ids.ArrowPanStep,
                "keyboardStep" => ids.KeyboardZoomStep,
                "transitionMs" => ids.ImageTransitionMs,
                _ => ids.TouchpadDistance,
            };
            SetText(d, id, text);
            if (field != "clickZoom")
            {
                GoTo(d, 0); // Save from another page: the refused field's page must come back
            }

            Assert.False(d.TrySaveAll());
            Assert.Equal("Mouse", d.ActivePageKey);
            if (field != "clickZoom")
            {
                Assert.Equal(H(d, id), User32.GetFocus());
            }

            d.Close();
        }, Options(warnings, new FakeFiles()));

        Assert.False(dialog.Saved);
        var shown = Assert.Single(warnings.Shown);
        var expected = field switch
        {
            "clickZoom" => Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent),
            "arrowPan" => Tr.DialogSettingsInvalidArrowPanStepPercent(AppSettings.MinArrowPanStepPercent, AppSettings.MaxArrowPanStepPercent),
            "keyboardStep" => Tr.DialogSettingsInvalidKeyboardZoomStepPercent(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent),
            "transitionMs" => Tr.DialogSettingsInvalidImageTransitionMs(AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs),
            _ => Tr.DialogSettingsInvalidTouchpadSwipeDistance(AppSettings.MinTouchpadSwipeDistancePerImage, AppSettings.MaxTouchpadSwipeDistancePerImage),
        };
        Assert.Equal(expected, shown.Message);
    }

    [Fact]
    public void Mouse_BoundaryValues_AreAccepted()
    {
        var saved = Saved(new AppSettings(), (d, page) =>
        {
            SetText(d, page.Ids.ClickZoomPercent, N(AppSettings.MinClickZoomPercent));
            SetText(d, page.Ids.ArrowPanStep, N(AppSettings.MaxArrowPanStepPercent));
            SetText(d, page.Ids.KeyboardZoomStep, N(AppSettings.MinKeyboardZoomStepPercent));
            SetText(d, page.Ids.ImageTransitionMs, N(AppSettings.MaxImageTransitionMs));
            SetText(d, page.Ids.TouchpadDistance, N(AppSettings.MinTouchpadSwipeDistancePerImage));
        });
        Assert.Equal(AppSettings.MinClickZoomPercent, saved.ClickZoomPercent);
        Assert.Equal(AppSettings.MaxArrowPanStepPercent, saved.ArrowPanStepPercent);
        Assert.Equal(AppSettings.MinKeyboardZoomStepPercent, saved.KeyboardZoomStepPercent);
        Assert.Equal(AppSettings.MaxImageTransitionMs, saved.ImageTransitionMs);
        Assert.Equal(AppSettings.MinTouchpadSwipeDistancePerImage, saved.TouchpadSwipeDistancePerImage);
    }

    // ---- dependants ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Mouse_FadeDuration_IsEnabledOnlyForTheFadeTransition()
    {
        Show(new AppSettings { ImageTransition = ImageTransition.None }, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            Assert.False(Enabled(d, ids.ImageTransitionMs));
            Choose(d, ids.ImageTransition, 1);
            Assert.True(Enabled(d, ids.ImageTransitionMs));
            Choose(d, ids.ImageTransition, 0);
            Assert.False(Enabled(d, ids.ImageTransitionMs));
            d.Close();
        });
        Show(new AppSettings { ImageTransition = ImageTransition.Fade }, (d, _) =>
        {
            GoTo(d, MouseIndex);
            Assert.True(Enabled(d, Page(d).Ids.ImageTransitionMs));
            d.Close();
        });
    }

    // ---- zoom shortcut summary ---------------------------------------------------------------------------------------------------

    private sealed class FakeShortcutsPage : SettingsPageBase
    {
        public string Typed { get; set; } = "Q";

        public override string Key => "Shortcuts";

        public override void Declare(SettingsPageBuilder page) => page.Note(() => "fake", aligned: false);

        public override void Load(SettingsFormState state)
        {
        }

        public override void Collect(SettingsFormState state) => state.ShortcutTexts[nameof(ShortcutMappings.ClickZoom)] = Typed;
    }

    [Fact]
    public void ZoomShortcutSummary_ShowsTheSavedShortcuts_AndFollowsWhatIsTypedOnTheShortcutsPage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = new AppSettings();
        settings.Shortcuts.ClickZoom = "Z";
        var fake = new FakeShortcutsPage();
        var pages = SettingsPageCatalog.CreatePages().ToArray();
        var shortcuts = SettingsPresenter.Pages.ToList().FindIndex(p => p.Key == "Shortcuts");
        pages[shortcuts] = fake;
        Show(settings, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var summary = Page(d).Ids.ZoomShortcutsSummary;
            Assert.Equal($"{Tr.SettingsShortcutClickZoom}: Q", Txt(d, summary).Split('\n')[0].TrimEnd('\r'));
            // typed on the Shortcuts page, then back to Mouse: the summary follows
            fake.Typed = "Ctrl+9";
            GoTo(d, shortcuts);
            GoTo(d, MouseIndex);
            Assert.StartsWith($"{Tr.SettingsShortcutClickZoom}: Ctrl+9", Txt(d, summary), StringComparison.Ordinal);
            Assert.Contains(Tr.SettingsShortcutFitHeight, Txt(d, summary), StringComparison.Ordinal);
            Assert.Equal(8, Txt(d, summary).Split('\n').Length);
            d.Close();
        }, Options(new FakeWarnings(), new FakeFiles(), pages: pages));
    }

    [Fact]
    public void ZoomShortcutSummary_WithTheRealShortcuts_ListsTheEightZoomKeys()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = new AppSettings();
        settings.Shortcuts.ClickZoom = "Z";
        Show(settings, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var text = Txt(d, Page(d).Ids.ZoomShortcutsSummary);
            Assert.StartsWith($"{Tr.SettingsShortcutClickZoom}: Z", text, StringComparison.Ordinal);
            Assert.Equal(8, text.Split('\n').Length);
            d.Close();
        });
    }

    // ---- language ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheLanguage_RetextsEveryControlOfThePage_IncludingTheCodeSetSummaryAndMenuLabels()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        Show(new AppSettings(), (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            var english = (Check: Txt(d, ids.ClickToZoom), Combo: ComboText(d, ids.MiddleClick, 5), Summary: Txt(d, ids.ZoomShortcutsSummary),
                Menu: Txt(d, Page(d).MenuChecks[ContextMenuItemId.OpenFolder]));
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            Drain(d);
            Assert.Equal(Tr.SettingsClickToZoomLabel, Txt(d, ids.ClickToZoom));
            Assert.NotEqual(english.Check, Txt(d, ids.ClickToZoom));
            Assert.Equal(Tr.EnumMiddleClickActionFitWidth2, ComboText(d, ids.MiddleClick, 5));
            Assert.NotEqual(english.Combo, ComboText(d, ids.MiddleClick, 5));
            Assert.StartsWith($"{Tr.SettingsShortcutClickZoom}:", Txt(d, ids.ZoomShortcutsSummary), StringComparison.Ordinal);
            Assert.NotEqual(english.Summary, Txt(d, ids.ZoomShortcutsSummary));
            Assert.Equal(SettingsPresenter.ContextMenuItemLabel(ContextMenuItemId.OpenFolder), Txt(d, Page(d).MenuChecks[ContextMenuItemId.OpenFolder]));
            Assert.NotEqual(english.Menu, Txt(d, Page(d).MenuChecks[ContextMenuItemId.OpenFolder]));
            // the value of the combo is kept across the language switch
            Assert.Equal(2, ComboIndex(d, ids.MiddleClick));
            Localizer.SetCurrent(TestLocalization.English);
            Drain(d);
            Assert.Equal(english.Summary, Txt(d, ids.ZoomShortcutsSummary));
            d.Close();
        });
    }

    // ---- Reset page ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ResetPage_OnMouse_RestoresTheDefaults_OfThisPageOnly_AndDoesNotSave()
    {
        var settings = NonDefault();
        settings.LoggingEnabled = true;
        Show(settings, (d, _) =>
        {
            GoTo(d, MouseIndex);
            var ids = Page(d).Ids;
            Command(d, SettingsDialog.ResetPageId);
            var defaults = new AppSettings();
            Assert.Equal(N(defaults.ClickZoomPercent), Txt(d, ids.ClickZoomPercent));
            Assert.Equal(defaults.ClickToZoomEnabled, Checked(d, ids.ClickToZoom));
            Assert.Equal(N(defaults.ArrowPanStepPercent), Txt(d, ids.ArrowPanStep));
            Assert.Equal((int)defaults.MiddleClickAction, ComboIndex(d, ids.MiddleClick));
            Assert.Equal(defaults.KineticPanEnabled, Checked(d, ids.KineticPan));
            Assert.Equal(N(defaults.ImageTransitionMs), Txt(d, ids.ImageTransitionMs));
            Assert.Equal(defaults.TouchpadSwipeEnabled, Checked(d, ids.TouchpadSwipe));
            var effective = ContextMenuItems.EffectiveHidden(defaults);
            foreach (var (item, id) in Page(d).MenuChecks)
            {
                Assert.Equal(!effective.Contains(item), Checked(d, id));
            }

            // the working copy only changes on Save; another page keeps its value
            Assert.Equal(250, d.Settings.ClickZoomPercent);
            Assert.True(d.Settings.LoggingEnabled);
            d.Close();
        });
    }

    // ---- Tab / handles / lazy ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Tab_WalksTheMousePage_InDeclarationOrder_ThroughTheTickBoxesToo()
    {
        Show(new AppSettings { ImageTransition = ImageTransition.Fade }, (d, _) =>
        {
            GoTo(d, MouseIndex);
            User32.SetFocus(H(d, SettingsDialog.NavId));
            var expected = d.PageItems(MouseIndex)
                .Where(i => i.Class != DialogControlClass.Static && (i.Style & DialogConstants.WsTabStop) != 0)
                .Select(i => i.Id).Where(id => User32.IsWindowEnabled(H(d, id))).ToList();
            Assert.True(expected.Count >= 18 + 10);
            var visited = new List<int>();
            for (var step = 0; step < expected.Count + 8; step++)
            {
                Key(d, User32.GetFocus(), VkTab);
                var id = User32.GetDlgCtrlID(User32.GetFocus());
                if (id < 1000)
                {
                    break; // left the page (the frame's own buttons)
                }

                visited.Add(id);
            }

            Assert.Equal(expected, visited);
            Assert.Equal(expected.OrderBy(id => id).ToList(), expected);
            d.Close();
        });
    }

    [Fact]
    public void OpeningTheMousePage_DoesNotLeakGdiOrUserObjects()
    {
        var (gdi, user) = OnSta(() =>
        {
            void One() => ShowOnOwner(NewDialog(), (d, _) =>
            {
                GoTo((SettingsDialog)d, MouseIndex);
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
    public void TheMouseControls_ExistOnlyOnceThePageIsOpened_AndSaveWithoutVisitingKeepsItsValues()
    {
        var settings = NonDefault();
        AppSettings? saved = null;
        Show(settings, (d, _) =>
        {
            Assert.False(d.IsPageCreated(MouseIndex));
            Assert.Equal(0, H(d, d.PageItems(MouseIndex)[0].Id));
            Assert.True(d.TrySaveAll());
            saved = d.Settings;
            Assert.False(d.IsPageCreated(MouseIndex), "saving must not create the page");
            d.Close();
        }, realizeAll: false);

        Assert.Equal(250, saved!.ClickZoomPercent);
        Assert.Equal(MiddleClickAction.OpenFolder, saved.MiddleClickAction);
        Assert.Equal(345, saved.TouchpadSwipeDistancePerImage);
        Assert.Equal(ImageTransition.Fade, saved.ImageTransition);
        Assert.Equal(
            new[] { ContextMenuItemId.Undo, ContextMenuItemId.ZoomPresets, ContextMenuItemId.CopyFullPath }.OrderBy(i => (int)i).ToArray(),
            ContextMenuItems.EffectiveHidden(saved).OrderBy(i => (int)i).ToArray());
    }

    // ---- snapshots (manual) ------------------------------------------------------------------------------------------------------

    /// <summary><c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;dir&gt;</c>: <c>settings-mouse-{en,vi}[-150][-mid|-bottom].png</c> (nothing without the variable).</summary>
    [Fact]
    [Trait("Category", "Manual")]
    public void WriteSnapshots_OfTheMousePage()
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
                var settings = new AppSettings { ImageTransition = ImageTransition.Fade, HiddenContextMenuItems = [] };
                foreach (var (suffix, dpi, scroll) in new[]
                {
                    ("", 0u, -1), ("-mid", 0u, ScrollConstants.SbPageDown), ("-bottom", 0u, ScrollConstants.SbBottom),
                    ("-150", 144u, -1), ("-150-bottom", 144u, ScrollConstants.SbBottom),
                })
                {
                    var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
                    dialog.Presenter.ShellIntegration = new FakeShell();
                    dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
                    ShowOnOwner(dialog, (d, _) =>
                    {
                        var s = (SettingsDialog)d;
                        GoTo(s, MouseIndex);
                        if (dpi != 0)
                        {
                            ChangeDpi(s, dpi);
                        }

                        if (scroll >= 0)
                        {
                            var bar = s.ControlHandle(SettingsDialog.ScrollId);
                            var count = scroll == ScrollConstants.SbBottom ? 1 : 3;
                            for (var i = 0; i < count; i++)
                            {
                                User32.SendMessage(s.Hwnd, ControlConstants.WmVScroll, scroll, bar);
                            }
                        }

                        Drain(d);
                        NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, $"settings-mouse-{code}{suffix}.png"), d.Hwnd);
                        d.Close();
                    });
                }

                return 0;
            });
        }
    }
}
