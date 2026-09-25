using System.Windows.Input;
using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>"Move to… / Copy to…" shortcuts (defaults M / Y) in the router.</summary>
[Trait("Category", "HotPath")]
public sealed class MoveCopyShortcutRouterTests
{
    private static ReviewCommand? Resolve(ShortcutRouter router, Key key, ModifierKeys modifiers = ModifierKeys.None, bool hasImage = true) =>
        router.TryResolve(key, Key.None, modifiers, isFullscreen: false, hasImage: hasImage);

    [Theory]
    [InlineData(Key.M, ReviewCommandType.MoveToFolder)]
    [InlineData(Key.Y, ReviewCommandType.CopyToFolder)]
    public void DefaultKeys_ResolveWithoutForcingThePicker(Key key, ReviewCommandType expected)
    {
        var cmd = Resolve(new ShortcutRouter(new AppSettings()), key);

        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
        Assert.False(cmd.Value.ForcePicker);
    }

    [Theory]
    [InlineData(Key.M, ReviewCommandType.MoveToFolder)]
    [InlineData(Key.Y, ReviewCommandType.CopyToFolder)]
    public void ShiftKey_ForcesThePicker(Key key, ReviewCommandType expected)
    {
        var cmd = Resolve(new ShortcutRouter(new AppSettings()), key, ModifierKeys.Shift);

        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
        Assert.True(cmd.Value.ForcePicker);
    }

    [Theory]
    [InlineData(Key.M)]
    [InlineData(Key.Y)]
    public void CtrlKey_IsNotMoveOrCopy(Key key) =>
        Assert.Null(Resolve(new ShortcutRouter(new AppSettings()), key, ModifierKeys.Control));

    [Theory]
    [InlineData(Key.M)]
    [InlineData(Key.Y)]
    public void WithoutImage_Nothing(Key key) =>
        Assert.Null(Resolve(new ShortcutRouter(new AppSettings()), key, hasImage: false));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyShortcut_DisablesTheCommand(string value)
    {
        var settings = new AppSettings();
        settings.Shortcuts.MoveToFolder = value;
        settings.Shortcuts.CopyToFolder = value;
        var router = new ShortcutRouter(settings);

        Assert.Null(Resolve(router, Key.M));
        Assert.Null(Resolve(router, Key.Y));
        // "Empty" must not become Key.None or any other key.
        Assert.Null(Resolve(router, Key.None));
    }

    [Fact]
    public void RemappedKeys_FollowSettingsAndRebuild()
    {
        var settings = new AppSettings();
        settings.Shortcuts.MoveToFolder = "F7";
        settings.Shortcuts.CopyToFolder = "F8";
        var router = new ShortcutRouter(settings);

        Assert.Equal(ReviewCommandType.MoveToFolder, Resolve(router, Key.F7)?.Type);
        Assert.Equal(ReviewCommandType.CopyToFolder, Resolve(router, Key.F8)?.Type);
        Assert.Null(Resolve(router, Key.M));

        settings.Shortcuts.MoveToFolder = "";
        router.Rebuild(settings);
        Assert.Null(Resolve(router, Key.F7));
        Assert.Equal(ReviewCommandType.CopyToFolder, Resolve(router, Key.F8)?.Type);
    }

    [Fact]
    public void DefaultBindings_KeepResolvingToTheirOwnCommands()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(ReviewCommandType.Next, Resolve(router, Key.Right)?.Type);
        Assert.Equal(ReviewCommandType.RunAction, Resolve(router, Key.Enter)?.Type);
        Assert.Equal(ReviewCommandType.Recycle, Resolve(router, Key.Delete)?.Type);
        Assert.Equal(ReviewCommandType.Undo, Resolve(router, Key.Z, ModifierKeys.Control)?.Type);
    }
}
