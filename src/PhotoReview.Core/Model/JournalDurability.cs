using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// How hard the operation journal pushes each record to disk (ADR 0007, IO03). Both modes keep the same
/// invariants: Prepared before the file mutation, Committed only after it, one complete JSON line per record.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<JournalDurability>))]
public enum JournalDurability
{
    /// <summary>Default (J-C): plain <c>Flush()</c> into the OS cache; survives a process crash, not a power loss.</summary>
    Fast = 0,

    /// <summary>J-D: <c>WriteThrough</c> + <c>Flush(true)</c> per record, journal and mutation off the UI thread.</summary>
    PowerLossSafe = 1,
}
