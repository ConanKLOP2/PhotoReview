namespace PhotoReview.Core.Tests.Properties;

/// <summary>Canonicalisation properties: idempotent, case/space-insensitive, alias and canonical name meet at one spelling.</summary>
public sealed class ShortcutKeyCanonicalPropertyTests
{
    private const string Alphabet = "abcXYZ019 _-\t";

    private static string Jitter(Random rng, string name)
    {
        var chars = name.Select(c => rng.Next(3) switch { 0 => char.ToUpperInvariant(c), 1 => char.ToLowerInvariant(c), _ => c }).ToArray();
        return new string(' ', rng.Next(0, 3)) + new string(chars) + new string('\t', rng.Next(0, 2));
    }

    [Fact(DisplayName = "Canonicalize is idempotent and maps every alias, in any casing and padding, to the canonical name")]
    public void AliasAndCanonical_MeetAtOneSpelling()
    {
        var pairs = ShortcutKeyCanonical.AliasPairs;
        PropertyRunner.Check("ShortcutKeyCanonical aliases", iterations: 2000, (rng, _) =>
        {
            var (alias, canonical) = pairs[rng.Next(pairs.Count)];
            var name = rng.Next(2) == 0 ? alias : canonical;

            var result = ShortcutKeyCanonical.Canonicalize(Jitter(rng, name));

            Assert.Equal(canonical, result);
            Assert.Equal(result, ShortcutKeyCanonical.Canonicalize(result));
        });
    }

    [Fact(DisplayName = "Canonicalize is idempotent for arbitrary text, trims, and gives \"\" for blank; the result is never an alias")]
    public void ArbitraryText_IsIdempotentAndNeverLeavesAnAlias()
    {
        var aliases = ShortcutKeyCanonical.AliasPairs.Select(p => p.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PropertyRunner.Check("ShortcutKeyCanonical arbitrary", iterations: 5000, (rng, _) =>
        {
            var text = rng.Next(8) == 0 ? null : PropertyRunner.RandomString(rng, Alphabet, 8);

            var once = ShortcutKeyCanonical.Canonicalize(text);

            Assert.Equal(once, ShortcutKeyCanonical.Canonicalize(once));
            Assert.Equal(once, once.Trim());
            Assert.False(aliases.Contains(once), $"'{once}' is still an alias");
            if (string.IsNullOrWhiteSpace(text)) Assert.Equal("", once);
            else if (!aliases.Contains(text.Trim()) && !ShortcutKeyCanonical.AliasPairs.Any(p => string.Equals(p.Canonical, text.Trim(), StringComparison.OrdinalIgnoreCase)))
                Assert.Equal(text.Trim(), once); // unknown names pass through trimmed
        });
    }
}
