using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Q-R18: how many PhotoReview processes may run for one Windows user session. Read once at startup, before the
/// instance lock is taken, so a change takes effect at the next start.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<InstanceMode>))]
public enum InstanceMode
{
    /// <summary>Default: one app-wide lock and forward pipe. Every later launch hands its path to the running window.</summary>
    SingleWindow = 0,

    /// <summary>One window per folder; the lock and forward pipe follow the folder the window currently shows.</summary>
    PerFolder = 1,
}
