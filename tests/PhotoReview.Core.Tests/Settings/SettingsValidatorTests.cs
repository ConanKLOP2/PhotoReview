using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

[Trait("Category", "HotPath")]
public sealed class SettingsValidatorTests
{
    private sealed class FakeKeyNameValidator : IKeyNameValidator
    {
        private readonly HashSet<string> _validKeys;

        public FakeKeyNameValidator(IEnumerable<string>? validKeys = null)
        {
            _validKeys = validKeys is not null
                ? new HashSet<string>(validKeys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Right", "Left", "Enter", "Delete", "C", "PageDown", "PageUp",
                    "Home", "Add", "Subtract", "F", "Space", "Z", "F11", "F3",
                    "F4", "F5", "F6", "T", "D1", "D2", "D3", "D4"
                };
        }

        public bool IsValidKeyName(string keyName) =>
            !string.IsNullOrWhiteSpace(keyName) && _validKeys.Contains(keyName.Trim());
    }

    [Fact(DisplayName = "Default settings are valid")]
    public void DefaultSettings_AreValid()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();

        var error = validator.ValidateShortcuts(settings);

        Assert.Null(error);
    }

    [Fact(DisplayName = "Invalid shortcut key reports property error")]
    public void InvalidShortcutKey_ReturnsError()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Shortcuts.Next = "InvalidKey";

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal("Shortcut Next khÃ´ng há»£p lá»‡.", error);
    }

    [Fact(DisplayName = "Whitespace or empty shortcut key reports property error")]
    public void EmptyShortcutKey_ReturnsError()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Shortcuts.Previous = "   ";

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal("Shortcut Previous khÃ´ng há»£p lá»‡.", error);
    }

    [Fact(DisplayName = "Action with missing name or invalid shortcut reports action error")]
    public void InvalidAction_ReturnsError()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Actions = new List<ReviewAction>
        {
            new ReviewAction { Name = "", Shortcut = "Enter" }
        };

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal("Action pháº£i cÃ³ tÃªn vÃ  phÃ­m táº¯t há»£p lá»‡.", error);
    }

    [Fact(DisplayName = "Action with invalid key name reports action error")]
    public void ActionWithInvalidKey_ReturnsError()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Actions = new List<ReviewAction>
        {
            new ReviewAction { Name = "TestAction", Shortcut = "UnknownKey123" }
        };

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal("Action pháº£i cÃ³ tÃªn vÃ  phÃ­m táº¯t há»£p lá»‡.", error);
    }

    [Fact(DisplayName = "Duplicate shortcut keys report conflict message")]
    public void DuplicateKeys_ReturnsConflictMessage()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        // Both Next and Previous set to Right
        settings.Shortcuts.Previous = "Right";

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Contains("PhÃ­m Right bá»‹ dÃ¹ng trÃ¹ng bá»Ÿi:", error);
        Assert.Contains("Next", error);
        Assert.Contains("Previous", error);
    }

    [Fact(DisplayName = "Conflict between action and built-in shortcut reports conflict message")]
    public void ActionAndShortcutConflict_ReturnsConflictMessage()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Actions = new List<ReviewAction>
        {
            new ReviewAction { Name = "DuplicateAction", Shortcut = "Right" }
        };

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Contains("PhÃ­m Right bá»‹ dÃ¹ng trÃ¹ng bá»Ÿi:", error);
        Assert.Contains("Next", error);
        Assert.Contains("Action: DuplicateAction", error);
    }

    [Fact(DisplayName = "Throws ArgumentNullException when settings is null")]
    public void NullSettings_ThrowsArgumentNullException()
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());

        Assert.Throws<ArgumentNullException>(() => validator.ValidateShortcuts(null!));
    }

    [Fact(DisplayName = "Throws ArgumentNullException when keyValidator is null")]
    public void NullValidator_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsValidator(null!));
    }
}

