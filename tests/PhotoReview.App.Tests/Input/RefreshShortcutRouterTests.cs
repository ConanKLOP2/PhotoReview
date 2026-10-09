using System.Windows.Input;
using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>Q-TOUCHPAD-REFRESH: the Refresh shortcut (default R) and its auto-repeat rule.</summary>
public sealed class RefreshShortcutRouterTests
{
    [Fact]
    public void Refresh_DefaultKeyR_ResolvesOnlyWithAnImage()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(ReviewCommandType.Refresh, router.TryResolve(Key.R, Key.None, ModifierKeys.None, false, hasImage: true)?.Type);
        Assert.Null(router.TryResolve(Key.R, Key.None, ModifierKeys.None, false, hasImage: false));
    }

    [Fact]
    public void Refresh_F5StillRunsTheDefaultBackupAction()
    {
        var settings = new AppSettings();
        var router = new ShortcutRouter(settings);

        var command = router.TryResolve(Key.F5, Key.None, ModifierKeys.None, false, hasImage: true);

        Assert.Equal(ReviewCommandType.RunAction, command?.Type);
        Assert.Equal("F5", settings.Actions[command!.Value.ActionIndex].Shortcut);
    }

    [Fact]
    public void Refresh_EmptyShortcut_IsDisabled_AndARemappedKeyFollows()
    {
        var settings = new AppSettings { Shortcuts = new ShortcutMappings { Refresh = "" } };
        var router = new ShortcutRouter(settings);
        Assert.Null(router.TryResolve(Key.R, Key.None, ModifierKeys.None, false, true));

        settings.Actions.RemoveAll(a => a.Shortcut == "F5");
        settings.Shortcuts.Refresh = "F5";
        router.Rebuild(settings);

        Assert.Equal(ReviewCommandType.Refresh, router.TryResolve(Key.F5, Key.None, ModifierKeys.None, false, true)?.Type);
        Assert.Null(router.TryResolve(Key.R, Key.None, ModifierKeys.None, false, true));
    }

    [Fact]
    public void Refresh_IgnoresAutoRepeat() => Assert.True(ReviewCommandType.Refresh.IgnoresAutoRepeat());
}
