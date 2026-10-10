using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Parses one journal line with ONE JSON pass (perf: the journal is re-read in full by startup reconcile, the Recovery
/// window, Dismiss and every conditional append). Earlier each line was parsed twice: a <see cref="JsonDocument"/> to check
/// the enums, then <see cref="JsonSerializer"/> again for the record.
/// <para>Semantics are exactly those of that two-pass reader: a line is accepted only when it is a JSON object whose
/// <c>Type</c> and <c>State</c> are present and recognized (a string that <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
/// accepts as a defined member, the "Delete" alias for Type, or a defined number), whose <c>Id</c> is non-empty and whose
/// <c>Source</c> is not blank (a blank <c>Destination</c> is read as null). Malformed JSON is rejected. The lenient enum converters would otherwise map an unknown or missing
/// Type/State to the first member (Move/Prepared) and invent a pending move.</para>
/// <para>How: options-level converters (which take precedence over the enums' <c>[JsonConverter]</c> attribute) record, per
/// thread, whether the last Type/State token they read was recognized, and delegate the value itself to
/// <see cref="LenientEnumConverter{T}"/>. A missing member never reaches its converter, so it stays "not seen". Duplicate
/// members: the last occurrence wins for both the check and the value, as it did for <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
/// and the deserializer.</para>
/// </summary>
internal static class JournalLineParser
{
    // Tri-state per thread: 0 = member not read (missing), 1 = recognized, 2 = present but not recognized.
    [ThreadStatic] private static int t_type;
    [ThreadStatic] private static int t_state;

    public static JournalEntry? TryParse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        t_type = 0;
        t_state = 0;
        try
        {
            return Accept(JsonSerializer.Deserialize(line, JournalLineJsonContext.Default.JournalEntry));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Same as <see cref="TryParse(string)"/> for a UTF-8 line (no terminator). Invalid UTF-8 is rejected.</summary>
    public static JournalEntry? TryParse(ReadOnlySpan<byte> utf8Line)
    {
        t_type = 0;
        t_state = 0;
        try
        {
            return Accept(JsonSerializer.Deserialize(utf8Line, JournalLineJsonContext.Default.JournalEntry));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JournalEntry? Accept(JournalEntry? entry) =>
        entry is not null && t_type == 1 && t_state == 1 && !string.IsNullOrEmpty(entry.Id) && IsUsablePath(entry.Source)
            && HasValidGroupMembers(entry)
            ? NormalizeDestination(entry)
            : null;

    // A blank/whitespace/NUL path would make every later file check throw ArgumentException out of reconcile/Recovery.
    private static bool IsUsablePath(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\0', StringComparison.Ordinal);

    // A blank Destination carries no information: it is the same as no Destination (null), as the readers already assume.
    private static JournalEntry NormalizeDestination(JournalEntry entry) =>
        entry.Destination is not null && !IsUsablePath(entry.Destination) ? entry with { Destination = null } : entry;

    // A group member without a usable Source (JSON null element, missing or blank Source) would make every later file check
    // throw ArgumentException out of reconcile/Recovery, so such a line is dropped like any other malformed line.
    // A NUL in a member Source or Destination (W2-FA-07) is the same corruption the entry-level paths reject: the file checks of the
    // downgrade repair would throw ArgumentException (a blank Destination stays accepted, readers treat it as "none").
    private static bool HasValidGroupMembers(JournalEntry entry) =>
        entry.GroupMembers is null || entry.GroupMembers.All(member => member is not null && IsUsablePath(member.Source)
            && (member.Destination is null || !member.Destination.Contains('\0', StringComparison.Ordinal)));

    private static bool IsKnown<T>(ref Utf8JsonReader reader, string? alias) where T : struct, Enum => reader.TokenType switch
    {
        // W2-FA-01: Enum.TryParse also accepts a comma list ("Prepared,Committed") and ORs the members, whereas
        // LenientEnumConverter reads only ONE name or number and would silently return the first member (Move/Prepared).
        JsonTokenType.String => reader.GetString() is { } text && !text.Contains(',', StringComparison.Ordinal)
            && (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                || (alias is not null && string.Equals(text.Trim(), alias, StringComparison.OrdinalIgnoreCase))),
        JsonTokenType.Number => reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number),
        _ => false,
    };

    /// <summary>Records whether the Type token was recognized (WP-11: a concrete, parameterless converter so the source-generated
    /// <see cref="JournalLineJsonContext"/> can list it).</summary>
    internal sealed class TypeRecordingConverter : JsonConverter<FileOperationType>
    {
        private readonly LenientEnumConverter<FileOperationType> _inner = new();

        // A null token must reach Read (recorded as not recognized), exactly like JsonValueKind.Null failed the old check.
        public override bool HandleNull => true;

        public override FileOperationType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            t_type = IsKnown<FileOperationType>(ref reader, "Delete") ? 1 : 2;
            return _inner.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, FileOperationType value, JsonSerializerOptions options) =>
            _inner.Write(writer, value, options);
    }

    /// <summary>Records whether the State token was recognized (see <see cref="TypeRecordingConverter"/>).</summary>
    internal sealed class StateRecordingConverter : JsonConverter<JournalState>
    {
        private readonly LenientEnumConverter<JournalState> _inner = new();

        // A null token must reach Read (recorded as not recognized), exactly like JsonValueKind.Null failed the old check.
        public override bool HandleNull => true;

        public override JournalState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            t_state = IsKnown<JournalState>(ref reader, null) ? 1 : 2;
            return _inner.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, JournalState value, JsonSerializerOptions options) =>
            _inner.Write(writer, value, options);
    }
}
