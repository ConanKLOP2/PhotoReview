using System.Text;
using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>RV-T03 gap: an Id whose latest line is a fresh Prepared after a Dismissed must lose nothing in <see cref="JournalCompactionPlan.Build"/>.</summary>
public sealed class JournalCompactionPlanReprepareTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JournalEntry Entry(string id, JournalState state, int tick, string? errorCode = null) =>
        new(id, FileOperationType.Copy, state, $@"C:\photos\{id}.jpg", $@"C:\photos\sel\{id}.jpg", 1, Base, Base.AddSeconds(tick),
            Error: errorCode is null ? null : "boom", ErrorCode: errorCode);

    private static byte[] Line(JournalEntry e) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\r\n");

    private static byte[] Concat(params byte[][] lines)
    {
        using var ms = new MemoryStream();
        foreach (var l in lines) ms.Write(l);
        return ms.ToArray();
    }

    [Fact]
    public void Build_DismissedThenRePrepared_NothingOfThatIdIsDropped()
    {
        var p1 = Line(Entry("a", JournalState.Prepared, 0));
        var f = Line(Entry("a", JournalState.Failed, 1));
        var d = Line(Entry("a", JournalState.Dismissed, 2));
        var p2 = Line(Entry("a", JournalState.Prepared, 3)); // a retry re-prepared the Id after the user dismissed it
        var bytes = Concat(p1, f, d, p2);

        var result = JournalCompactionPlan.Build(bytes);

        Assert.Equal(0, result.DroppedLines);
        Assert.Equal(bytes, result.Kept);
    }

    [Fact]
    public void Build_DismissedThenRePreparedThenCommitted_OnlyCommittedKept()
    {
        var c = Line(Entry("a", JournalState.Committed, 4));
        var bytes = Concat(Line(Entry("a", JournalState.Prepared, 0)), Line(Entry("a", JournalState.Dismissed, 1)),
            Line(Entry("a", JournalState.Prepared, 2)), c);

        var result = JournalCompactionPlan.Build(bytes);

        Assert.Equal(3, result.DroppedLines);
        Assert.Equal(c, result.Kept);
    }

    [Fact]
    public void Build_CommittedThenGenuineFailedWithoutReconcileCode_FailedIsLatestSoNothingIsDropped()
    {
        // The FA-01 exemption is only for reconcile verdicts: a real Failed after Committed is the latest line.
        var bytes = Concat(Line(Entry("a", JournalState.Prepared, 0)), Line(Entry("a", JournalState.Committed, 1)),
            Line(Entry("a", JournalState.Failed, 2, JournalErrors.RetryVerifyFailed)));

        var result = JournalCompactionPlan.Build(bytes);

        Assert.Equal(0, result.DroppedLines);
        Assert.Equal(bytes, result.Kept);
    }
}
