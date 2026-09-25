using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>What the mouse wheel does over the main image. Ctrl+wheel always zooms at the cursor.</summary>
[JsonConverter(typeof(LenientEnumConverter<MouseWheelAction>))]
public enum MouseWheelAction
{
    /// <summary>Default: each wheel event zooms one step, anchored at the cursor.</summary>
    Zoom = 0,

    /// <summary>Wheel down = next image, wheel up = previous image (one image per notch of 120).</summary>
    Navigate = 1,
}
