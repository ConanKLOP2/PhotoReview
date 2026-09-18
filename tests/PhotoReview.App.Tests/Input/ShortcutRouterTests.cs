using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.Input;

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

    [Fact]
    public void Fullscreen_NormalKey_ResolvesToFullscreen()
    {
        var cmd = _router.TryResolve(Key.F11, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: false);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandType.Fullscreen, cmd.Value.Type);
    }

    [Fact]
    public void Fullscreen_SystemKey_ResolvesToFullscreen()
    {
        // Khi bấm Alt+F11, WPF Key là Key.System và SystemKey là Key.F11
        var cmd = _router.TryResolve(Key.System, Key.F11, ModifierKeys.Alt, isFullscreen: false, hasImage: false);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandType.Fullscreen, cmd.Value.Type);
    }

    [Fact]
    public void Escape_WhenFullscreen_ResolvesToExitFullscreen()
    {
        var cmd = _router.TryResolve(Key.Escape, Key.None, ModifierKeys.None, isFullscreen: true, hasImage: true);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandType.ExitFullscreen, cmd.Value.Type);
    }

    [Fact]
    public void Escape_WhenNotFullscreen_ResolvesToClose()
    {
        var cmd = _router.TryResolve(Key.Escape, Key.None, ModifierKeys.None, isFullscreen: false, hasImage: true);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandType.Close, cmd.Value.Type);
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

    [Fact]
    public void Compare_WhenInvisibleAndNoPair_ReturnsNull()
    {
        var cmd = _router.TryResolve(Key.C, Key.None, ModifierKeys.None, false, hasImage: true, hasComparePair: false, isCompareVisible: false);
        Assert.Null(cmd);
    }

    [Fact]
    public void Compare_WhenInvisibleAndHasPair_ResolvesToToggleCompare()
    {
        var cmd = _router.TryResolve(Key.C, Key.None, ModifierKeys.None, false, hasImage: true, hasComparePair: true, isCompareVisible: false);
        Assert.Equal(ReviewCommandType.ToggleCompare, cmd?.Type);
    }

    [Fact]
    public void Compare_WhenVisible_ResolvesToToggleCompareEvenWithoutPair()
    {
        var cmd = _router.TryResolve(Key.C, Key.None, ModifierKeys.None, false, hasImage: true, hasComparePair: false, isCompareVisible: true);
        Assert.Equal(ReviewCommandType.ToggleCompare, cmd?.Type);
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
}