using PhotoReview.Core.Localization;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: <c>StatusPanel</c> của <c>MainWindow.xaml</c> - góc dưới trái: dòng trạng thái (StatusText), dòng EXIF
/// (ExifInfoText, nhỏ hơn 1, Margin 0,2,0,0, màu phụ) và hàng cảnh báo tệp bị bỏ qua (chữ vàng cỡ 13 + nút "Xem danh sách").
/// Margin 8, Padding 6,4, CornerRadius 4, hai dòng chữ cắt "…".
/// </summary>
internal sealed class StatusPanel : PanelElement
{
    public const string PanelId = "StatusPanel";
    public const string StatusTextId = "StatusText";
    public const string ExifTextId = "ExifInfoText";
    public const string SkippedRowId = "SkippedRow";
    public const string SkippedViewListId = "SkippedViewListButton";

    private const string Family = "Segoe UI";

    public StatusPanel()
        : base(PanelId)
    {
        HorizontalAlignment = OverlayHorizontalAlignment.Left;
        VerticalAlignment = OverlayVerticalAlignment.Bottom;
        Margin = new OverlayThickness(8);
        Padding = new OverlayThickness(6, 4);
        CornerRadius = 4;
        Status = new TextElement(StatusTextId) { Trimming = TextTrimming.CharacterEllipsis };
        Exif = new TextElement(ExifTextId)
        {
            Trimming = TextTrimming.CharacterEllipsis,
            Margin = new OverlayThickness(0, 2, 0, 0),
            Color = DarkPalette.TextSecondary,
        };
        SkippedWarning = new TextElement("SkippedWarningText")
        {
            Style = new TextStyle(Family, 13),
            Color = DarkPalette.Warning,
            VerticalAlignment = OverlayVerticalAlignment.Center,
        };
        ViewList = new ButtonElement(SkippedViewListId)
        {
            Padding = new OverlayThickness(8, 2),
            Margin = new OverlayThickness(8, 0, 0, 0),
            Background = DarkPalette.Get("Dark.Input"),
            BorderColor = DarkPalette.Get("Dark.BorderToolbar"),
            CornerRadius = 0,
            VerticalAlignment = OverlayVerticalAlignment.Center,
        };
        SkippedRow = new PanelElement(SkippedRowId)
        {
            Orientation = OverlayOrientation.Horizontal,
            Margin = new OverlayThickness(0, 4, 0, 0),
            Background = new ColorF(0, 0, 0, 0),
        };
        SkippedRow.Add(SkippedWarning).Add(ViewList);
        Add(Status).Add(Exif).Add(SkippedRow);
        ApplyLocalization();
    }

    public TextElement Status { get; }

    public TextElement Exif { get; }

    public TextElement SkippedWarning { get; }

    public ButtonElement ViewList { get; }

    public PanelElement SkippedRow { get; }

    /// <summary>Đặt chữ, cỡ chữ và hiện/ẩn từng phần theo model (như binding XAML).</summary>
    internal void Apply(OverlayModel model)
    {
        IsVisible = model.StatusPanelVisible;
        Status.IsVisible = model.FileInfoVisible;
        Status.Text = model.StatusText;
        Status.Style = new TextStyle(Family, model.FontSize);
        Exif.IsVisible = model.ExifVisible;
        Exif.Text = model.ExifText;
        Exif.Style = new TextStyle(Family, model.ExifFontSize);
        SkippedRow.IsVisible = model.HasSkippedEntries;
        SkippedWarning.Text = model.SkippedWarningText;
    }

    /// <summary>Chữ địa phương hoá của nút trong panel (đổi khi <c>Localizer.CurrentChanged</c>).</summary>
    internal void ApplyLocalization()
    {
        ViewList.Content = Tr.MainSkippedViewList;
        ViewList.AutomationName = Tr.MainSkippedViewListAutomationName;
    }
}
