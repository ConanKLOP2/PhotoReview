using System.Text.Json.Serialization;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// WP-11: compile-time JSON metadata for one journal line, written by <see cref="OperationJournal"/> with default options
/// (enums go through their own <c>[JsonConverter]</c>, null optional members are omitted by the record's attributes).
/// </summary>
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext
{
}

/// <summary>
/// WP-11: the same metadata for READING a journal line in <see cref="JournalLineParser"/>. The two recording converters
/// (options-level, so they take precedence over the enums' attribute) are listed here instead of being passed to a
/// reflection-based <c>JsonSerializerOptions</c>.
/// </summary>
[JsonSourceGenerationOptions(Converters = new[] { typeof(JournalLineParser.TypeRecordingConverter), typeof(JournalLineParser.StateRecordingConverter) })]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalLineJsonContext : JsonSerializerContext
{
}
