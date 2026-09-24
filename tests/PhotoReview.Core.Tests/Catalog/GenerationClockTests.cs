using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public sealed class GenerationClockTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(12, 34, 56)]
    public void Constructor_InitializesGenerationsToSpecifiedValues(long navigation, long folder, long interaction)
    {
        var clock = new GenerationClock(initialNavigation: navigation, initialFolder: folder, initialInteraction: interaction);

        Assert.Equal(navigation, clock.CurrentNavigation);
        Assert.Equal(folder, clock.CurrentFolder);
        Assert.Equal(interaction, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(navigation));
        Assert.True(clock.IsFolderCurrent(folder));
        Assert.True(clock.IsInteractionCurrent(interaction));
    }

    public enum Generation { Navigation, Folder, Interaction }

    [Theory]
    [InlineData(Generation.Navigation)]
    [InlineData(Generation.Folder)]
    [InlineData(Generation.Interaction)]
    public void Next_IncrementsMonotonicallyAndUpdatesCurrent(Generation generation)
    {
        var clock = new GenerationClock();

        var t1 = Next(clock, generation);
        Assert.Equal(1, t1);
        Assert.Equal(1, Current(clock, generation));
        Assert.True(IsCurrent(clock, generation, 1));
        Assert.False(IsCurrent(clock, generation, 0));

        var t2 = Next(clock, generation);
        Assert.Equal(2, t2);
        Assert.Equal(2, Current(clock, generation));
        Assert.True(IsCurrent(clock, generation, 2));
        Assert.False(IsCurrent(clock, generation, 1));
    }

    private static long Next(GenerationClock clock, Generation generation) => generation switch
    {
        Generation.Navigation => clock.NextNavigation(),
        Generation.Folder => clock.NextFolder(),
        _ => clock.NextInteraction(),
    };

    private static long Current(GenerationClock clock, Generation generation) => generation switch
    {
        Generation.Navigation => clock.CurrentNavigation,
        Generation.Folder => clock.CurrentFolder,
        _ => clock.CurrentInteraction,
    };

    private static bool IsCurrent(GenerationClock clock, Generation generation, long value) => generation switch
    {
        Generation.Navigation => clock.IsNavigationCurrent(value),
        Generation.Folder => clock.IsFolderCurrent(value),
        _ => clock.IsInteractionCurrent(value),
    };

    [Fact]
    public void StopForAction_IncrementsAllThreeCountersSimultaneously()
    {
        var clock = new GenerationClock(5, 10, 15);

        clock.StopForAction();

        Assert.Equal(6, clock.CurrentNavigation);
        Assert.Equal(11, clock.CurrentFolder);
        Assert.Equal(16, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(6));
        Assert.True(clock.IsFolderCurrent(11));
        Assert.True(clock.IsInteractionCurrent(16));
        Assert.False(clock.IsNavigationCurrent(5));
        Assert.False(clock.IsFolderCurrent(10));
        Assert.False(clock.IsInteractionCurrent(15));
    }

    [Fact]
    public void ConcurrentIncrements_AreThreadSafeAndAccurate()
    {
        var clock = new GenerationClock();
        const int iterations = 10_000;

        Parallel.For(0, iterations, _ =>
        {
            clock.NextNavigation();
            clock.NextFolder();
            clock.NextInteraction();
        });

        Assert.Equal(iterations, clock.CurrentNavigation);
        Assert.Equal(iterations, clock.CurrentFolder);
        Assert.Equal(iterations, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(iterations));
        Assert.True(clock.IsFolderCurrent(iterations));
        Assert.True(clock.IsInteractionCurrent(iterations));
    }

    [Fact]
    public void ConcurrentStopForAction_IncrementsAllCountersWithoutLoss()
    {
        var clock = new GenerationClock();
        const int iterations = 5_000;

        Parallel.For(0, iterations, _ =>
        {
            clock.StopForAction();
        });

        Assert.Equal(iterations, clock.CurrentNavigation);
        Assert.Equal(iterations, clock.CurrentFolder);
        Assert.Equal(iterations, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(iterations));
        Assert.True(clock.IsFolderCurrent(iterations));
        Assert.True(clock.IsInteractionCurrent(iterations));
    }
}

