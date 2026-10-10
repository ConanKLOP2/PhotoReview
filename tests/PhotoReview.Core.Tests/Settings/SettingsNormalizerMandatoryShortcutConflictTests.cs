using System.Reflection;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Audit C-05: <see cref="SettingsNormalizer.DisableConflictingOptionalShortcuts"/> must turn off an optional shortcut whose key a
/// MANDATORY shortcut already uses, for every mandatory/optional pair (not only optional versus a user action). Pairs are derived
/// by reflection from <see cref="ShortcutMappings"/>, so a new mandatory property is covered without editing this test.
/// </summary>
public sealed class SettingsNormalizerMandatoryShortcutConflictTests
{
    private sealed class AnyKey : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => !string.IsNullOrWhiteSpace(keyName);
    }

    private static readonly PropertyInfo[] Mandatory = typeof(ShortcutMappings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanWrite
            && !ShortcutMappings.IsOptional(p.Name) && p.Name != nameof(ShortcutMappings.MoveToFolder2))
        .ToArray();

    // An optional with an empty default (ToggleCaptureMember) is exercised with a key no default uses.
    private static string KeyOf(string optionalName) =>
        (string?)typeof(ShortcutMappings).GetProperty(optionalName)!.GetValue(ShortcutMappings.Default()) is { Length: > 0 } key ? key : "J";

    public static TheoryData<string, string> Pairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var mandatory in Mandatory)
            foreach (var optional in ShortcutMappings.OptionalNames)
                data.Add(mandatory.Name, optional);
        return data;
    }

    [Fact]
    public void MandatoryShortcutSet_IsTheThirteenValidatedBindings()
    {
        Assert.Equal(13, Mandatory.Length); // guards the reflection filter: a silent shrink would hollow the theory out
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void OptionalShortcut_SharingAMandatoryKey_IsDisabledAndSettingsStayValid(string mandatoryName, string optionalName)
    {
        var settings = new AppSettings { Shortcuts = new ShortcutMappings(), Actions = [] };
        var key = KeyOf(optionalName);
        typeof(ShortcutMappings).GetProperty(mandatoryName)!.SetValue(settings.Shortcuts, key);
        typeof(ShortcutMappings).GetProperty(optionalName)!.SetValue(settings.Shortcuts, key);

        var disabled = SettingsNormalizer.DisableConflictingOptionalShortcuts(settings);

        Assert.Contains(optionalName, disabled);
        Assert.Equal("", typeof(ShortcutMappings).GetProperty(optionalName)!.GetValue(settings.Shortcuts));
        Assert.Equal(key, typeof(ShortcutMappings).GetProperty(mandatoryName)!.GetValue(settings.Shortcuts)); // the mandatory binding wins
        Assert.Null(new SettingsValidator(new AnyKey()).ValidateShortcuts(settings));
    }
}
