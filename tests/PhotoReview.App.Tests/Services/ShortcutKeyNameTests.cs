using System.Windows.Input;
using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Services;

public class ShortcutKeyNameTests
{
    [Theory(DisplayName = "Real command keys parse, case-insensitively and trimmed")]
    [InlineData("Right", Key.Right)]
    [InlineData(" delete ", Key.Delete)]
    [InlineData("F11", Key.F11)]
    [InlineData("Space", Key.Space)]
    public void TryParse_ValidKey_Parses(string name, Key expected)
    {
        Assert.True(ShortcutKeyName.TryParse(name, out var key));
        Assert.Equal(expected, key);
    }

    [Theory(DisplayName = "Numbers, comma lists, unknown names and never-firing keys are rejected")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("+44")]
    [InlineData("A,B")]
    [InlineData("NotAKey")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Escape")]
    [InlineData("Tab")]
    [InlineData("ImeProcessed")]
    [InlineData("None")]
    [InlineData("LeftCtrl")]
    public void TryParse_Unusable_Rejected(string? name)
    {
        Assert.False(ShortcutKeyName.TryParse(name, out _));
    }
}

public class ShortcutKeyCanonicalTableTests
{
    [Fact(DisplayName = "Every alias pair of the Core table is a true same-value alias of the WPF Key enum")]
    public void AliasTable_MatchesKeyEnum()
    {
        foreach (var (alias, canonical) in PhotoReview.Core.Settings.ShortcutKeyCanonical.AliasPairs)
        {
            Assert.True(Enum.TryParse<Key>(alias, out var a), alias);
            Assert.True(Enum.TryParse<Key>(canonical, out var c), canonical);
            Assert.Equal(a, c);
        }
    }

    [Fact(DisplayName = "Every Key name that shares a value with another is covered by the table (no missing alias)")]
    public void AliasTable_CoversAllEnumAliases()
    {
        var covered = PhotoReview.Core.Settings.ShortcutKeyCanonical.AliasPairs.SelectMany(p => new[] { p.Alias, p.Canonical }).ToHashSet(StringComparer.Ordinal);
        foreach (var group in Enum.GetNames<Key>().GroupBy(n => (int)Enum.Parse<Key>(n)).Where(g => g.Count() > 1))
            foreach (var name in group) Assert.Contains(name, covered);
    }

    [Theory(DisplayName = "Aliases parse to the same Key as their canonical name")]
    [InlineData("Return", "Enter")]
    [InlineData("Prior", "PageUp")]
    [InlineData("next", "PageDown")]
    public void TryParse_Alias_SameKey(string alias, string canonical)
    {
        Assert.True(ShortcutKeyName.TryParse(alias, out var a));
        Assert.True(ShortcutKeyName.TryParse(canonical, out var c));
        Assert.Equal(a, c);
    }
}
