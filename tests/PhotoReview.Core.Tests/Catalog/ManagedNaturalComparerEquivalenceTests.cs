using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>The allocation-free comparer must equal the original StringBuilder implementation (<see cref="NaturalKeyReference"/>) for every input.</summary>
[Trait("Category", "HotPath")]
public sealed class ManagedNaturalComparerEquivalenceTests
{
    // Digits (ASCII, zero, Arabic-Indic, fullwidth), letters in both cases, separators, a surrogate pair.
    private static readonly char[] Alphabet =
        ['0', '0', '0', '1', '2', '9', '٣', '٠', '１', 'a', 'A', 'b', 'Z', 'i', 'I', ' ', '(', ')', '.', '_', 'É', 'é', '\uD83D', '\uDE00'];

    private static string Random(Random r, int maxLength)
    {
        var chars = new char[r.Next(0, maxLength + 1)];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[r.Next(Alphabet.Length)];
        return new string(chars);
    }

    [Fact(DisplayName = "BuildNaturalKey and Compare match the original implementation on random names, including very long ones")]
    public void MatchesOriginal_OnRandomInput()
    {
        var r = new Random(20260926);
        var comparer = ManagedNaturalComparer.Instance;
        for (var i = 0; i < 20_000; i++)
        {
            var maxLength = i % 50 == 0 ? 400 : 24; // long names exercise the pooled-buffer path (> 256 key chars)
            var a = Random(r, maxLength);
            var b = i % 7 == 0 ? a.ToUpperInvariant() : Random(r, maxLength);

            Assert.Equal(NaturalKeyReference.BuildNaturalKey(a), ManagedNaturalComparer.BuildNaturalKey(a));
            Assert.Equal(Math.Sign(NaturalKeyReference.Compare(a, b)), Math.Sign(comparer.Compare(a, b)));
        }
    }

    [Theory(DisplayName = "Edge names keep their exact key")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("a0b00c")]
    [InlineData("007.jpg")]
    [InlineData("12345678901234567890")]
    public void EdgeKeys_MatchOriginal(string name) =>
        Assert.Equal(NaturalKeyReference.BuildNaturalKey(name), ManagedNaturalComparer.BuildNaturalKey(name));
}
