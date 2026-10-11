using PhotoReview.Core.Localization;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: <c>ToolbarPanel</c> của <c>MainWindow.xaml</c> - góc trên trái: bốn nút theo đúng thứ tự/độ đệm của XAML
/// (📂 cỡ 16 Padding 8,2; ⚙ Padding 8,4 Margin 5,0,0,0; "Vừa khung" cùng kiểu; ⋮ ToggleButton cỡ 18, 34 x 32, Margin 5,0,0,0),
/// nền <c>Dark.Overlay</c>, Margin 8, Padding 6,4, CornerRadius 4. Nút lấy style của <c>MainWindow.xaml</c>:
/// nền <c>Dark.Input</c>, viền <c>Dark.BorderToolbar</c> 1 DIP, chữ <c>Dark.TextBright</c>, không bo góc.
/// Chuỗi (nội dung nút Fit, tooltip, AutomationName) lấy từ <see cref="Tr"/> qua <see cref="ApplyLocalization"/>, gọi lại khi
/// ngôn ngữ đổi.
/// </summary>
internal sealed class ToolbarPanel : PanelElement
{
    public const string PanelId = "ToolbarPanel";
    public const string OpenFolderId = "OpenFolderButton";
    public const string SettingsId = "SettingsButton";
    public const string FitId = "FitButton";
    public const string ToolsId = "ToolsButton";

    private const string Family = "Segoe UI";

    private int _focusedIndex = -1;

    public ToolbarPanel()
        : base(PanelId)
    {
        HorizontalAlignment = OverlayHorizontalAlignment.Left;
        VerticalAlignment = OverlayVerticalAlignment.Top;
        Margin = new OverlayThickness(8);
        Padding = new OverlayThickness(6, 4);
        CornerRadius = 4;
        Orientation = OverlayOrientation.Horizontal;

        OpenFolder = CreateButton(OpenFolderId, "\U0001F4C2", 16, new OverlayThickness(8, 2), OverlayThickness.Zero);
        Settings = CreateButton(SettingsId, "⚙", 12, new OverlayThickness(8, 4), new OverlayThickness(5, 0, 0, 0));
        Fit = CreateButton(FitId, string.Empty, 12, new OverlayThickness(8, 4), new OverlayThickness(5, 0, 0, 0));
        Tools = CreateButton(ToolsId, "⋮", 18, OverlayThickness.Zero, new OverlayThickness(5, 0, 0, 0));
        Tools.FixedWidth = 34;
        Tools.FixedHeight = 32;
        Buttons = [OpenFolder, Settings, Fit, Tools];
        foreach (ButtonElement button in Buttons)
        {
            Add(button);
        }

        ApplyLocalization();
    }

    public ButtonElement OpenFolder { get; }

    public ButtonElement Settings { get; }

    public ButtonElement Fit { get; }

    public ButtonElement Tools { get; }

    /// <summary>Theo thứ tự Tab.</summary>
    public IReadOnlyList<ButtonElement> Buttons { get; }

    /// <summary>Chỉ số nút đang giữ bàn phím; -1 = không nút nào (bàn phím ở ảnh).</summary>
    public int FocusedIndex => _focusedIndex;

    /// <summary><c>ToolbarPanel.IsKeyboardFocusWithin</c>.</summary>
    public bool HasKeyboardFocus => _focusedIndex >= 0;

    public ButtonElement? FocusedButton => _focusedIndex >= 0 ? Buttons[_focusedIndex] : null;

    /// <summary>Chữ + tooltip + tên tự động hoá theo ngôn ngữ hiện hành.</summary>
    internal void ApplyLocalization()
    {
        OpenFolder.ToolTip = Tr.MainToolbarOpenFolderToolTip;
        OpenFolder.AutomationName = Tr.MainToolbarOpenFolderAutomationName;
        Settings.ToolTip = Tr.MainToolbarSettingsToolTip;
        Settings.AutomationName = Tr.MainToolbarSettingsAutomationName;
        Fit.Content = Tr.MainToolbarFit;
        Fit.ToolTip = Tr.MainToolbarFitToolTip;
        Fit.AutomationName = Tr.MainToolbarFitAutomationName;
        Tools.ToolTip = Tr.MainToolbarToolsToolTip;
        Tools.AutomationName = Tr.MainToolbarToolsAutomationName;
    }

    /// <summary>
    /// Tab / Shift+Tab: đưa bàn phím tới nút kế (hoặc ra khỏi toolbar sau nút cuối/trước nút đầu). Trả true nếu bàn phím vẫn
    /// nằm trong toolbar.
    /// </summary>
    internal bool MoveFocus(bool forward)
    {
        int next = _focusedIndex < 0 ? (forward ? 0 : Buttons.Count - 1) : _focusedIndex + (forward ? 1 : -1);
        SetFocus(next >= 0 && next < Buttons.Count ? next : -1);
        return _focusedIndex >= 0;
    }

    internal void ClearFocus() => SetFocus(-1);

    private void SetFocus(int index)
    {
        _focusedIndex = index;
        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].IsFocused = i == index;
        }
    }

    private static ButtonElement CreateButton(string id, string content, double size, OverlayThickness padding, OverlayThickness margin) =>
        new(id)
        {
            Content = content,
            Style = new TextStyle(Family, size),
            Padding = padding,
            Margin = margin,
            Background = DarkPalette.Get("Dark.Input"),
            BorderColor = DarkPalette.Get("Dark.BorderToolbar"),
            CornerRadius = 0,
        };
}
