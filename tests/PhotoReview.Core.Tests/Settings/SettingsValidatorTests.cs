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
                    "F4", "F5", "F6", "T", "D1", "D2", "D3", "D4", "End", "I"
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

    [Theory(DisplayName = "Invalid or blank shortcut key reports property error")]
    [InlineData("Next", "InvalidKey")]
    [InlineData("Previous", "   ")]
    public void InvalidShortcutKey_ReturnsError(string property, string key)
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        if (property == "Next") settings.Shortcuts.Next = key;
        else settings.Shortcuts.Previous = key;

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal($"Phím tắt {property} không hợp lệ.", error);
    }

    [Theory(DisplayName = "Action with missing name or invalid key name reports action error")]
    [InlineData("", "Enter")]
    [InlineData("TestAction", "UnknownKey123")]
    public void InvalidAction_ReturnsError(string name, string shortcut)
    {
        var validator = new SettingsValidator(new FakeKeyNameValidator());
        var settings = new AppSettings();
        settings.Actions = new List<ReviewAction>
        {
            new ReviewAction { Name = name, Shortcut = shortcut }
        };

        var error = validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Equal("Mỗi hành động cần có tên và phím tắt hợp lệ.", error);
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
        Assert.Contains("Phím Right bị dùng trùng bởi:", error);
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
        Assert.Contains("Phím Right bị dùng trùng bởi:", error);
        Assert.Contains("Next", error);
        Assert.Contains("Hành động: DuplicateAction", error);
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
