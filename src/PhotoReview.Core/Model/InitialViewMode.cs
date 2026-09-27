using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chế độ hiển thị ban đầu khi mở một ảnh.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<InitialViewMode>))]
public enum InitialViewMode
{
    Fit = 0,

    [JsonAlias("100%")]
    Percent100 = 1,

    [JsonAlias("200%")]
    Percent200 = 2,

    /// <summary>
    /// Legacy value kept only so old configs still parse; the Settings combo no longer offers it and
    /// <see cref="SettingsNormalizer"/> migrates a loaded value of this to <see cref="Percent200"/>.
    /// </summary>
    [JsonAlias("400%")]
    Percent400 = 3,

    /// <summary>Fills the viewport width exactly; vertical placement follows <see cref="FitWidthAnchor"/> (PR-B).</summary>
    [JsonAlias("fit-width")]
    FitWidth = 4,

    /// <summary>Fills the viewport height exactly, centred horizontally and vertically (PR-B).</summary>
    [JsonAlias("fit-height")]
    FitHeight = 5,

    /// <summary>Zooms straight to <see cref="AppSettings.ClickZoomPercent"/>, centred in the viewport (PR-B).</summary>
    [JsonAlias("click-zoom")]
    ClickZoomLevel = 6
}
