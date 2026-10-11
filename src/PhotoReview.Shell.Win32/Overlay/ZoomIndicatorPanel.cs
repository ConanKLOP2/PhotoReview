using PhotoReview.Core.Localization;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>WP-23: <c>ZoomIndicatorPanel</c> - góc trên phải: phần trăm zoom hiện tại (không cắt chữ).</summary>
internal sealed class ZoomIndicatorPanel : PanelElement
{
    public const string PanelId = "ZoomIndicatorPanel";
    public const string TextId = "ZoomIndicatorText";

    public ZoomIndicatorPanel()
        : base(PanelId)
    {
        HorizontalAlignment = OverlayHorizontalAlignment.Right;
        VerticalAlignment = OverlayVerticalAlignment.Top;
        Margin = new OverlayThickness(8);
        Padding = new OverlayThickness(6, 4);
        CornerRadius = 4;
        Text = new TextElement(TextId);
        Add(Text);
        ApplyLocalization();
    }

    public TextElement Text { get; }

    /// <summary><c>AutomationProperties.Name</c>.</summary>
    public string AutomationName { get; private set; } = string.Empty;

    internal void Apply(OverlayModel model)
    {
        IsVisible = model.ZoomIndicatorVisible;
        Text.Text = model.ZoomIndicatorText;
        Text.Style = new TextStyle("Segoe UI", model.FontSize);
    }

    internal void ApplyLocalization() => AutomationName = Tr.MainZoomIndicatorAutomationName;
}
