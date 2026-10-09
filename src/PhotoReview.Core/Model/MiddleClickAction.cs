using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>What clicking the middle mouse button (wheel press) over the main image does. Each value maps to an existing command.</summary>
[JsonConverter(typeof(LenientEnumConverter<MiddleClickAction>))]
public enum MiddleClickAction
{
    /// <summary>The middle button does nothing.</summary>
    None = 0,

    /// <summary>The click-to-zoom toggle (Fit &lt;-&gt; <c>ClickZoomPercent</c>), like the click-zoom shortcut.</summary>
    ClickZoom = 1,

    /// <summary>Zoom to 100 % = one source pixel per device pixel (ADR 0008), like the 100 % shortcut. Default.</summary>
    ActualSize = 2,

    /// <summary>Fit the whole image in the view (the Fit toggle shortcut).</summary>
    Fit = 3,

    /// <summary>Fit width with the "Fit width shows 1" anchor (<c>AppSettings.FitWidthAnchor</c>).</summary>
    FitWidth = 4,

    /// <summary>Fit width with the "Fit width shows 2" anchor (<c>AppSettings.FitWidthAnchor2</c>).</summary>
    FitWidth2 = 5,

    /// <summary>Previous image.</summary>
    PreviousImage = 6,

    /// <summary>Next image.</summary>
    NextImage = 7,

    /// <summary>Previous sibling folder.</summary>
    PreviousFolder = 8,

    /// <summary>Next sibling folder.</summary>
    NextFolder = 9,

    /// <summary>The "Open folder…" picker.</summary>
    OpenFolder = 10,
}
