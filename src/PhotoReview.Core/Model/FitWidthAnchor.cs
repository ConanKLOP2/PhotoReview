using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Vertical anchor used by <see cref="InitialViewMode.FitWidth"/> (and the FitWidth shortcut when the mouse is not
/// over the viewport): which image point is kept at the viewport centre after fitting the width (PR-B contract
/// change: this replaced a fixed top-third rule with a user setting).
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<FitWidthAnchor>))]
public enum FitWidthAnchor
{
    /// <summary>Image point (0.5, 0.5) at the viewport centre. Default.</summary>
    Centre = 0,

    /// <summary>Image point (0.5, 1/3) at the viewport centre -- portraits usually have the face there.</summary>
    TopThird = 1
}
