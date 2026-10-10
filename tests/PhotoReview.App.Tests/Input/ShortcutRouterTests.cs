using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Input;

[Trait("Category", "HotPath")]
public sealed class ShortcutRouterTests
{
    private readonly AppSettings _settings;
    private readonly ShortcutRouter _router;

    public ShortcutRouterTests()
    {
        _settings = new AppSettings
        {
            Shortcuts = new ShortcutMappings
            {
                Fullscreen = "F11",
                NextFolder = "PageDown",
                PreviousFolder = "PageUp",
                FirstImage = "Home",
                Undo = "Z",
                Compare = "C",
                SendToRecycleBin = "Delete",
                Skip = "S",
                ToggleFit = "F",
                ZoomIn = "OemPlus",
                ZoomOut = "OemMinus",
                Next = "Right",
                Previous = "Left"
            },
            Actions =
            [
                new ReviewAction { Name = "Action 1", Shortcut = "D1", Operation = FileOperationType.Move, Destination = "Sorted" },
                new ReviewAction { Name = "Action 2", Shortcut = "D2", Operation = FileOperationType.Copy, Destination = "Selected" }
            ]
        };

        _router = new ShortcutRouter(_settings);
    }

    // Alt+F11 reaches WPF as KeyId.System with SystemKey = F11, so both spellings must resolve.
    [Theory]
    [InlineData(KeyId.F11, KeyId.None, KeyModifiers.None)]
    [InlineData(KeyId.System, KeyId.F11, KeyModifiers.Alt)]
    public void Fullscreen_ResolvesToFullscreen(KeyId key, KeyId systemKey, KeyModifiers modifiers)
    {
        var cmd = _router.TryResolve(key, systemKey, modifiers, isFullscreen: false, hasImage: false);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandType.Fullscreen, cmd.Value.Type);
    }

    [Theory]
    [InlineData(true, ReviewCommandType.ExitFullscreen)]
    [InlineData(false, ReviewCommandType.Close)]
    public void Escape_ResolvesByFullscreenState(bool isFullscreen, ReviewCommandType expected)
    {
        var cmd = _router.TryResolve(KeyId.Escape, KeyId.None, KeyModifiers.None, isFullscreen: isFullscreen, hasImage: true);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
    }

    [Fact]
    public void FolderNavigation_ResolvesCorrectly()
    {
        var nextFolder = _router.TryResolve(KeyId.PageDown, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false);
        var prevFolder = _router.TryResolve(KeyId.PageUp, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false);

        Assert.Equal(ReviewCommandType.NextFolder, nextFolder?.Type);
        Assert.Equal(ReviewCommandType.PreviousFolder, prevFolder?.Type);
    }

    [Fact]
    public void FirstImage_WhenHasImage_ResolvesToFirstImage()
    {
        var cmd = _router.TryResolve(KeyId.Home, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.FirstImage, cmd?.Type);
    }

    [Fact]
    public void FirstImage_WhenNoImage_ReturnsNull()
    {
        var cmd = _router.TryResolve(KeyId.Home, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false);
        Assert.Null(cmd);
    }

    [Fact]
    public void Undo_RequiresControlModifier()
    {
        var withCtrl = _router.TryResolve(KeyId.Z, KeyId.None, KeyModifiers.Control, isFullscreen: false, hasImage: false);
        var withoutCtrl = _router.TryResolve(KeyId.Z, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false);

        Assert.Equal(ReviewCommandType.Undo, withCtrl?.Type);
        Assert.Null(withoutCtrl);
    }

    [Fact]
    public void ImageCommands_WhenNoImage_ReturnNull()
    {
        Assert.Null(_router.TryResolve(KeyId.Right, KeyId.None, KeyModifiers.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(KeyId.Left, KeyId.None, KeyModifiers.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(KeyId.Delete, KeyId.None, KeyModifiers.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(KeyId.S, KeyId.None, KeyModifiers.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, false, hasImage: false));
    }

    [Theory]
    [InlineData(false, false, false)] // not visible, no pair: nothing to compare
    [InlineData(true, false, true)]   // not visible, has pair: open compare
    [InlineData(false, true, true)]   // visible: always closable, even without a pair
    public void Compare_ResolvesToToggleOnlyWhenThereIsSomethingToToggle(bool hasComparePair, bool isCompareVisible, bool resolves)
    {
        var cmd = _router.TryResolve(KeyId.C, KeyId.None, KeyModifiers.None, false, hasImage: true, hasComparePair: hasComparePair, isCompareVisible: isCompareVisible);
        Assert.Equal(resolves ? ReviewCommandType.ToggleCompare : null, cmd?.Type);
    }

    [Fact]
    public void CustomActions_ResolveToCorrectIndex()
    {
        var action0 = _router.TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, false, hasImage: true);
        var action1 = _router.TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, false, hasImage: true);

        Assert.NotNull(action0);
        Assert.Equal(ReviewCommandType.RunAction, action0.Value.Type);
        Assert.Equal(0, action0.Value.ActionIndex);

        Assert.NotNull(action1);
        Assert.Equal(ReviewCommandType.RunAction, action1.Value.Type);
        Assert.Equal(1, action1.Value.ActionIndex);
    }

    [Fact]
    public void NavigationAndZoomCommands_ResolveCorrectly()
    {
        Assert.Equal(ReviewCommandType.Next, _router.TryResolve(KeyId.Right, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Previous, _router.TryResolve(KeyId.Left, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Recycle, _router.TryResolve(KeyId.Delete, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Skip, _router.TryResolve(KeyId.S, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ToggleFit, _router.TryResolve(KeyId.F, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomIn, _router.TryResolve(KeyId.OemPlus, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomOut, _router.TryResolve(KeyId.OemMinus, KeyId.None, KeyModifiers.None, false, true)?.Type);
    }

    // --- Optional shortcuts: LastImage (End), ZoomActualSize (D1), ToggleInfoOverlay (I); empty = disabled ---

    private static ShortcutRouter DefaultRouter() => new(new AppSettings());

    [Fact]
    public void LastImage_WhenHasImage_ResolvesToLastImage()
    {
        var cmd = DefaultRouter().TryResolve(KeyId.End, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.LastImage, cmd?.Type);
    }

    [Fact]
    public void LastImage_WhenNoImage_ReturnsNull_LikeFirstImage()
    {
        var router = DefaultRouter();
        Assert.Null(router.TryResolve(KeyId.End, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false));
        Assert.Null(router.TryResolve(KeyId.Home, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false));
    }

    [Fact]
    public void ZoomActualSize_WhenHasImage_ResolvesToZoomActualSize()
    {
        var cmd = DefaultRouter().TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.ZoomActualSize, cmd?.Type);
    }

    [Fact]
    public void ZoomActualSize_WhenNoImage_ReturnsNull()
    {
        Assert.Null(DefaultRouter().TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false));
    }

    [Fact]
    public void ClickZoom_WhenHasImage_ResolvesToClickZoom()
    {
        var cmd = DefaultRouter().TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.ClickZoom, cmd?.Type);
    }

    [Fact]
    public void ClickZoom_WhenNoImage_ReturnsNull()
    {
        Assert.Null(DefaultRouter().TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleInfoOverlay_ResolvesWithOrWithoutImage(bool hasImage)
    {
        var cmd = DefaultRouter().TryResolve(KeyId.I, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: hasImage);
        Assert.Equal(ReviewCommandType.ToggleInfoOverlay, cmd?.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAKey")]
    [InlineData("1")]
    public void OptionalShortcuts_EmptyOrUnparseable_AreDisabled(string value)
    {
        var settings = new AppSettings();
        settings.Shortcuts.LastImage = value;
        settings.Shortcuts.ZoomActualSize = value;
        settings.Shortcuts.ToggleInfoOverlay = value;
        settings.Shortcuts.ClickZoom = value;
        var router = new ShortcutRouter(settings);

        Assert.Null(router.TryResolve(KeyId.End, KeyId.None, KeyModifiers.None, false, hasImage: true));
        Assert.Null(router.TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, false, hasImage: true));
        Assert.Null(router.TryResolve(KeyId.I, KeyId.None, KeyModifiers.None, false, hasImage: true));
        Assert.Null(router.TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, false, hasImage: true));
    }

    [Fact]
    public void OptionalShortcuts_Rebuild_PicksUpCustomKeys()
    {
        var settings = new AppSettings();
        var router = new ShortcutRouter(settings);
        settings.Shortcuts.LastImage = "F7";
        settings.Shortcuts.ZoomActualSize = "D0";
        settings.Shortcuts.ToggleInfoOverlay = "P";
        settings.Shortcuts.ClickZoom = "D5";
        router.Rebuild(settings);

        Assert.Equal(ReviewCommandType.LastImage, router.TryResolve(KeyId.F7, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomActualSize, router.TryResolve(KeyId.D0, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ToggleInfoOverlay, router.TryResolve(KeyId.P, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ClickZoom, router.TryResolve(KeyId.D5, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Null(router.TryResolve(KeyId.End, KeyId.None, KeyModifiers.None, false, true));
        Assert.Null(router.TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, false, true)); // old ClickZoom default no longer bound
    }

    [Fact]
    public void ZoomActualSize_ActionOnTheSameKey_KeepsPriority()
    {
        // The fixture binds an action to D1 (an old config): actions are resolved before the zoom group.
        var cmd = _router.TryResolve(KeyId.D1, KeyId.None, KeyModifiers.None, false, hasImage: true);
        Assert.Equal(ReviewCommandType.RunAction, cmd?.Type);
    }

    [Fact]
    public void ClickZoom_ActionOnTheSameKey_KeepsPriority()
    {
        // The fixture binds an action to D2 (ClickZoom's default): actions are resolved before the zoom group.
        var cmd = _router.TryResolve(KeyId.D2, KeyId.None, KeyModifiers.None, false, hasImage: true);
        Assert.Equal(ReviewCommandType.RunAction, cmd?.Type);
    }

    // ---- PR-B: FitWidth (W) / FitHeight (H) / ToggleKeepZoom (K) -- the fixture's Shortcuts object initializer ----
    // ---- leaves these at ShortcutMappings' own defaults, so no extra wiring is needed here. ----

    [Fact]
    public void FitWidth_ResolvesOnlyWithAnImage()
    {
        Assert.Equal(ReviewCommandType.FitWidth, _router.TryResolve(KeyId.W, KeyId.None, KeyModifiers.None, false, hasImage: true)?.Type);
        Assert.Null(_router.TryResolve(KeyId.W, KeyId.None, KeyModifiers.None, false, hasImage: false));
    }

    [Fact]
    public void FitWidth2_DefaultKeyD4_ResolvesToItsOwnCommandOnlyWithAnImage()
    {
        Assert.Equal(ReviewCommandType.FitWidth2, _router.TryResolve(KeyId.D4, KeyId.None, KeyModifiers.None, false, hasImage: true)?.Type);
        Assert.Null(_router.TryResolve(KeyId.D4, KeyId.None, KeyModifiers.None, false, hasImage: false));
        // The first Fit width key is unaffected.
        Assert.Equal(ReviewCommandType.FitWidth, _router.TryResolve(KeyId.W, KeyId.None, KeyModifiers.None, false, hasImage: true)?.Type);
    }

    [Fact]
    public void FitWidth2_EmptyShortcut_IsDisabled_AndARemappedKeyFollows()
    {
        var settings = new AppSettings { Shortcuts = new ShortcutMappings { FitWidth2 = "" } };
        var router = new ShortcutRouter(settings);
        Assert.Null(router.TryResolve(KeyId.D4, KeyId.None, KeyModifiers.None, false, true));

        settings.Shortcuts.FitWidth2 = "Q";
        router.Rebuild(settings);
        Assert.Equal(ReviewCommandType.FitWidth2, router.TryResolve(KeyId.Q, KeyId.None, KeyModifiers.None, false, true)?.Type);
        Assert.Null(router.TryResolve(KeyId.D4, KeyId.None, KeyModifiers.None, false, true));
    }

    [Fact]
    public void FitHeight_ResolvesOnlyWithAnImage()
    {
        Assert.Equal(ReviewCommandType.FitHeight, _router.TryResolve(KeyId.H, KeyId.None, KeyModifiers.None, false, hasImage: true)?.Type);
        Assert.Null(_router.TryResolve(KeyId.H, KeyId.None, KeyModifiers.None, false, hasImage: false));
    }

    [Fact]
    public void ToggleKeepZoom_ResolvesWithoutAnImage_LikeToggleInfoOverlay()
    {
        Assert.Equal(ReviewCommandType.ToggleKeepZoom, _router.TryResolve(KeyId.K, KeyId.None, KeyModifiers.None, false, hasImage: false)?.Type);
    }

    [Fact]
    public void EmptyFitWidthHeightKeepZoomShortcuts_ResolveToNothing()
    {
        var settings = new AppSettings { Shortcuts = new ShortcutMappings() };
        var router = new ShortcutRouter(settings);
        settings.Shortcuts.FitWidth = "";
        settings.Shortcuts.FitHeight = "";
        settings.Shortcuts.ToggleKeepZoom = "";
        router.Rebuild(settings);

        Assert.Null(router.TryResolve(KeyId.W, KeyId.None, KeyModifiers.None, false, true));
        Assert.Null(router.TryResolve(KeyId.H, KeyId.None, KeyModifiers.None, false, true));
        Assert.Null(router.TryResolve(KeyId.K, KeyId.None, KeyModifiers.None, false, true));
    }

    // ---- Q-R42 OpenFolder (default Ctrl+O) / Q-R43 CustomZoom (default D3) ----

    [Fact]
    public void OpenFolder_RequiresControlModifier_LikeUndo()
    {
        var router = DefaultRouter();
        var withCtrl = router.TryResolve(KeyId.O, KeyId.None, KeyModifiers.Control, isFullscreen: false, hasImage: false);
        var withoutCtrl = router.TryResolve(KeyId.O, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false);

        Assert.Equal(ReviewCommandType.OpenFolder, withCtrl?.Type);
        Assert.Null(withoutCtrl);
    }

    [Fact]
    public void OpenFolder_ResolvesWithoutAnImage()
    {
        var cmd = DefaultRouter().TryResolve(KeyId.O, KeyId.None, KeyModifiers.Control, isFullscreen: false, hasImage: false);
        Assert.Equal(ReviewCommandType.OpenFolder, cmd?.Type);
    }

    [Fact]
    public void CustomZoom_WhenHasImage_ResolvesToCustomZoom()
    {
        var cmd = DefaultRouter().TryResolve(KeyId.D3, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.CustomZoom, cmd?.Type);
    }

    [Fact]
    public void CustomZoom_WhenNoImage_ReturnsNull()
    {
        Assert.Null(DefaultRouter().TryResolve(KeyId.D3, KeyId.None, KeyModifiers.None, isFullscreen: false, hasImage: false));
    }

    [Fact]
    public void ToggleCaptureMember_RequiresConfiguredShortcutAndCurrentCapturePair()
    {
        var settings = new AppSettings();
        var router = new ShortcutRouter(settings);
        Assert.Null(router.TryResolve(KeyId.B, KeyId.None, KeyModifiers.None, false, hasImage: true, hasCapturePair: true));

        settings.Shortcuts.ToggleCaptureMember = "B";
        router.Rebuild(settings);

        Assert.Null(router.TryResolve(KeyId.B, KeyId.None, KeyModifiers.None, false, hasImage: true, hasCapturePair: false));
        Assert.Equal(ReviewCommandType.ToggleCaptureMember,
            router.TryResolve(KeyId.B, KeyId.None, KeyModifiers.None, false, hasImage: true, hasCapturePair: true)?.Type);
        Assert.Null(router.TryResolve(KeyId.B, KeyId.None, KeyModifiers.None, false, hasImage: true, hasCapturePair: true, isCompareVisible: true));
        Assert.Null(router.TryResolve(KeyId.B, KeyId.None, KeyModifiers.None, false, hasImage: false, hasCapturePair: true));
    }

    [Fact]
    public void OpenFolderAndCustomZoom_EmptyOrUnparseable_AreDisabled()
    {
        var settings = new AppSettings();
        settings.Shortcuts.OpenFolder = "";
        settings.Shortcuts.CustomZoom = "NotAKey";
        var router = new ShortcutRouter(settings);

        Assert.Null(router.TryResolve(KeyId.O, KeyId.None, KeyModifiers.Control, false, hasImage: true));
        Assert.Null(router.TryResolve(KeyId.D3, KeyId.None, KeyModifiers.None, false, hasImage: true));
    }

    [Fact]
    public void OpenFolderAndCustomZoom_DefaultsDoNotCollideWithOtherDefaults()
    {
        // Q-R42/Q-R43: OpenFolder's default is Ctrl+O and CustomZoom's is D3 -- distinct from every other
        // default shortcut (in particular Undo=Z and ClickZoom=D2), so SettingsValidator sees no duplicate.
        var validator = new PhotoReview.Core.Settings.SettingsValidator(new PhotoReview.App.Services.WpfKeyNameValidator());
        var error = validator.ValidateShortcuts(new AppSettings());
        Assert.Null(error);
    }

    // RV-A03 / RV-D3 = A: file-changing commands fire only with NO modifier.
    public static TheoryData<KeyId> FileChangingKeys() => new()
    {
        KeyId.Delete, KeyId.Enter, KeyId.F3, KeyId.F4, KeyId.F5
    };

    [Theory]
    [MemberData(nameof(FileChangingKeys))]
    public void RecycleAndActions_WithAnyModifier_DoNotResolve(KeyId key)
    {
        var router = DefaultRouter();
        Assert.NotNull(router.TryResolve(key, KeyId.None, KeyModifiers.None, false, hasImage: true));
        foreach (var mods in new[] { KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Control | KeyModifiers.Shift })
        {
            Assert.Null(router.TryResolve(key, KeyId.None, mods, false, hasImage: true));
        }
    }

    [Fact]
    public void NavigationZoomAndToggles_StillAcceptCtrl()
    {
        var router = DefaultRouter();
        Assert.Equal(ReviewCommandType.Next, router.TryResolve(KeyId.Right, KeyId.None, KeyModifiers.Control, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Previous, router.TryResolve(KeyId.Left, KeyId.None, KeyModifiers.Control, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomIn, router.TryResolve(KeyId.Add, KeyId.None, KeyModifiers.Control, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomOut, router.TryResolve(KeyId.Subtract, KeyId.None, KeyModifiers.Control, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ToggleFit, router.TryResolve(KeyId.F, KeyId.None, KeyModifiers.Control, false, true)?.Type);
    }

    [Fact]
    public void MoveToFolder_ShiftForcesPicker_CtrlDoesNotResolve()
    {
        var router = DefaultRouter();
        var shifted = router.TryResolve(KeyId.M, KeyId.None, KeyModifiers.Shift, false, hasImage: true);
        Assert.Equal(ReviewCommandType.MoveToFolder, shifted?.Type);
        Assert.True(shifted?.ForcePicker);
        Assert.Null(router.TryResolve(KeyId.M, KeyId.None, KeyModifiers.Control, false, hasImage: true));
    }

    [Fact]
    public void AltF4_ReportedAsKeySystem_DoesNotTriggerTheF4Action()
    {
        Assert.Null(DefaultRouter().TryResolve(KeyId.System, KeyId.F4, KeyModifiers.Alt, false, hasImage: true));
    }

    [Fact]
    public void SameKeyOnActionAndRecycle_ActionWins()
    {
        var settings = new AppSettings();
        settings.Shortcuts.SendToRecycleBin = "D5";
        settings.Actions = [new ReviewAction { Name = "A", Shortcut = "D5", Operation = FileOperationType.Move, Destination = "X" }];
        var cmd = new ShortcutRouter(settings).TryResolve(KeyId.D5, KeyId.None, KeyModifiers.None, false, hasImage: true);
        Assert.Equal(ReviewCommandType.RunAction, cmd?.Type);
        Assert.Equal(0, cmd?.ActionIndex);
    }

    [Theory]
    [InlineData(KeyId.Delete)]
    [InlineData(KeyId.Space)]
    [InlineData(KeyId.F)]
    [InlineData(KeyId.Add)]
    [InlineData(KeyId.Subtract)]
    [InlineData(KeyId.W)]
    [InlineData(KeyId.H)]
    [InlineData(KeyId.Right)]
    [InlineData(KeyId.Left)]
    [InlineData(KeyId.Enter)]
    [InlineData(KeyId.F5)]
    [InlineData(KeyId.M)]
    [InlineData(KeyId.Y)]
    [InlineData(KeyId.D3)]
    public void ImageGroupCommands_WithoutImage_ReturnNull(KeyId key)
    {
        Assert.Null(DefaultRouter().TryResolve(key, KeyId.None, KeyModifiers.None, false, hasImage: false));
    }

    [Fact]
    public void Compare_NothingToToggle_DoesNotFallThroughToALowerPriorityCommandOnTheSameKey()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Compare = "S";
        settings.Shortcuts.Skip = "S";
        var router = new ShortcutRouter(settings);
        Assert.Null(router.TryResolve(KeyId.S, KeyId.None, KeyModifiers.None, false, hasImage: true, hasComparePair: false, isCompareVisible: false));
        Assert.Equal(ReviewCommandType.ToggleCompare, router.TryResolve(KeyId.S, KeyId.None, KeyModifiers.None, false, hasImage: true, hasComparePair: true)?.Type);
    }
}
