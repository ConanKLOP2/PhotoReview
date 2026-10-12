using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: <c>CapturePairBadge</c> - giữa phía trên, lệch xuống 44 (dưới toolbar): thành viên JPEG/RAW của bộ ảnh đang xem;
/// tooltip nói cách chuyển sang thành viên kia.
/// </summary>
internal sealed class CapturePairBadge : PanelElement
{
    public const string PanelId = "CapturePairBadge";
    public const string TextId = "CapturePairText";

    public CapturePairBadge()
        : base(PanelId)
    {
        HorizontalAlignment = OverlayHorizontalAlignment.Center;
        VerticalAlignment = OverlayVerticalAlignment.Top;
        Margin = new OverlayThickness(8, 44, 8, 8);
        Padding = new OverlayThickness(6, 4);
        CornerRadius = 4;
        Text = new TextElement(TextId);
        Add(Text);
    }

    public TextElement Text { get; }

    /// <summary>Chữ tooltip hiện hành (rỗng khi ẩn).</summary>
    public string ToolTip { get; private set; } = string.Empty;

    internal void Apply(OverlayModel model)
    {
        IsVisible = model.CapturePairVisible;
        Text.Text = model.CapturePairText;
        Text.Style = new TextStyle("Segoe UI", model.FontSize);
        ToolTip = model.CapturePairToolTip;
    }
}
