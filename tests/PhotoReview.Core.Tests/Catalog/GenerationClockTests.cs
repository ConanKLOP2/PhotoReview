using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class GenerationClockTests
{
    [Fact]
    public void DefaultConstructor_InitializesAllGenerationsToZero()
    {
        var clock = new GenerationClock();

        Assert.Equal(0, clock.CurrentNavigation);
        Assert.Equal(0, clock.CurrentFolder);
        Assert.Equal(0, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(0));
        Assert.True(clock.IsFolderCurrent(0));
        Assert.True(clock.IsInteractionCurrent(0));
    }

    [Fact]
    public void ParameterizedConstructor_InitializesWithSpecifiedValues()
    {
        var clock = new GenerationClock(initialNavigation: 12, initialFolder: 34, initialInteraction: 56);

        Assert.Equal(12, clock.CurrentNavigation);
        Assert.Equal(34, clock.CurrentFolder);
        Assert.Equal(56, clock.CurrentInteraction);
        Assert.True(clock.IsNavigationCurrent(12));
        Assert.True(clock.IsFolderCurrent(34));
        Assert.True(clock.IsInteractionCurrent(56));
    }

    [Fact]
    public void NextNavigation_IncrementsMonotonicallyAndUpdatesCurrent()
    {
        var clock = new GenerationClock();

        var t1 = clock.NextNavigation();
        Assert.Equal(1, t1);
        Assert.Equal(1, clock.CurrentNavigation);
        Assert.True(clock.IsNavigationCurrent(1));
        Assert.False(clock.IsNavigationCurrent(0));

        var t2 = clock.NextNavigation();
        Assert.Equal(2, t2);
        Assert.Equal(2, clock.CurrentNavigation);
        Assert.True(clock.IsNavigationCurrent(2));
        Assert.False(clock.IsNavigationCurrent(1));
    }

    [Fact]
    public void NextFolder_IncrementsMonotonicallyAndUpdatesCurrent()
    {
        var clock = new GenerationClock();

        var t1 = clock.NextFolder();
        Assert.Equal(1, t1);
        Assert.Equal(1, clock.CurrentFolder);
        Assert.True(clock.IsFolderCurrent(1));
        Assert.False(clock.IsFolderCurrent(0));

        var t2 = clock.NextFolder();
        Assert.Equal(2, t2);
        Assert.Equal(2, clock.CurrentFolder);
        Assert.True(clock.IsFolderCurrent(2));
        Assert.False(clock.IsFolderCurrent(1));
    }

    [Fact]
    public void NextInteraction_IncrementsMonotonicallyAndUpdatesCurrent()
    {
        var clock = new GenerationClock();

        var t1 = clock.NextInteraction();
        Assert.Equal(1, t1);
        Assert.Equal(1, clock.CurrentInteraction);
        Assert.True(clock.IsInteractionCurrent(1));
        Assert.False(clock.IsInteractionCurrent(0));

        var t2 = clock.NextInteraction();
        Assert.Equal(2, t2);
        Assert.Equal(2, clock.CurrentInteraction);
        Assert.True(clock.IsInteractionCurrent(2));
        Assert.False(clock.IsInteractionCurrent(1));
    }

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
