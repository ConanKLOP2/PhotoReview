using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class JournalGroupSchemaTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SingleFileEntry_KeepsLegacyJsonShapeAndParsesWithoutGroupMetadata()
    {
        var entry = new JournalEntry("single", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp, Stamp);

        var json = JsonSerializer.Serialize(entry);
        var parsed = JournalLineParser.TryParse(json);

        Assert.DoesNotContain("GroupId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupMembers", json, StringComparison.Ordinal);
        Assert.NotNull(parsed);
        Assert.Null(parsed.GroupId);
        Assert.Null(parsed.GroupMembers);
    }

    [Fact]
    public void GroupEntry_ReadByOlderBuild_LooksLikeASingleMoveOfTheFirstMember()
    {
        // Cross-build compatibility (documented in docs/architecture.md): a build without group support skips the unknown
        // GroupId/GroupMembers members, so it sees ONE Move whose top-level fields are the FIRST member's. The top level is
        // therefore always kept equal to the first member; it is not a judgement about the other members.
        var members = new JournalGroupMember[]
        {
            new(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp),
            new(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 120, Stamp),
        };
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Committed,
            members[0].Source, members[0].Destination, members[0].Size, Stamp, Stamp, GroupId: "capture-a", GroupMembers: members);

        var legacy = JsonSerializer.Deserialize<LegacyEntry>(JsonSerializer.Serialize(entry));

        Assert.NotNull(legacy);
        Assert.Equal(members[0].Source, legacy.Source);
        Assert.Equal(members[0].Destination, legacy.Destination);
        Assert.Equal(members[0].Size, legacy.Size);
    }

    [Fact]
    public void CommittedGroupMove_TailReaderKeepsEveryMemberOnTheReturnedEntry()
    {
        // ReadCommittedMoves exposes a group as one entry (top level = first member). Only the fingerprint fallback of
        // UndoService looks entries up by top-level Destination, and group moves never enter that history (they undo from the
        // members registered in memory), so a caller needing other members reads GroupMembers.
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp, Stamp, GroupId: "capture-a", GroupMembers:
            [
                new(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp),
                new(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 120, Stamp),
            ]);

        var parsed = OperationJournal.TryParseTailCommittedMove(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)));

        Assert.NotNull(parsed);
        Assert.Equal(entry, parsed);
        Assert.Equal(2, parsed.GroupMembers!.Count);
    }

    // The pre-group JournalEntry shape (an older build): unknown members are ignored by System.Text.Json.
    private sealed record LegacyEntry(string Id, string Source, string? Destination, long Size);

    [Fact]
    public void GroupEntry_RoundTripsAllMemberFingerprintsAndDestinations()
    {
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp, Stamp,
            GroupId: "capture-a", GroupMembers:
            [
                new(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 12, Stamp),
                new(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 120, Stamp),
                new(@"C:\photos\a.xmp", @"C:\photos\selected\a.xmp", 4, Stamp)
            ]);

        var parsed = JournalLineParser.TryParse(JsonSerializer.Serialize(entry));

        Assert.NotNull(parsed);
        Assert.Equal(entry.Id, parsed.Id);
        Assert.Equal(entry.GroupId, parsed.GroupId);
        var members = Assert.IsAssignableFrom<IReadOnlyList<JournalGroupMember>>(parsed.GroupMembers);
        Assert.Equal([entry.Source, @"C:\photos\a.cr2", @"C:\photos\a.xmp"], members.Select(member => member.Source));
        Assert.Equal([12L, 120L, 4L], members.Select(member => member.Size));
    }
}
