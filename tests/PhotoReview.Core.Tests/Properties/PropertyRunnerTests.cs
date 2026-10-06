namespace PhotoReview.Core.Tests.Properties;

/// <summary>The seed contract the property tests rely on: fixed in CI, replayable, and every failure names its seed.</summary>
public sealed class PropertyRunnerTests
{
    [Fact]
    public void Seeds_DefaultIsTheFixedList() => Assert.Equal([1, 2, 3, 4], PropertyRunner.Seeds(4, envValue: ""));

    [Fact]
    public void Seeds_IntegerReplaysExactlyThatSeed() => Assert.Equal([12345], PropertyRunner.Seeds(4, envValue: " 12345 "));

    [Fact]
    public void Seeds_RandomGivesOneNonNegativeSeed() => Assert.True(Assert.Single(PropertyRunner.Seeds(4, envValue: "random")) >= 0);

    [Fact]
    public void Seeds_GarbageIsRejected() => Assert.Throws<ArgumentException>(() => PropertyRunner.Seeds(4, envValue: "banana"));

    [Fact]
    public void Check_FailureNamesSeedIterationAndReplayVariable()
    {
        var ex = Assert.Throws<PropertyFailedException>(() =>
            PropertyRunner.Check("demo", 10, (_, i) => { if (i == 4) throw new InvalidOperationException("boom"); }, fixedSeeds: 2, envValue: ""));

        Assert.Contains("seed=1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("iteration=4", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PropertyRunner.SeedEnvVar + "=1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_SameSeedSeesTheSameSequence()
    {
        List<int> Run() { var l = new List<int>(); PropertyRunner.Check("d", 5, (r, _) => l.Add(r.Next()), fixedSeeds: 1, envValue: ""); return l; }
        Assert.Equal(Run(), Run());
    }
}
