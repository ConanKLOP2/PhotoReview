using System.IO;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Pins the hang detection of <see cref="BinaryFuzz.Run"/> itself: a case that blocks must fail the run by name (per-case bound),
/// a corpus that stops making progress must fail on the whole-corpus bound, and slow-but-finishing cases must not fail.
/// The blocking is done on an event the test releases, so no case outlives the test.
/// </summary>
public sealed class BinaryFuzzHarnessTests
{
    private static readonly byte[] Seed = new byte[64];

    private static BinaryFuzz.Case[] Corpus(params string[] labels) =>
        labels.Select(l => new BinaryFuzz.Case(l, (byte[])Seed.Clone())).ToArray();

    [Fact(DisplayName = "BinaryFuzz.Run: a case that blocks fails the run naming that case within the per-case bound")]
    public void Run_BlockingExercise_IsReportedAsStuck()
    {
        using var release = new ManualResetEventSlim(false);
        var cases = Corpus("ok-1", "blocker", "never-reached");
        var index = 0;
        try
        {
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => BinaryFuzz.Run(
                "harness",
                cases,
                _ =>
                {
                    if (Interlocked.Increment(ref index) == 2) release.Wait(TimeSpan.FromMinutes(1));
                    return true;
                },
                _ => false,
                bound: TimeSpan.FromMinutes(5),
                caseBound: TimeSpan.FromMilliseconds(300)));
            Assert.Contains("[blocker]", ex.Message);
            Assert.Contains("per-case bound", ex.Message);
        }
        finally { release.Set(); }
    }

    [Fact(DisplayName = "BinaryFuzz.Run: a corpus that makes no progress within the total bound fails even when each case is within its own bound")]
    public void Run_CorpusExceedingTheTotalBound_Fails()
    {
        using var release = new ManualResetEventSlim(false);
        var cases = Corpus("c1", "c2");
        try
        {
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => BinaryFuzz.Run(
                "harness",
                cases,
                _ =>
                {
                    release.Wait(TimeSpan.FromMinutes(1));
                    return true;
                },
                _ => false,
                bound: TimeSpan.FromMilliseconds(300),
                caseBound: TimeSpan.FromMinutes(5)));
            Assert.Contains("corpus not finished", ex.Message);
        }
        finally { release.Set(); }
    }

    [Fact(DisplayName = "BinaryFuzz.Run: cases that finish are counted and a documented exception is a clean rejection")]
    public void Run_FinishingCases_AreCounted()
    {
        var stats = BinaryFuzz.Run(
            "harness",
            Corpus("a", "b", "c"),
            bytes => bytes.Length > 0 ? throw new InvalidDataException("clean") : true,
            ex => ex is InvalidDataException);

        Assert.Equal(new BinaryFuzz.Stats(0, 3, 3), stats);
    }

    [Fact(DisplayName = "BinaryFuzz.Run: an undocumented exception is reported as a defect with its case label")]
    public void Run_UndocumentedException_IsADefect()
    {
        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => BinaryFuzz.Run(
            "harness",
            Corpus("boom"),
            _ => throw new InvalidOperationException("oops"),
            _ => false));

        Assert.Contains("[boom]", ex.Message);
        Assert.Contains("InvalidOperationException", ex.Message);
    }
}
