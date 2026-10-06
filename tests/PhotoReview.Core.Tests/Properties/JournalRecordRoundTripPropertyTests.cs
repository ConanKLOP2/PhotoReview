using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Properties;

/// <summary>A journal line written the way <c>OperationJournal</c> writes it (<see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions?)"/>) reads back as an equal record.</summary>
public sealed class JournalRecordRoundTripPropertyTests
{
    // Quotes, backslashes, control chars, BMP and astral text: everything a path or error text can carry except NUL (rejected on purpose).
    private const string Chars = "abcXYZ019 _-.:\\/\"'\t\r\n\u00e9\u65e5\u0e01{}[]";

    private static string Text(Random rng, int max) => PropertyRunner.RandomString(rng, Chars, max) + (rng.Next(6) == 0 ? "\ud83d\ude00" : "");

    private static string Path(Random rng) => @"C:\p\" + Text(rng, 12) + "x"; // never blank

    private static string? Opt(Random rng, Func<string> make) => rng.Next(3) == 0 ? null : make();

    private static DateTime Utc(Random rng) => new(rng.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);

    private static JournalEntry RandomEntry(Random rng)
    {
        IReadOnlyList<JournalGroupMember>? members = rng.Next(3) != 0
            ? null
            : [.. Enumerable.Range(0, rng.Next(0, 4)).Select(_ => new JournalGroupMember(Path(rng), Opt(rng, () => Path(rng)), rng.NextInt64(0, long.MaxValue), Utc(rng), rng.Next(2) == 0))];
        return new JournalEntry(
            Id: Guid.NewGuid().ToString("N") + Text(rng, 3),
            Type: (FileOperationType)rng.Next(0, 3),
            State: (JournalState)rng.Next(0, 4),
            Source: Path(rng),
            Destination: Opt(rng, () => Path(rng)),
            Size: PropertyRunner.EdgyLong(rng),
            LastWriteUtc: Utc(rng),
            TimestampUtc: Utc(rng),
            Error: Opt(rng, () => Text(rng, 30)),
            ErrorCode: Opt(rng, () => Text(rng, 8)),
            Permanent: rng.Next(3) switch { 0 => null, 1 => true, _ => false },
            Undo: rng.Next(3) switch { 0 => null, 1 => true, _ => false },
            GroupId: Opt(rng, () => Text(rng, 8)),
            GroupMembers: members);
    }

    [Fact(DisplayName = "Serialize then JournalLineParser.TryParse (string and UTF-8) returns an equal JournalEntry for random valid records")]
    public void SerializedEntry_ReadsBackEqual()
    {
        PropertyRunner.Check("Journal record round trip", iterations: 400, (rng, _) =>
        {
            var entry = RandomEntry(rng);
            var line = JsonSerializer.Serialize(entry);
            Assert.DoesNotContain('\n', line); // one journal record == one line

            var parsed = JournalLineParser.TryParse(line);
            Assert.NotNull(parsed);
            Assert.Equal(entry, parsed);
            Assert.Equal(entry.GetHashCode(), parsed!.GetHashCode());
            Assert.Equal(entry, JournalLineParser.TryParse(System.Text.Encoding.UTF8.GetBytes(line)));
            Assert.Equal(line, JsonSerializer.Serialize(parsed)); // re-serialising is byte-stable
        });
    }
}
