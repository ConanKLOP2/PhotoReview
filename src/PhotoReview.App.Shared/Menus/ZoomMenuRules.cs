using PhotoReview.Core.Settings;

namespace PhotoReview.App.Menus;

/// <summary>Quy tắc zoom của menu chuột phải, sao từ MainWindow/MainWindowHelpers (WP-18; MainWindow WPF không đổi).</summary>
internal static class ZoomMenuRules
{
    /// <summary>Các mức zoom có sẵn của submenu Zoom (sao từ <c>MainWindow.ZoomPresets</c>).</summary>
    internal static IReadOnlyList<int> PresetPercents { get; } = [50, 70, 100, 150, 200, 300, 400];

    /// <summary>Mức zoom (%) mà "Set current zoom as click level" sẽ ghi; null khi đang Fit. Sao từ <c>MainWindowHelpers</c>.</summary>
    internal static int? CalculateClickLevelFromEffectiveZoom(double? effectiveZoom) =>
        effectiveZoom is { } zoom && double.IsFinite(zoom)
            ? Math.Clamp((int)Math.Round(zoom * 100, MidpointRounding.AwayFromZero), AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent)
            : null;
}