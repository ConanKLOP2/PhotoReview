using PhotoReview.Core.Catalog;
using PhotoReview.Core.Tests.Catalog;

namespace PhotoReview.Core.Tests.Properties;

/// <summary>Natural-sort comparer vs the verbatim original (<see cref="NaturalKeyReference"/>), under the shared seeded runner.</summary>
[Trait("Category", "HotPath")]
public sealed class NaturalSortOraclePropertyTests
{
    private static readonly string[] Pieces =
        ["a", "B", "z", "0", "00", "1", "7", "10", "007", "٣", "３", " ", "(", ")", ".", "_", "-", "İ", "i", "é", ".jpg", ".JPG", "😀"];

    private static string Name(Random rng) => string.Concat(Enumerable.Range(0, rng.Next(0, 7)).Select(_ => Pieces[rng.Next(Pieces.Length)]));

    [Fact(DisplayName = "Compare agrees in sign with the reference oracle, and Array.Sort gives the oracle's order for any list")]
    public void Compare_And_Sort_MatchOracle()
    {
        var comparer = ManagedNaturalComparer.Instance;
        PropertyRunner.Check("Natural sort vs oracle", iterations: 100, (rng, _) =>
        {
            var names = Enumerable.Range(0, rng.Next(2, 40)).Select(_ => Name(rng)).ToArray();
            foreach (var a in names.Take(6))
                foreach (var b in names)
                    Assert.True(Math.Sign(comparer.Compare(a, b)) == Math.Sign(NaturalKeyReference.Compare(a, b)), $"'{a}' vs '{b}'");

            var ours = names.Order(comparer).ToArray();
            var oracle = names.Order(Comparer<string>.Create(NaturalKeyReference.Compare)).ToArray();
            Assert.Equal(oracle, ours);
        });
    }
}
