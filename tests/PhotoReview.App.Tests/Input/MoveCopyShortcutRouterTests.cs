using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>"Move to… / Copy to…" shortcuts (defaults M / Y) in the router.</summary>
[Trait("Category", "HotPath")]
public sealed class MoveCopyShortcutRouterTests
{
    private static ReviewCommand? Resolve(ShortcutRouter router, KeyId key, KeyModifiers modifiers = KeyModifiers.None, bool hasImage = true) =>
        router.TryResolve(key, KeyId.None, modifiers, isFullscreen: false, hasImage: hasImage);

    [Theory]
    [InlineData(KeyId.M, ReviewCommandType.MoveToFolder)]
    [InlineData(KeyId.Y, ReviewCommandType.CopyToFolder)]
    public void DefaultKeys_ResolveWithoutForcingThePicker(KeyId key, ReviewCommandType expected)
    {
        var cmd = Resolve(new ShortcutRouter(new AppSettings()), key);

        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
        Assert.False(cmd.Value.ForcePicker);
    }

    [Theory]
    [InlineData(KeyId.M, ReviewCommandType.MoveToFolder)]
    [InlineData(KeyId.Y, ReviewCommandType.CopyToFolder)]
    public void ShiftKey_ForcesThePicker(KeyId key, ReviewCommandType expected)
    {
        var cmd = Resolve(new ShortcutRouter(new AppSettings()), key, KeyModifiers.Shift);

        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Value.Type);
        Assert.True(cmd.Value.ForcePicker);
    }

    [Theory]
    [InlineData(KeyId.M)]
    [InlineData(KeyId.Y)]
    public void CtrlKey_IsNotMoveOrCopy(KeyId key) =>
        Assert.Null(Resolve(new ShortcutRouter(new AppSettings()), key, KeyModifiers.Control));

    [Theory]
    [InlineData(KeyId.M)]
    [InlineData(KeyId.Y)]
    public void WithoutImage_Nothing(KeyId key) =>
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

        Assert.Null(Resolve(router, KeyId.M));
        Assert.Null(Resolve(router, KeyId.Y));
        // "Empty" must not become KeyId.None or any other key.
        Assert.Null(Resolve(router, KeyId.None));
    }

    [Fact]
    public void RemappedKeys_FollowSettingsAndRebuild()
    {
        var settings = new AppSettings();
        settings.Shortcuts.MoveToFolder = "F7";
        settings.Shortcuts.CopyToFolder = "F8";
        var router = new ShortcutRouter(settings);

        Assert.Equal(ReviewCommandType.MoveToFolder, Resolve(router, KeyId.F7)?.Type);
        Assert.Equal(ReviewCommandType.CopyToFolder, Resolve(router, KeyId.F8)?.Type);
        Assert.Null(Resolve(router, KeyId.M));

        settings.Shortcuts.MoveToFolder = "";
        router.Rebuild(settings);
        Assert.Null(Resolve(router, KeyId.F7));
        Assert.Equal(ReviewCommandType.CopyToFolder, Resolve(router, KeyId.F8)?.Type);
    }

    [Fact]
    public void DefaultBindings_KeepResolvingToTheirOwnCommands()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(ReviewCommandType.Next, Resolve(router, KeyId.Right)?.Type);
        Assert.Equal(ReviewCommandType.RunAction, Resolve(router, KeyId.Enter)?.Type);
        Assert.Equal(ReviewCommandType.Recycle, Resolve(router, KeyId.Delete)?.Type);
        Assert.Equal(ReviewCommandType.Undo, Resolve(router, KeyId.Z, KeyModifiers.Control)?.Type);
    }
}
