using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// What point stays put when +/- (keyboard zoom in/out), 100 % or the click-zoom shortcut/menu changes the zoom
/// level. Mouse wheel and click-to-zoom already anchor at the cursor unconditionally; this setting extends the
/// same anchoring to the keyboard/menu paths that used to always target the viewport centre.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<KeyboardZoomAnchor>))]
public enum KeyboardZoomAnchor
{
    /// <summary>Anchor at the mouse position when it is over the image viewport; the viewport centre otherwise.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Pointer' means the mouse pointer here, not the .NET/C pointer type; renaming would break the PR-A contract shared with parallel branches (see the PR description).")]
    Pointer = 0,

    /// <summary>Always anchor at the viewport centre, whatever the mouse position.</summary>
    ViewportCentre = 1,
}
