using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Settings;

/// <summary>
/// perf(startup): compile-time System.Text.Json metadata for config.json. The reflection-based
/// serializer built AppSettings' metadata on first use -- ~120 ms on the UI thread at every
/// startup (reflection + IL-emitted accessors, JIT'ed even in a ReadyToRun publish). Options match
/// what <see cref="SettingsStore"/> used before (defaults + WriteIndented), so the file format and
/// the lenient enum handling (type-level <c>[JsonConverter]</c> on the enums) are unchanged.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
