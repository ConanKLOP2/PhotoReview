using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Differential tests: <see cref="JournalLineParser"/> (single JSON pass) must accept/reject exactly the lines the
/// original two-pass reader (<c>JsonDocument.Parse</c> + enum recognition check, then <c>JsonSerializer.Deserialize</c>)
/// did, git-shown at HEAD~1 on this branch. <see cref="LegacyJournalLineParser"/> below is an exact copy of that logic
/// and is the oracle every test in this file compares against.
/// </summary>
public sealed class JournalLineParserTests
{
    // ---- Legacy oracle: an exact copy of OperationJournal.ReadEntries' per-line logic before this branch's change. ----
    private static class LegacyJournalLineParser
    {
        public static JournalEntry? TryParse(string line)
        {
            try
            {
                if (!HasRecognizedEnums(line)) return null;
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                return entry is not null && !string.IsNullOrEmpty(entry.Id) && entry.Source is not null ? entry : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool HasRecognizedEnums(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && IsKnown<FileOperationType>(root, nameof(JournalEntry.Type), "Delete")
                && IsKnown<JournalState>(root, nameof(JournalEntry.State), alias: null);
        }

        private static bool IsKnown<T>(JsonElement root, string property, string? alias) where T : struct, Enum
        {
            if (!root.TryGetProperty(property, out var value)) return false;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { } text
                    && (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                        || (alias is not null && string.Equals(text.Trim(), alias, StringComparison.OrdinalIgnoreCase))),
                JsonValueKind.Number => value.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number),
                _ => false,
            };
        }
    }

    // ---- Hand-written corpus: every skip/accept case called out in the task. ----
    private static readonly (string Name, string Json)[] Corpus =
    [
        ("ValidMovePrepared", Valid("Move", "Prepared")),
        ("ValidMoveCommitted", Valid("Move", "Committed")),
        ("ValidMoveFailed", Valid("Move", "Failed")),
        ("ValidMoveDismissed", Valid("Move", "Dismissed")),
        ("ValidCopyPrepared", Valid("Copy", "Prepared")),
        ("ValidCopyCommitted", Valid("Copy", "Committed")),
        ("ValidRecyclePrepared", Valid("Recycle", "Prepared")),
        ("ValidRecycleCommitted", Valid("Recycle", "Committed")),
        ("DeleteAliasLower", Valid("delete", "Prepared")),
        ("DeleteAliasUpper", Valid("DELETE", "Prepared")),
        ("DeleteAliasSpaced", Valid(" delete ", "Prepared")),
        ("NumericType0", Valid("0", "0", quoteType: false)),
        ("NumericType1", Valid("1", "0", quoteType: false)),
        ("NumericType2", Valid("2", "0", quoteType: false)),
        ("NumericTypeOutOfRange7", Valid("7", "0", quoteType: false)),
        ("NumericTypeNegative", Valid("-1", "0", quoteType: false)),
        ("NumericTypeFloat", Valid("1.5", "0", quoteType: false)),
        ("NumericTypeTooLarge", Valid("2147483648", "0", quoteType: false)),
        ("NumericStringType1", Valid("1", "0")),
        ("NumericStringType2Spaced", Valid(" 2 ", "0")),
        ("NumericStateOutOfRange", Valid("Move", "7", quoteState: false)),
        ("NumericStateNegative", Valid("Move", "-1", quoteState: false)),
        ("CommaListTypes", Valid("Move, Copy", "Prepared")),
        ("UnknownType", Valid("Rename", "Prepared")),
        ("EmptyType", Valid("", "Prepared")),
        ("EmptyState", Valid("Move", "")),
        ("TypeJsonNull", "{\"Id\":\"id1\",\"Type\":null,\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("StateJsonNull", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":null,\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("TypeIsObject", "{\"Id\":\"id1\",\"Type\":{},\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("TypeIsArray", "{\"Id\":\"id1\",\"Type\":[],\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("TypeIsTrue", "{\"Id\":\"id1\",\"Type\":true,\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("MissingType", "{\"Id\":\"id1\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("MissingState", "{\"Id\":\"id1\",\"Type\":\"Move\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("LowerCasePropertyNames", "{\"id\":\"id1\",\"type\":\"Move\",\"state\":\"Prepared\",\"source\":\"C:\\\\a.jpg\",\"destination\":null,\"size\":1,\"lastWriteUtc\":\"2026-01-01T00:00:00Z\",\"timestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("DuplicateTypeValidThenUnknown", "{\"Id\":\"id1\",\"Type\":\"Move\",\"Type\":\"Rename\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("DuplicateTypeUnknownThenValid", "{\"Id\":\"id1\",\"Type\":\"Rename\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("MissingId", "{\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("EmptyId", "{\"Id\":\"\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("NullId", "{\"Id\":null,\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("MissingSource", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("NullSource", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":null,\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("MalformedTruncated", "{\"Id\":\"id1\",\"Type\":\"Move\""),
        ("MalformedTrailingGarbage", Valid("Move", "Prepared") + "garbage"),
        ("MalformedSingleBrace", "{"),
        ("NonObjectArray", "[]"),
        ("NonObjectString", "\"x\""),
        ("NonObjectNumber", "1"),
        ("NonObjectNull", "null"),
        ("NonObjectEmptyString", ""),
        ("NonObjectWhitespace", "   "),
        ("ExtraUnknownMember", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\",\"Foo\":\"bar\"}"),
        ("ErrorCodePermanentUndoPresent", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Failed\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\",\"Error\":\"boom\",\"ErrorCode\":\"PendingUnconfirmed\",\"Permanent\":true,\"Undo\":true}"),
        ("SizeAsString", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":\"123\",\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
        ("DestinationNull", "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}"),
    ];

    private static string Valid(string type, string state, bool quoteType = true, bool quoteState = true)
    {
        var typeToken = quoteType ? $"\"{type}\"" : type;
        var stateToken = quoteState ? $"\"{state}\"" : state;
        return "{\"Id\":\"id1\",\"Type\":" + typeToken + ",\"State\":" + stateToken +
            ",\"Source\":\"C:\\\\a.jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}";
    }

    public static IEnumerable<object[]> CorpusCases => Corpus.Select(c => new object[] { c.Name, c.Json });

    [Theory(DisplayName = "New single-pass parser agrees with the legacy two-pass reader for every corpus line")]
    [MemberData(nameof(CorpusCases))]
    public void TryParse_HandWrittenCorpus_MatchesLegacy(string name, string json)
    {
        var expected = LegacyJournalLineParser.TryParse(json);
        var actualFromString = JournalLineParser.TryParse(json);
        var actualFromBytes = JournalLineParser.TryParse(Encoding.UTF8.GetBytes(json));

        Assert.True(expected == actualFromString, $"[{name}] string overload disagrees with legacy for: {json}");
        Assert.True(expected == actualFromBytes, $"[{name}] byte overload disagrees with legacy for: {json}");
    }

    [Fact(DisplayName = "TryParse rejects invalid UTF-8 bytes")]
    public void TryParse_InvalidUtf8Bytes_ReturnsNull()
    {
        byte[] invalid = [0x7B, 0x22, 0x49, 0x64, 0xFF, 0xFE, 0x22]; // lone continuation bytes inside the string
        Assert.Null(JournalLineParser.TryParse(invalid));
    }

    // ---- Seeded randomized fuzz over the same field-variant space. ----
    private static readonly string[] TypeVariants =
    [
        "\"Move\"", "\"Copy\"", "\"Recycle\"", "\"delete\"", "\"DELETE\"", "\" Delete \"",
        "0", "1", "2", "3", "-1", "1.5", "2147483648", "\"1\"", "\" 2 \"", "\"Move, Copy\"",
        "\"Rename\"", "\"\"", "null", "{}", "[]", "true",
    ];
    private static readonly string[] StateVariants =
    [
        "\"Prepared\"", "\"Committed\"", "\"Failed\"", "\"Dismissed\"",
        "0", "1", "2", "3", "4", "-1", "2.5", "\"1\"", "\" 2 \"",
        "\"Bogus\"", "\"\"", "null", "{}", "[]", "false",
    ];
    private static readonly string?[] IdVariants = ["\"id1\"", "\"\"", "null", null];
    private static readonly string?[] SourceVariants = ["\"C:\\\\a.jpg\"", "null", null];
    private static readonly string[] DestinationVariants = ["\"C:\\\\b.jpg\"", "null"];
    private static readonly string[] SizeVariants = ["1", "\"123\"", "0"];
    private static readonly string[] TimestampVariants = ["\"2026-01-01T00:00:00Z\""];

    private static string BuildFuzzLine(Random rng)
    {
        var parts = new List<string>();
        if (IdVariants[rng.Next(IdVariants.Length)] is { } id) parts.Add("\"Id\":" + id);
        parts.Add("\"Type\":" + TypeVariants[rng.Next(TypeVariants.Length)]);
        if (rng.Next(6) == 0) parts.Add("\"Type\":" + TypeVariants[rng.Next(TypeVariants.Length)]); // duplicate
        parts.Add("\"State\":" + StateVariants[rng.Next(StateVariants.Length)]);
        if (rng.Next(6) == 0) parts.Add("\"State\":" + StateVariants[rng.Next(StateVariants.Length)]); // duplicate
        if (SourceVariants[rng.Next(SourceVariants.Length)] is { } source) parts.Add("\"Source\":" + source);
        parts.Add("\"Destination\":" + DestinationVariants[rng.Next(DestinationVariants.Length)]);
        parts.Add("\"Size\":" + SizeVariants[rng.Next(SizeVariants.Length)]);
        parts.Add("\"LastWriteUtc\":" + TimestampVariants[0]);
        parts.Add("\"TimestampUtc\":" + TimestampVariants[0]);
        if (rng.Next(8) == 0) parts.Add("\"Extra\":\"noise\"");

        // Shuffle field order (Fisher-Yates) to also cover "last one wins" beyond adjacent duplicates.
        for (var i = parts.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (parts[i], parts[j]) = (parts[j], parts[i]);
        }

        var line = "{" + string.Join(",", parts) + "}";

        return rng.Next(20) switch
        {
            0 => line[..(line.Length / 2)], // truncated
            1 => line + "garbage", // trailing garbage
            2 => "{", // degenerate
            3 => "", // empty
            _ => line,
        };
    }

    [Fact(DisplayName = "Seeded fuzz: new parser agrees with legacy over 3000 generated lines")]
    public void TryParse_SeededFuzz_MatchesLegacyOverManyLines()
    {
        var rng = new Random(20260928);
        for (var i = 0; i < 3000; i++)
        {
            var line = BuildFuzzLine(rng);
            var expected = LegacyJournalLineParser.TryParse(line);
            var actualFromString = JournalLineParser.TryParse(line);
            var actualFromBytes = JournalLineParser.TryParse(Encoding.UTF8.GetBytes(line));

            Assert.True(expected == actualFromString, $"[fuzz #{i}] string overload disagrees with legacy for: {line}");
            Assert.True(expected == actualFromBytes, $"[fuzz #{i}] byte overload disagrees with legacy for: {line}");
        }
    }

    // ---- OperationJournal-level: every corpus line the parser rejects must be skipped by ReadPendingOperations. ----
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");

    private static readonly string GoodLine =
        "{\"Id\":\"good\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\photos\\\\good.jpg\",\"Destination\":\"C:\\\\photos\\\\sel\\\\good.jpg\",\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}";

    public static IEnumerable<object[]> SkipCases =>
        Corpus.Where(c => JournalLineParser.TryParse(c.Json) is null && !c.Json.Contains('\n', StringComparison.Ordinal))
            .Select(c => new object[] { c.Name, c.Json });

    [Theory(DisplayName = "A journal line this parser rejects is skipped; the good Prepared line beside it still reads back")]
    [MemberData(nameof(SkipCases))]
    public void ReadPendingOperations_BadLineBesideGoodLine_OnlyGoodLineReturned(string name, string badJson)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Paths.JournalFile, badJson + "\n" + GoodLine + "\n");
        var journal = new OperationJournal(Paths, fs, new SystemClock());

        var pending = journal.ReadPendingOperations();

        var only = Assert.Single(pending);
        Assert.Equal("good", only.Id);
        _ = name; // xUnit theory name only
    }
}
