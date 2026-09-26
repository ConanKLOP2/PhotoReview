using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// How the kinetic glide (after a drag-pan is released) is timed against the display. WPF on some machines delivers
/// frames irregularly (e.g. a 240 Hz panel next to a 60 Hz monitor); <see cref="Predict"/> keeps the motion true to the
/// refreshes the frames are actually seen on.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<KineticGlideSmoothing>))]
public enum KineticGlideSmoothing
{
    /// <summary>Each frame advances by the time between WPF's RenderingTime stamps (the original behaviour).</summary>
    Off = 0,

    /// <summary>
    /// Each frame advances to the refresh (vblank) of the window's monitor it is expected to be shown on, at that
    /// monitor's own rate (60, 75, 144, 240 Hz ...): a late frame shows the position for when it is seen, and
    /// several frames within one refresh move once.
    /// </summary>
    Predict = 1,
}
