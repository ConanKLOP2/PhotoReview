using System.Text.Json;
using PhotoReview.Core.FileActions;
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
