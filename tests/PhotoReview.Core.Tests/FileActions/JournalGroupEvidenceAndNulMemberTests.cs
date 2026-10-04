using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// W2-FA-04: compaction must keep the members-carrying line the older-build group repair needs, so a failed repair append
/// at startup cannot erase the evidence of a half-moved capture. W2-FA-07: a corrupt member path must not abort reconcile.
/// </summary>
public sealed class JournalGroupEvidenceAndNulMemberTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JournalGroupMember[] Members =
    [
        new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
        new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp),
    ];

    private static JournalEntry GroupPrepared(string id, JournalGroupMember[]? members = null) =>
        new(id, FileOperationType.Move, JournalState.Prepared, (members ?? Members)[0].Source, (members ?? Members)[0].Destination,
            (members ?? Members)[0].Size, Stamp, Stamp, GroupId: "capture-" + id, GroupMembers: members ?? Members);

    // What an older build appends: the same Id, no GroupId/GroupMembers (they are omitted when null).
    private static JournalEntry OlderBuildCommitted(JournalEntry prepared) =>
        prepared with { State = JournalState.Committed, TimestampUtc = Stamp.AddSeconds(30), GroupId = null, GroupMembers = null };

    private static byte[] Line(JournalEntry entry) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\r\n");

    private static byte[] Concat(IEnumerable<byte[]> lines)
    {
        using var ms = new MemoryStream();
        foreach (var line in lines) ms.Write(line);
        return ms.ToArray();
    }

    // ---- W2-FA-04: plan ----

    [Fact(DisplayName = "W2-FA-04: compaction keeps the members-carrying Prepared line of an Id an older build settled without members")]
    public void Build_OlderBuildCommittedWithoutMembers_KeepsTheMembersLine()
    {
        var prepared = GroupPrepared("g");
        var committed = OlderBuildCommitted(prepared);
        var bytes = Concat([Line(prepared), Line(committed)]);

        var result = JournalCompactionPlan.Build(bytes);

        Assert.Equal(0, result.DroppedLines);
        Assert.Equal(bytes, result.Kept);
    }

    [Fact(DisplayName = "W2-FA-04: once the line after the members line carries members too, the older members line is droppable again")]
    public void Build_CommittedWithMembers_DropsEarlierPrepared()
    {
        var prepared = GroupPrepared("g");
        var committed = prepared with { State = JournalState.Committed, TimestampUtc = Stamp.AddSeconds(30) };
        var bytes = Concat([Line(prepared), Line(committed)]);

        var result = JournalCompactionPlan.Build(bytes);

        Assert.Equal(1, result.DroppedLines);
        Assert.Equal(Line(committed), result.Kept);
    }

    [Fact(DisplayName = "W2-FA-04: only the LAST members-carrying line is kept; a Dismissed older-build line needs no evidence")]
    public void Build_KeepsOnlyLastMembersLine_AndNothingForDismissed()
    {
        var first = GroupPrepared("g");
        var reprepared = first with { TimestampUtc = Stamp.AddSeconds(10) };
        var committed = OlderBuildCommitted(first);
        var dismissedPrepared = GroupPrepared("d");
        var dismissed = OlderBuildCommitted(dismissedPrepared) with { State = JournalState.Dismissed };

        var result = JournalCompactionPlan.Build(Concat(
            [Line(first), Line(reprepared), Line(committed), Line(dismissedPrepared), Line(dismissed)]));

        Assert.Equal(Concat([Line(reprepared), Line(committed), Line(dismissed)]), result.Kept);
        Assert.Equal(2, result.DroppedLines);
    }

    // ---- W2-FA-04: startup, repair append fails, compaction runs ----

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryCompactionFiles(InMemoryFileSystem fs) : IJournalCompactionFiles
    {
        public Stream OpenReadDenyWriters(string path) => fs.OpenReadShared(path);

        public IStagedReplacement CreateStagedReplacement(string tempPath) => new Staged(fs);

        private sealed class Staged(InMemoryFileSystem fs) : IStagedReplacement
        {
            private readonly MemoryStream _bytes = new();
            public void Write(ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);
            public void FlushToDisk() { }
            public void ReplaceAtomically(string destination) => fs.AddFile(destination, _bytes.ToArray(), Stamp);
            public void Dispose() => _bytes.Dispose();
        }
    }

    [Fact(DisplayName = "W2-FA-04: a failed older-build repair append at startup does not let compaction erase the evidence; the next start still surfaces the half-moved group")]
    public async Task Startup_RepairAppendFails_CompactionKeepsEvidence_NextStartSurfacesFailedGroup()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\selected\a.jpg", new string('x', 10), Stamp); // first member moved
        fs.AddFile(@"C:\photos\a.cr2", new string('x', 100), Stamp);  // second member never moved
        var prepared = GroupPrepared("g");
        // Filler: droppable Prepared/Committed pairs, enough for a compaction past 1 MiB and 25% gain.
        var lines = new List<byte[]>();
        for (var i = 0; i < 4000; i++)
        {
            var filler = new JournalEntry($"f{i:D5}", FileOperationType.Move, JournalState.Prepared,
                $@"C:\photos\{new string('p', 60)}\f{i:D5}.jpg", $@"C:\selected\{new string('p', 60)}\f{i:D5}.jpg", 1, Stamp, Stamp);
            lines.Add(Line(filler));
            lines.Add(Line(filler with { State = JournalState.Committed, TimestampUtc = Stamp.AddSeconds(1) }));
        }
        lines.Add(Line(prepared));
        lines.Add(Line(OlderBuildCommitted(prepared)));
        fs.AddFile(Paths.JournalFile, Concat(lines), Stamp);
        var clock = new FakeClock(Stamp.AddMinutes(1));
        var journal = new OperationJournal(Paths, fs, clock, appendRetryDelay: _ => { }, compactionFiles: new InMemoryCompactionFiles(fs));
        var sizeBefore = fs.GetFileStat(Paths.JournalFile)!.Length;

        fs.OpenAppendHook = _ => new IOException("disk full"); // the repair append fails (disk full / antivirus lock)
        var failedAtFirstStart = await JournalStartupRecovery.RunAsync(journal, clock);
        fs.OpenAppendHook = null;

        Assert.Empty(failedAtFirstStart);
        Assert.True(fs.GetFileStat(Paths.JournalFile)!.Length < sizeBefore, "the filler pairs must have been compacted away");
        var surfaced = Assert.Single(journal.ReconcilePendingOperations()); // the next start, appends work again
        Assert.Equal("g", surfaced.Id);
        Assert.Equal(JournalState.Failed, surfaced.State);
        Assert.Equal(Members, Assert.Single(journal.ReadFailedOperations()).GroupMembers);
    }

    // ---- W2-FA-07: corrupt member paths ----

    private static string MemberLine(JournalEntry entry) => JsonSerializer.Serialize(entry);

    [Fact(DisplayName = "W2-FA-07: a NUL in a group member path does not abort reconcile (real file system): the other entries are still reconciled")]
    public void Reconcile_NulInMemberPath_DoesNotAbortReconcile()
    {
        using var root = new TempRoot("journal-nul-member");
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
        var done = Path.Combine(root.Path, "done.jpg");
        File.WriteAllText(done, "12345");
        var badMembers = new JournalGroupMember[]
        {
            new(Path.Combine(root.Path, "gone-a.jpg"), Path.Combine(root.Path, "sel", "a\0.jpg"), 5, Stamp),
            new(Path.Combine(root.Path, "gone-b.jpg"), Path.Combine(root.Path, "sel", "b.jpg"), 5, Stamp),
        };
        var badPrepared = GroupPrepared("bad", badMembers);
        var good = new JournalEntry("good", FileOperationType.Move, JournalState.Prepared,
            Path.Combine(root.Path, "moved-away.jpg"), done, 5, Stamp, Stamp);
        File.WriteAllText(paths.JournalFile, string.Join("\r\n", [
            MemberLine(badPrepared), MemberLine(OlderBuildCommitted(badPrepared)), MemberLine(good)]) + "\r\n");
        var journal = new OperationJournal(paths, new PhysicalFileSystem(), new FakeClock(Stamp.AddMinutes(1)));

        var reconciled = journal.ReconcilePendingOperations();

        Assert.Contains(reconciled, entry => entry.Id == "good" && entry.State == JournalState.Committed);
    }

    [Theory(DisplayName = "W2-FA-07: a group line with a NUL in a member Source or Destination is rejected like an entry-level NUL path")]
    [InlineData("a\0.jpg", @"C:\selected\a.jpg")]
    [InlineData(@"C:\photos\a.jpg", "b\0.jpg")]
    public void TryParse_NulInMemberPath_IsRejected(string source, string destination)
    {
        var members = new JournalGroupMember[] { new(source, destination, 1, Stamp), Members[1] };
        var line = JsonSerializer.Serialize(GroupPrepared("n", members));

        Assert.Null(JournalLineParser.TryParse(line));
        Assert.NotNull(JournalLineParser.TryParse(JsonSerializer.Serialize(GroupPrepared("ok"))));
    }

    [Fact(DisplayName = "W2-FA-07: a member path the file system rejects stops only its own repair; other groups are still repaired")]
    public void Reconcile_RepairOfOneGroupThrows_OtherGroupStillRepaired()
    {
        var inner = new InMemoryFileSystem();
        inner.AddFile(@"C:\selected\a.jpg", new string('x', 10), Stamp);
        inner.AddFile(@"C:\photos\a.cr2", new string('x', 100), Stamp);
        var fs = new CrashPointFileSystem(inner);
        var poisoned = new JournalGroupMember[] { new(@"C:\photos\p.jpg", @"C:\selected\poison.jpg", 1, Stamp), Members[0] };
        fs.ArgumentFaultPaths.Add(@"C:\selected\poison.jpg");
        var poisonedPrepared = GroupPrepared("poisoned", poisoned);
        var healthy = GroupPrepared("healthy");
        inner.AddFile(Paths.JournalFile, Concat([
            Line(poisonedPrepared), Line(OlderBuildCommitted(poisonedPrepared)), Line(healthy), Line(OlderBuildCommitted(healthy))]), Stamp);
        var journal = new OperationJournal(Paths, fs, new FakeClock(Stamp.AddMinutes(1)));

        var reconciled = journal.ReconcilePendingOperations();

        Assert.Equal("healthy", Assert.Single(reconciled).Id);
        Assert.Equal(JournalState.Failed, reconciled[0].State);
    }
}
