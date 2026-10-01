using System.Text;
using System.Text.Json;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>The tail (reverse) reader's line predicate must accept exactly what the full reader accepts.</summary>
public sealed class JournalTailCommittedMoveTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static byte[] Line(JournalEntry entry) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry));

    private static JournalEntry CommittedMove(params JournalGroupMember[] members) =>
        new("m", FileOperationType.Move, JournalState.Committed, @"C:\a.jpg", @"C:\s\a.jpg", 1, Stamp, Stamp,
            GroupId: members.Length > 0 ? "g" : null, GroupMembers: members.Length > 0 ? members : null);

    [Fact]
    public void TryParseTailCommittedMove_ValidCommittedGroupMove_IsReturned()
    {
        var entry = OperationJournal.TryParseTailCommittedMove(Line(CommittedMove(new JournalGroupMember(@"C:\a.jpg", @"C:\s\a.jpg", 1, Stamp))));

        Assert.NotNull(entry);
        Assert.Single(entry.GroupMembers!);
    }

    [Theory]
    [InlineData("""{"Id":"m","Type":"Move","State":"Committed","Source":"C:\\a.jpg","Destination":"C:\\s\\a.jpg","Size":1,"LastWriteUtc":"2026-09-29T00:00:00Z","TimestampUtc":"2026-09-29T00:00:00Z","GroupId":"g","GroupMembers":[{"Source":"","Destination":"C:\\s\\a.jpg","Size":1,"LastWriteUtc":"2026-09-29T00:00:00Z"}]}""")]
    [InlineData("""{"Id":"m","Type":"Move","State":"Committed","Source":"C:\\a.jpg","Destination":"C:\\s\\a.jpg","Size":1,"LastWriteUtc":"2026-09-29T00:00:00Z","TimestampUtc":"2026-09-29T00:00:00Z","GroupId":"g","GroupMembers":[null]}""")]
    [InlineData("""{"Id":"","Type":"Move","State":"Committed","Source":"C:\\a.jpg","Destination":"C:\\s\\a.jpg","Size":1,"LastWriteUtc":"2026-09-29T00:00:00Z","TimestampUtc":"2026-09-29T00:00:00Z"}""")]
    public void TryParseTailCommittedMove_LineTheFullReaderRejects_IsQuarantinedHereToo(string json)
    {
        Assert.Null(JournalLineParser.TryParse(json)); // the reference rule

        Assert.Null(OperationJournal.TryParseTailCommittedMove(Encoding.UTF8.GetBytes(json)));
    }
}
