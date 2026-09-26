using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Q-R25: Enter/Return, PageUp/Prior, PageDown/Next ... are ONE shortcut everywhere.</summary>
public sealed class ShortcutKeyCanonicalTests
{
    private sealed class AnyKeys : IKeyNameValidator { public bool IsValidKeyName(string keyName) => keyName.Length > 0; }

    [Theory(DisplayName = "Every alias maps to its canonical name, in any case and with surrounding whitespace")]
    [InlineData("Return", "Enter")]
    [InlineData("Prior", "PageUp")]
    [InlineData("Next", "PageDown")]
    [InlineData("Capital", "CapsLock")]
    [InlineData("Snapshot", "PrintScreen")]
    [InlineData("Oem1", "OemSemicolon")]
    [InlineData("Oem102", "OemBackslash")]
    public void Canonicalize_Alias_ReturnsCanonical(string alias, string canonical)
    {
        Assert.Equal(canonical, ShortcutKeyCanonical.Canonicalize(alias));
        Assert.Equal(canonical, ShortcutKeyCanonical.Canonicalize(alias.ToUpperInvariant()));
        Assert.Equal(canonical, ShortcutKeyCanonical.Canonicalize("  " + alias.ToLowerInvariant() + " "));
        Assert.Equal(canonical, ShortcutKeyCanonical.Canonicalize(canonical.ToLowerInvariant()));
    }

    [Fact(DisplayName = "All table pairs canonicalise and compare as the same key")]
    public void AliasPairs_AllSameKey()
    {
        foreach (var (alias, canonical) in ShortcutKeyCanonical.AliasPairs)
        {
            Assert.Equal(canonical, ShortcutKeyCanonical.Canonicalize(alias));
            Assert.True(ShortcutKeyCanonical.SameKey(alias, canonical));
        }
    }

    [Fact(DisplayName = "Different keys that only look alike (Add/OemPlus) and blanks are never the same key; unknown names stay as they are")]
    public void Canonicalize_NonAliases_Unchanged()
    {
        Assert.False(ShortcutKeyCanonical.SameKey("Add", "OemPlus"));
        Assert.False(ShortcutKeyCanonical.SameKey("", ""));
        Assert.False(ShortcutKeyCanonical.SameKey(null, " "));
        Assert.Equal("", ShortcutKeyCanonical.Canonicalize(null));
        Assert.Equal("Bogus", ShortcutKeyCanonical.Canonicalize(" Bogus "));
        Assert.Equal("f11", ShortcutKeyCanonical.Canonicalize("f11"));
    }

    [Fact(DisplayName = "Canonicalize is idempotent for table names and random strings")]
    public void Canonicalize_IsIdempotent()
    {
        const string alphabet = "aA1 ,+-ReturnPriorNext";
        var rng = new Random(25);
        var inputs = ShortcutKeyCanonical.AliasPairs.SelectMany(p => new[] { p.Alias, p.Canonical }).ToList();
        for (var i = 0; i < 500; i++)
            inputs.Add(new string(Enumerable.Range(0, rng.Next(0, 12)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray()));
        foreach (var input in inputs)
        {
            var once = ShortcutKeyCanonical.Canonicalize(input);
            Assert.Equal(once, ShortcutKeyCanonical.Canonicalize(once));
            Assert.Equal(once, ShortcutKeyCanonical.Canonicalize(" " + once + " "));
        }
    }

    [Fact(DisplayName = "An action shortcut Return conflicts with a shortcut Enter")]
    public void Validator_ReturnAction_ConflictsWithEnter()
    {
        var settings = new AppSettings { Actions = [new ReviewAction { Name = "A", Shortcut = "Return", Destination = "x" }] };
        settings.Shortcuts.Skip = "Enter";
        var error = new SettingsValidator(new AnyKeys()).ValidateShortcuts(settings);
        Assert.NotNull(error);
        Assert.Contains("Enter", error, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Action aliases Prior/Next conflict with the default PageUp/PageDown folder shortcuts")]
    [InlineData("Prior")]
    [InlineData("prior")]
    [InlineData("Next")]
    public void Validator_PagingAliasAction_Conflicts(string actionKey)
    {
        var settings = new AppSettings { Actions = [new ReviewAction { Name = "A", Shortcut = actionKey, Destination = "x" }] };
        Assert.NotNull(new SettingsValidator(new AnyKeys()).ValidateShortcuts(settings));
    }

    [Fact(DisplayName = "An optional shortcut aliasing an action key is disabled on load")]
    public void DisableConflictingOptional_AliasOfActionKey_Disabled()
    {
        var settings = new AppSettings { Actions = [new ReviewAction { Name = "A", Shortcut = "Return", Destination = "x" }] };
        settings.Shortcuts.LastImage = "Enter";
        var disabled = SettingsNormalizer.DisableConflictingOptionalShortcuts(settings);
        Assert.Contains(nameof(ShortcutMappings.LastImage), disabled);
        Assert.Equal("", settings.Shortcuts.LastImage);
    }

    [Fact(DisplayName = "An old config with Return/Prior loads canonical in memory and is saved canonical")]
    public void OldConfig_LoadsAndSavesCanonical()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        fs.WriteAllTextAtomic(paths.ConfigFile,
            """{"Shortcuts":{"PreviousFolder":"Prior","NextFolder":" next "},"Actions":[{"Name":"A","Shortcut":"Return","Operation":0,"Destination":"x"}]}""", durable: false);
        var store = new SettingsStore(paths, fs, NullLog.Instance);

        var loaded = store.Load();

        Assert.Equal("PageUp", loaded.Shortcuts.PreviousFolder);
        Assert.Equal("PageDown", loaded.Shortcuts.NextFolder);
        Assert.Equal("Enter", loaded.Actions[0].Shortcut);
        loaded.Shortcuts.PreviousFolder = "Prior"; // a caller assigning an alias at runtime
        loaded.Actions[0].Shortcut = "RETURN";
        store.Save(loaded);
        var json = fs.ReadAllText(paths.ConfigFile);
        Assert.DoesNotContain("Prior", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Return", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"PageUp\"", json, StringComparison.Ordinal);
    }
}
