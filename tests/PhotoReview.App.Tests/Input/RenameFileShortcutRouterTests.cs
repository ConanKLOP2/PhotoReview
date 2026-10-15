using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>FEAT-RENAME (PR3): the "Rename file" shortcut (F2 by default; a file-changing command: no modifier, needs a photo).</summary>
public sealed class RenameFileShortcutRouterTests
{
    private static ReviewCommand? Resolve(ShortcutRouter router, KeyId key, KeyModifiers modifiers = KeyModifiers.None, bool hasImage = true,
        bool compareVisible = false) =>
        router.TryResolve(key, KeyId.None, modifiers, isFullscreen: false, hasImage, hasComparePair: true, isCompareVisible: compareVisible);

    [Fact]
    public void F2_ByDefault_ResolvesToRenameFile()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(ReviewCommandType.RenameFile, Resolve(router, KeyId.F2)?.Type);
        Assert.Equal(ReviewCommand.RenameFile, Resolve(router, KeyId.F2));
    }

    [Fact]
    public void WithoutAPhoto_NothingResolves()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Null(Resolve(router, KeyId.F2, hasImage: false));
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Alt)]
    public void WithAModifier_DoesNotRename(KeyModifiers modifiers)
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Null(Resolve(router, KeyId.F2, modifiers));
    }

    [Fact]
    public void ARebind_FollowsTheSettings_AndClearingTheKeyDisablesIt()
    {
        var settings = new AppSettings();
        settings.Shortcuts.RenameFile = "F6";
        var router = new ShortcutRouter(settings);
        Assert.Null(Resolve(router, KeyId.F2));
        Assert.Equal(ReviewCommandType.RenameFile, Resolve(router, KeyId.F6)?.Type);

        settings.Shortcuts.RenameFile = string.Empty;
        router.Rebuild(settings);
        Assert.Null(Resolve(router, KeyId.F6));
        Assert.Null(Resolve(router, KeyId.F2));
    }

    [Fact]
    public void AnActionBoundToTheSameKey_TakesPrecedence()
    {
        var settings = new AppSettings();
        settings.Actions[0].Shortcut = "F2"; // the normalizer would disable the rename key; the router itself must still be deterministic
        var router = new ShortcutRouter(settings);

        Assert.Equal(ReviewCommandType.RunAction, Resolve(router, KeyId.F2)?.Type);
    }

    [Fact]
    public void ItDoesNotDisturbTheOtherKeys()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(ReviewCommandType.Next, Resolve(router, KeyId.Right)?.Type);
        Assert.Equal(ReviewCommandType.Recycle, Resolve(router, KeyId.Delete)?.Type);
        Assert.Equal(ReviewCommandType.RunAction, Resolve(router, KeyId.F3)?.Type); // the default Action keys are F3..F5
    }

    [Fact]
    public void HoldingTheKey_DoesNotOpenTheBoxRepeatedly() =>
        Assert.True(ReviewCommandType.RenameFile.IgnoresAutoRepeat());
}
