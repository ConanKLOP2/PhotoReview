using System.Text.Json.Serialization;

namespace PhotoReview.Core.Session;

/// <summary>
/// WP-11: compile-time System.Text.Json metadata for the session file (<see cref="SessionStore"/>). Options are the ones
/// the reflection-based serializer used (defaults + WriteIndented), so the saved bytes and the accepted input are unchanged.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}
