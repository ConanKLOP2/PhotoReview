using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
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

    // Alt+F11 reaches WPF as Key.System with SystemKey = F11, so both spellings must resolve.
    [Theory]
    [InlineData(Key.F11, Key.None, ModifierKeys.None)]
    [InlineData(Key.System, Key.F11, ModifierKeys.Alt)]
    public void Fullscreen_ResolvesToFullscreen(Key key, Key systemKey, ModifierKeys modifiers)
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
        var cmd = _router.TryResolve(Key.Escape, Key.None, ModifierKeys.None, isFullscreen: isFullscreen, hasImage: true);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
    }

    [Fact]
    public void FolderNavigation_ResolvesCorrectly()
    {
        var nextFolder = _router.TryResolve(Key.PageDown, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false);
        var prevFolder = _router.TryResolve(Key.PageUp, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false);

        Assert.Equal(ReviewCommandType.NextFolder, nextFolder?.Type);
        Assert.Equal(ReviewCommandType.PreviousFolder, prevFolder?.Type);
    }

    [Fact]
    public void FirstImage_WhenHasImage_ResolvesToFirstImage()
    {
        var cmd = _router.TryResolve(Key.Home, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.FirstImage, cmd?.Type);
    }

    [Fact]
    public void FirstImage_WhenNoImage_ReturnsNull()
    {
        var cmd = _router.TryResolve(Key.Home, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false);
        Assert.Null(cmd);
    }

    [Fact]
    public void Undo_RequiresControlModifier()
    {
        var withCtrl = _router.TryResolve(Key.Z, Key.None, ModifierKeys.Control, isFullscreen: false, hasImage: false);
        var withoutCtrl = _router.TryResolve(Key.Z, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false);

        Assert.Equal(ReviewCommandType.Undo, withCtrl?.Type);
        Assert.Null(withoutCtrl);
    }

    [Fact]
    public void ImageCommands_WhenNoImage_ReturnNull()
    {
        Assert.Null(_router.TryResolve(Key.Right, Key.None, ModifierKeys.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(Key.Left, Key.None, ModifierKeys.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(Key.Delete, Key.None, ModifierKeys.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(Key.S, Key.None, ModifierKeys.None, false, hasImage: false));
        Assert.Null(_router.TryResolve(Key.D1, Key.None, ModifierKeys.None, false, hasImage: false));
    }

    [Theory]
    [InlineData(false, false, false)] // not visible, no pair: nothing to compare
    [InlineData(true, false, true)]   // not visible, has pair: open compare
    [InlineData(false, true, true)]   // visible: always closable, even without a pair
    public void Compare_ResolvesToToggleOnlyWhenThereIsSomethingToToggle(bool hasComparePair, bool isCompareVisible, bool resolves)
    {
        var cmd = _router.TryResolve(Key.C, Key.None, ModifierKeys.None, false, hasImage: true, hasComparePair: hasComparePair, isCompareVisible: isCompareVisible);
        Assert.Equal(resolves ? ReviewCommandType.ToggleCompare : null, cmd?.Type);
    }

    [Fact]
    public void CustomActions_ResolveToCorrectIndex()
    {
        var action0 = _router.TryResolve(Key.D1, Key.None, ModifierKeys.None, false, hasImage: true);
        var action1 = _router.TryResolve(Key.D2, Key.None, ModifierKeys.None, false, hasImage: true);

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
        Assert.Equal(ReviewCommandType.Next, _router.TryResolve(Key.Right, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Previous, _router.TryResolve(Key.Left, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Recycle, _router.TryResolve(Key.Delete, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.Skip, _router.TryResolve(Key.S, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ToggleFit, _router.TryResolve(Key.F, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomIn, _router.TryResolve(Key.OemPlus, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomOut, _router.TryResolve(Key.OemMinus, Key.None, ModifierKeys.None, false, true)?.Type);
    }

    // --- Optional shortcuts: LastImage (End), ZoomActualSize (D1), ToggleInfoOverlay (I); empty = disabled ---

    private static ShortcutRouter DefaultRouter() => new(new AppSettings());

    [Fact]
    public void LastImage_WhenHasImage_ResolvesToLastImage()
    {
        var cmd = DefaultRouter().TryResolve(Key.End, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.LastImage, cmd?.Type);
    }

    [Fact]
    public void LastImage_WhenNoImage_ReturnsNull_LikeFirstImage()
    {
        var router = DefaultRouter();
        Assert.Null(router.TryResolve(Key.End, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false));
        Assert.Null(router.TryResolve(Key.Home, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false));
    }

    [Fact]
    public void ZoomActualSize_WhenHasImage_ResolvesToZoomActualSize()
    {
        var cmd = DefaultRouter().TryResolve(Key.D1, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.ZoomActualSize, cmd?.Type);
    }

    [Fact]
    public void ZoomActualSize_WhenNoImage_ReturnsNull()
    {
        Assert.Null(DefaultRouter().TryResolve(Key.D1, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false));
    }

    [Fact]
    public void ClickZoom_WhenHasImage_ResolvesToClickZoom()
    {
        var cmd = DefaultRouter().TryResolve(Key.D2, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: true);
        Assert.Equal(ReviewCommandType.ClickZoom, cmd?.Type);
    }

    [Fact]
    public void ClickZoom_WhenNoImage_ReturnsNull()
    {
        Assert.Null(DefaultRouter().TryResolve(Key.D2, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleInfoOverlay_ResolvesWithOrWithoutImage(bool hasImage)
    {
        var cmd = DefaultRouter().TryResolve(Key.I, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: hasImage);
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

        Assert.Null(router.TryResolve(Key.End, Key.None, ModifierKeys.None, false, hasImage: true));
        Assert.Null(router.TryResolve(Key.D1, Key.None, ModifierKeys.None, false, hasImage: true));
        Assert.Null(router.TryResolve(Key.I, Key.None, ModifierKeys.None, false, hasImage: true));
        Assert.Null(router.TryResolve(Key.D2, Key.None, ModifierKeys.None, false, hasImage: true));
    }

    [Fact]
    public void OptionalShortcuts_Rebuild_PicksUpCustomKeys()
    {
        var settings = new AppSettings();
        var router = new ShortcutRouter(settings);
        settings.Shortcuts.LastImage = "F7";
        settings.Shortcuts.ZoomActualSize = "D0";
        settings.Shortcuts.ToggleInfoOverlay = "O";
        settings.Shortcuts.ClickZoom = "D3";
        router.Rebuild(settings);

        Assert.Equal(ReviewCommandType.LastImage, router.TryResolve(Key.F7, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ZoomActualSize, router.TryResolve(Key.D0, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ToggleInfoOverlay, router.TryResolve(Key.O, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Equal(ReviewCommandType.ClickZoom, router.TryResolve(Key.D3, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Null(router.TryResolve(Key.End, Key.None, ModifierKeys.None, false, true));
        Assert.Null(router.TryResolve(Key.D2, Key.None, ModifierKeys.None, false, true)); // old ClickZoom default no longer bound
    }

    [Fact]
    public void ZoomActualSize_ActionOnTheSameKey_KeepsPriority()
    {
        // The fixture binds an action to D1 (an old config): actions are resolved before the zoom group.
        var cmd = _router.TryResolve(Key.D1, Key.None, ModifierKeys.None, false, hasImage: true);
        Assert.Equal(ReviewCommandType.RunAction, cmd?.Type);
    }

    [Fact]
    public void ClickZoom_ActionOnTheSameKey_KeepsPriority()
    {
        // The fixture binds an action to D2 (ClickZoom's default): actions are resolved before the zoom group.
        var cmd = _router.TryResolve(Key.D2, Key.None, ModifierKeys.None, false, hasImage: true);
        Assert.Equal(ReviewCommandType.RunAction, cmd?.Type);
    }
}
