using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>FEAT-EXTERNAL-EDITORS: the "open in default editor" shortcut (no default key; user-defined) and its auto-repeat rule.</summary>
public sealed class OpenInDefaultEditorShortcutRouterTests
{
    [Fact]
    public void NoDefaultKey_NothingResolves()
    {
        var router = new ShortcutRouter(new AppSettings());

        Assert.Equal(string.Empty, new AppSettings().Shortcuts.OpenInDefaultEditor);
        Assert.Null(router.TryResolve(KeyId.F9, KeyId.None, KeyModifiers.None, false, hasImage: true));
    }

    [Fact]
    public void ABoundKey_ResolvesToTheCommand_AndFollowsARebind()
    {
        var settings = new AppSettings();
        settings.Shortcuts.OpenInDefaultEditor = "F9";
        var router = new ShortcutRouter(settings);

        Assert.Equal(ReviewCommandType.OpenInDefaultEditor, router.TryResolve(KeyId.F9, KeyId.None, KeyModifiers.None, false, true)?.Type);

        settings.Shortcuts.OpenInDefaultEditor = "F10";
        router.Rebuild(settings);
        Assert.Null(router.TryResolve(KeyId.F9, KeyId.None, KeyModifiers.None, false, true));
        Assert.Equal(ReviewCommandType.OpenInDefaultEditor, router.TryResolve(KeyId.F10, KeyId.None, KeyModifiers.None, false, true)?.Type);
    }

    [Fact]
    public void HoldingTheKey_DoesNotLaunchTheEditorRepeatedly() =>
        Assert.True(ReviewCommandType.OpenInDefaultEditor.IgnoresAutoRepeat());
}
