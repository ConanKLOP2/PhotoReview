using PhotoReview.Core.Localization;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>WP-23: <c>FolderInfoPanel</c> - góc dưới phải, MaxWidth 640, một dòng cắt "…" (thư mục hiện tại + hai bên).</summary>
internal sealed class FolderInfoPanel : PanelElement
{
    public const string PanelId = "FolderInfoPanel";
    public const string TextId = "FolderInfoText";

    public FolderInfoPanel()
        : base(PanelId)
    {
        HorizontalAlignment = OverlayHorizontalAlignment.Right;
        VerticalAlignment = OverlayVerticalAlignment.Bottom;
        Margin = new OverlayThickness(8);
        Padding = new OverlayThickness(6, 4);
        CornerRadius = 4;
        MaxWidth = 640;
        Text = new TextElement(TextId) { Trimming = TextTrimming.CharacterEllipsis };
        Add(Text);
        ApplyLocalization();
    }

    public TextElement Text { get; }

    /// <summary><c>AutomationProperties.Name</c>.</summary>
    public string AutomationName { get; private set; } = string.Empty;

    internal void Apply(OverlayModel model)
    {
        IsVisible = model.FolderInfoVisible;
        Text.Text = model.FolderInfoText;
        Text.Style = new TextStyle("Segoe UI", model.FontSize);
    }

    internal void ApplyLocalization() => AutomationName = Tr.MainFolderInfoAutomationName;
}
