using System.Numerics;
using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

internal enum D2DCursorKind
{
    Arrow,
    IBeam,
}

/// <summary>
/// SPIKE (WP-35 D2D Settings): TOÃ€N Bá»˜ trang Settings lÃ  Má»˜T bá» máº·t Direct2D. Lá»›p nÃ y khÃ´ng cÃ³ HWND: bá»‘ cá»¥c (px nguyÃªn), váº½ vÃ o
/// <see cref="D2DDrawContext"/>, cuá»™n (offset px + váº½ láº¡i cáº£ khung + thanh cuá»™n tá»± váº½), chuá»™t, bÃ n phÃ­m (Tab/mÅ©i tÃªn/Space/focus ring),
/// Ã´ nháº­p tá»± váº½, combobox tá»± váº½, nÃºt, cÃ¢y trá»£ nÄƒng. Cá»­a sá»• (<see cref="D2DSettingsWindow"/>) chá»‰ chuyá»ƒn thÃ´ng Ä‘iá»‡p vÃ o Ä‘Ã¢y.
/// <para>
/// Toáº¡ Ä‘á»™: má»i sá»‘ lÃ  px thiáº¿t bá»‹. Khung váº½ Ä‘áº·t <c>PushTransform(scale(1/dpiScale))</c> nÃªn toáº¡ Ä‘á»™ px khá»›p pixel (cá»¡ chá»¯ cÅ©ng tÃ­nh px).
/// Váº½ láº¡i: Má»–I khung xoÃ¡ vÃ  váº½ cáº£ cá»­a sá»• (khÃ´ng cÃ³ vÃ¹ng báº©n, khÃ´ng ScrollWindow) - nÃªn khÃ´ng thá»ƒ cÃ²n hÃ¬nh cÅ© khi cuá»™n.
/// </para>
/// </summary>
internal sealed class D2DSettingsView
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkPrior = 0x21;
    private const int VkNext = 0x22;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkDelete = 0x2E;
    private const int VkF4 = 0x73;

    private readonly DWriteTextRenderer _text;
    private readonly ID2DClipboard _clipboard;
    private readonly List<D2DWidget> _content = [];
    private readonly List<D2DButtonWidget> _footer = [];
    private D2DSettingsPalette _palette;
    private double _scale = 1;
    private int _clientWidth;
    private int _clientHeight;
    private int _lineHeight;
    private int _fieldHeight;
    private PxRect _viewport;
    private PxRect _scrollTrack;
    private PxRect _header;
    private PxRect _footerRect;
    private D2DWidget? _focused;
    private D2DWidget? _hot;
    private D2DWidget? _pressed;
    private D2DComboWidget? _openCombo;
    private int _popupHot = -1;
    private bool _draggingThumb;
    private int _thumbGrabOffset;
    private bool _draggingSlider;
    private bool _draggingSelection;
    private D2DFieldWidget? _selectionField;
    private bool _caretVisible = true;
    private bool _windowFocused = true;

    public D2DSettingsView(DWriteTextRenderer text, ID2DClipboard clipboard, D2DSettingsPalette palette, D2DEditMode mode, Func<string> title)
    {
        _text = text;
        _clipboard = clipboard;
        _palette = palette;
        Mode = mode;
        Title = title;
    }

    public D2DEditMode Mode { get; }

    public Func<string> Title { get; set; }

    public D2DSettingsPalette Palette
    {
        get => _palette;
        set => _palette = value;
    }

    public IReadOnlyList<D2DWidget> Content => _content;

    public IReadOnlyList<D2DButtonWidget> Footer => _footer;

    public double Scale => _scale;

    public int ClientWidth => _clientWidth;

    public int ClientHeight => _clientHeight;

    public PxRect Viewport => _viewport;

    public int ContentHeightPx { get; private set; }

    public int OffsetPx { get; private set; }

    public int MaxOffsetPx => Math.Max(0, ContentHeightPx - _viewport.Height);

    public D2DWidget? Focused => _focused;

    public bool PopupOpen => _openCombo is not null;

    public bool CaretVisible => _caretVisible;

    /// <summary>Sá»‘ láº§n váº½ (cháº©n Ä‘oÃ¡n/Ä‘o).</summary>
    public long FrameCount { get; private set; }

    /// <summary>CÃ³ thay Ä‘á»•i cáº§n váº½ láº¡i (cá»­a sá»• xoÃ¡ cá» sau khi váº½).</summary>
    public bool NeedsRedraw { get; set; } = true;

    /// <summary>Pháº§n tá»­ ná»™i dung Ä‘á»•i giÃ¡ trá»‹ (tick, sá»‘, combobox, thanh trÆ°á»£t).</summary>
    public event Action<D2DWidget>? Changed;

    /// <summary>NÃºt á»Ÿ chÃ¢n Ä‘Æ°á»£c báº¥m.</summary>
    public event Action<D2DButtonWidget>? ButtonInvoked;

    /// <summary>Focus nháº£y sang pháº§n tá»­ khÃ¡c (cá»­a sá»• dÃ¹ng Ä‘á»ƒ chuyá»ƒn focus HWND tháº­t á»Ÿ cháº¿ Ä‘á»™ lai).</summary>
    public event Action<D2DWidget?>? FocusMoved;

    public void AddTop(D2DWidget widget) => _content.Add(widget);

    public void AddFooter(D2DButtonWidget button) => _footer.Add(button);

    /// <summary>Má»i pháº§n tá»­ (lÃ¡ + nhÃ³m), theo thá»© tá»± khai bÃ¡o.</summary>
    public IEnumerable<D2DWidget> AllWidgets()
    {
        foreach (var widget in _content)
        {
            foreach (var inner in Flatten(widget))
            {
                yield return inner;
            }
        }

        foreach (var button in _footer)
        {
            yield return button;
        }
    }

    public D2DWidget? Find(string id) => AllWidgets().FirstOrDefault(w => w.Id == id);

    private static IEnumerable<D2DWidget> Flatten(D2DWidget widget)
    {
        yield return widget;
        switch (widget)
        {
            case D2DGroupWidget group:
                foreach (var child in group.Children)
                {
                    foreach (var inner in Flatten(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case D2DCheckFlowWidget flow:
                foreach (var check in flow.Checks)
                {
                    yield return check;
                }

                break;
        }
    }

    // ---- bá»‘ cá»¥c ---------------------------------------------------------------------------------------------------------

    private int S(double dip) => (int)Math.Round(dip * _scale);

    private static readonly TextTrimming NoTrim = TextTrimming.None;

    private TextStyle Style(bool bold = false, double sizeDip = 12) => new("Segoe UI", sizeDip * _scale, bold);

    private ITextLayout LayoutText(string text, double maxWidthPx, bool bold = false, double sizeDip = 12)
        => _text.CreateLayout(text, Style(bold, sizeDip), maxWidthPx, NoTrim);

    private int TextHeight(string text, int widthPx)
    {
        using var layout = LayoutText(text.Length == 0 ? " " : text, Math.Max(1, widthPx));
        return (int)Math.Ceiling(layout.Size.Height);
    }

    private double MeasureWidth(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        using var layout = LayoutText(text, double.PositiveInfinity);
        return layout.Size.Width;
    }

    /// <summary>TÃ­nh láº¡i bá»‘ cá»¥c cho cá»¡ client vÃ  DPI. Giá»¯ offset (káº¹p láº¡i), giá»¯ focus.</summary>
    public void Layout(int clientWidth, int clientHeight, double scale)
    {
        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _scale = scale;
        using (var probe = LayoutText("Ag", double.PositiveInfinity))
        {
            _lineHeight = (int)Math.Ceiling(probe.Size.Height);
        }

        _fieldHeight = Math.Max(S(24), _lineHeight + S(8));
        var headerH = S(46);
        var footerH = S(54);
        var scrollW = S(15);
        _header = new PxRect(0, 0, clientWidth, headerH);
        _footerRect = new PxRect(0, Math.Max(headerH, clientHeight - footerH), clientWidth, Math.Min(footerH, Math.Max(0, clientHeight - headerH)));
        _viewport = new PxRect(0, headerH, Math.Max(S(120), clientWidth - scrollW), Math.Max(0, _footerRect.Y - headerH));
        _scrollTrack = new PxRect(_viewport.Right, headerH, Math.Max(0, clientWidth - _viewport.Right), _viewport.Height);

        var margin = S(14);
        var innerWidth = Math.Max(S(160), _viewport.Width - (2 * margin));
        var y = S(8);
        foreach (var widget in _content)
        {
            y += Place(widget, margin, y, innerWidth);
        }

        ContentHeightPx = y + S(6);
        OffsetPx = Math.Clamp(OffsetPx, 0, MaxOffsetPx);

        // NÃºt chÃ¢n: cÄƒn pháº£i, cÃ¹ng chiá»u cao.
        var gap = S(8);
        var x = clientWidth - S(14);
        var buttonH = _fieldHeight;
        var by = _footerRect.Y + ((_footerRect.Height - buttonH) / 2);
        for (var i = _footer.Count - 1; i >= 0; i--)
        {
            var w = Math.Max(S(75), (int)Math.Ceiling(MeasureWidth(_footer[i].Name)) + S(24));
            x -= w;
            _footer[i].Bounds = new PxRect(x, by, w, buttonH);
            x -= gap;
        }

        NeedsRedraw = true;
    }

    private int LabelColumn(int width) => Math.Clamp(width * 38 / 100, S(120), S(210));

    private int Place(D2DWidget widget, int x, int y, int width)
    {
        if (!widget.Visible)
        {
            widget.Bounds = new PxRect(x, y, 0, 0);
            return 0;
        }

        switch (widget)
        {
            case D2DGroupWidget group:
            {
                var pad = S(12);
                var cy = y + _lineHeight + S(8);
                foreach (var child in group.Children)
                {
                    cy += Place(child, x + pad, cy, width - (2 * pad));
                }

                cy += S(4);
                group.Bounds = new PxRect(x, y, width, cy - y);
                return cy - y + S(10);
            }

            case D2DCheckWidget check:
            {
                var indent = S(check.IndentDip);
                var box = S(14);
                var gap = S(6);
                var textWidth = Math.Max(S(40), width - indent - box - gap);
                var h = Math.Max(box, TextHeight(check.Name, textWidth)) + S(2);
                check.Bounds = new PxRect(x + indent, y, width - indent, h);
                return h + S(6);
            }

            case D2DNoteWidget note:
            {
                var indent = note.Aligned ? LabelColumn(width) + S(9) : 0;
                var textWidth = Math.Max(S(60), width - indent);
                var h = TextHeight(note.Name, textWidth);
                note.Bounds = new PxRect(x + indent, y, textWidth, h);
                return h + S(8);
            }

            case D2DFieldWidget field:
            {
                var labelW = LabelColumn(width);
                var gap = S(9);
                var labelH = TextHeight(field.Name, labelW);
                var rowH = Math.Max(_fieldHeight, labelH);
                var boxW = Math.Min(Math.Max(S(40), width - labelW - gap), S(field.WidthDip));
                field.LabelBounds = new PxRect(x, y + ((rowH - labelH) / 2), labelW, labelH);
                field.BoxBounds = new PxRect(x + labelW + gap, y + ((rowH - _fieldHeight) / 2), boxW, _fieldHeight);
                var unitW = field.Unit is null ? 0 : (int)Math.Ceiling(MeasureWidth(field.Unit)) + S(4);
                field.UnitBounds = new PxRect(field.BoxBounds.Right + S(6), y + ((rowH - _lineHeight) / 2), unitW, _lineHeight);
                field.Bounds = new PxRect(x, y, width, rowH);
                return rowH + S(8);
            }

            case D2DComboWidget combo:
            {
                var labelW = LabelColumn(width);
                var gap = S(9);
                var labelH = TextHeight(combo.Name, labelW);
                var rowH = Math.Max(_fieldHeight, labelH);
                var avail = Math.Max(S(60), width - labelW - gap);
                var boxW = Math.Min(avail, combo.WidthDip > 0 ? S(combo.WidthDip) : S(360));
                combo.LabelBounds = new PxRect(x, y + ((rowH - labelH) / 2), labelW, labelH);
                combo.BoxBounds = new PxRect(x + labelW + gap, y + ((rowH - _fieldHeight) / 2), boxW, _fieldHeight);
                combo.Bounds = new PxRect(x, y, width, rowH);
                return rowH + S(8);
            }

            case D2DSliderWidget slider:
            {
                var labelW = LabelColumn(width);
                var gap = S(9);
                var valueW = Math.Max(S(51), (int)Math.Ceiling(MeasureWidth("100 %")) + S(9));
                var trackW = Math.Clamp(width - labelW - gap - valueW - gap, S(90), S(225));
                var labelH = TextHeight(slider.Name, labelW);
                var sliderH = Math.Max(_fieldHeight, _lineHeight + S(12));
                var rowH = Math.Max(sliderH, labelH);
                slider.LabelBounds = new PxRect(x, y + ((rowH - labelH) / 2), labelW, labelH);
                slider.TrackBounds = new PxRect(x + labelW + gap, y + ((rowH - sliderH) / 2), trackW, sliderH);
                slider.ValueBounds = new PxRect(slider.TrackBounds.Right + gap, y + ((rowH - _lineHeight) / 2), valueW, _lineHeight);
                slider.Bounds = new PxRect(x, y, width, rowH);
                return rowH + S(8);
            }

            case D2DCheckFlowWidget flow:
            {
                var indent = S(flow.IndentDip);
                var box = S(14);
                var colW = 0;
                foreach (var check in flow.Checks)
                {
                    colW = Math.Max(colW, (int)Math.Ceiling(MeasureWidth(check.Name)) + box + S(6) + S(18));
                }

                var avail = Math.Max(colW, width - indent);
                var cols = Math.Max(1, avail / Math.Max(1, colW));
                var rowH = Math.Max(box, _lineHeight) + S(8);
                for (var i = 0; i < flow.Checks.Count; i++)
                {
                    var check = flow.Checks[i];
                    check.Bounds = new PxRect(x + indent + ((i % cols) * colW), y + ((i / cols) * rowH), colW, rowH - S(4));
                }

                var rows = (flow.Checks.Count + cols - 1) / cols;
                flow.Bounds = new PxRect(x, y, width, rows * rowH);
                return (rows * rowH) + S(2);
            }

            default:
                return 0;
        }
    }

    // ---- váº½ -------------------------------------------------------------------------------------------------------------

    private static RectD R(PxRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static void Fill(IDrawContext ctx, PxRect r, ColorF color)
    {
        if (r.Width > 0 && r.Height > 0)
        {
            ctx.FillRectangle(R(r), color);
        }
    }

    /// <summary>Viá»n 1 px (cáº¡nh trong hÃ¬nh chá»¯ nháº­t), báº±ng bá»‘n hÃ¬nh chá»¯ nháº­t Ä‘áº·c Ä‘á»ƒ cháº¯c Ä‘Ãºng pixel.</summary>
    private static void Border(IDrawContext ctx, PxRect r, ColorF color, int thickness = 1)
    {
        var t = Math.Max(1, thickness);
        Fill(ctx, new PxRect(r.X, r.Y, r.Width, t), color);
        Fill(ctx, new PxRect(r.X, r.Bottom - t, r.Width, t), color);
        Fill(ctx, new PxRect(r.X, r.Y + t, t, r.Height - (2 * t)), color);
        Fill(ctx, new PxRect(r.Right - t, r.Y + t, t, r.Height - (2 * t)), color);
    }

    private void DrawString(IDrawContext ctx, string text, double x, double y, ColorF color, double maxWidth = double.PositiveInfinity,
        bool bold = false, double sizeDip = 12)
    {
        if (text.Length == 0)
        {
            return;
        }

        using var layout = LayoutText(text, maxWidth, bold, sizeDip);
        ctx.DrawText(layout, new PointD(x, y), color);
    }

    /// <summary>Váº½ má»™t khung hÃ¬nh: xoÃ¡ toÃ n bá»™ vÃ  váº½ láº¡i táº¥t cáº£ (khÃ´ng dÃ¹ng vÃ¹ng báº©n).</summary>
    public void Render(D2DDrawContext ctx)
    {
        FrameCount++;
        var p = _palette;
        ctx.Clear(p.Window);
        ctx.PushTransform(Matrix3x2.CreateScale((float)(1 / _scale)));
        try
        {
            RenderHeader(ctx);
            ctx.PushClip(R(_viewport));
            ctx.PushTransform(Matrix3x2.CreateTranslation(0, _viewport.Y - OffsetPx));
            try
            {
                var visible = new PxRect(0, OffsetPx, _viewport.Width, _viewport.Height);
                foreach (var widget in _content)
                {
                    RenderWidget(ctx, widget, visible);
                }
            }
            finally
            {
                ctx.PopTransform();
                ctx.PopClip();
            }

            RenderScrollBar(ctx);
            RenderFooter(ctx);
            RenderPopup(ctx);
        }
        finally
        {
            ctx.PopTransform();
        }

        NeedsRedraw = false;
    }

    private void RenderHeader(IDrawContext ctx)
    {
        DrawString(ctx, Title(), S(14), S(9), _palette.Text, bold: true, sizeDip: 16);
        Fill(ctx, new PxRect(0, _header.Bottom - 1, _clientWidth, 1), _palette.Border);
    }

    private void RenderWidget(IDrawContext ctx, D2DWidget widget, PxRect visible)
    {
        if (!widget.Visible || !widget.Bounds.Intersects(visible.Inflate(S(30))))
        {
            return;
        }

        var p = _palette;
        var text = widget.Enabled ? p.Text : p.TextDisabled;
        switch (widget)
        {
            case D2DGroupWidget group:
            {
                var b = group.Bounds;
                var top = b.Y + (_lineHeight / 2);
                var title = group.Name;
                var titleW = (int)Math.Ceiling(MeasureWidth(title));
                Border(ctx, new PxRect(b.X, top, b.Width, b.Bottom - top), p.Border);
                Fill(ctx, new PxRect(b.X + S(9), top, titleW + S(8), 1), p.Window);
                DrawString(ctx, title, b.X + S(13), b.Y, p.Text);
                foreach (var child in group.Children)
                {
                    RenderWidget(ctx, child, visible);
                }

                break;
            }

            case D2DCheckWidget check:
                RenderCheck(ctx, check, text);
                break;
            case D2DNoteWidget note:
                DrawString(ctx, note.Name, note.Bounds.X, note.Bounds.Y, p.TextMuted, note.Bounds.Width);
                break;
            case D2DFieldWidget field:
                DrawString(ctx, field.Name, field.LabelBounds.X, field.LabelBounds.Y, text, field.LabelBounds.Width);
                if (Mode == D2DEditMode.SelfDrawn)
                {
                    RenderEdit(ctx, field);
                }
                else
                {
                    Fill(ctx, field.BoxBounds, p.Input); // phÃ­a sau control Win32 tháº­t (trÃ¡nh lÃ³e khi Ä‘ang Ä‘áº·t vá»‹ trÃ­)
                }

                if (field.Unit is not null)
                {
                    DrawString(ctx, field.Unit, field.UnitBounds.X, field.UnitBounds.Y, text);
                }

                break;
            case D2DComboWidget combo:
                DrawString(ctx, combo.Name, combo.LabelBounds.X, combo.LabelBounds.Y, text, combo.LabelBounds.Width);
                if (Mode == D2DEditMode.SelfDrawn)
                {
                    RenderCombo(ctx, combo);
                }
                else
                {
                    Fill(ctx, combo.BoxBounds, p.Input);
                }

                break;
            case D2DSliderWidget slider:
                RenderSlider(ctx, slider, text);
                break;
            case D2DCheckFlowWidget flow:
                foreach (var check in flow.Checks)
                {
                    RenderCheck(ctx, check, check.Enabled ? p.Text : p.TextDisabled);
                }

                break;
        }
    }

    private static readonly (int X, int Y)[] TickCells = [(2, 6), (3, 7), (4, 8), (5, 9), (6, 8), (7, 7), (8, 6), (9, 5), (10, 4)];

    private void RenderCheck(IDrawContext ctx, D2DCheckWidget check, ColorF text)
    {
        var p = _palette;
        var b = check.Bounds;
        var size = S(14);
        var box = new PxRect(b.X, b.Y + 1, size, size);
        Fill(ctx, box, check.Enabled ? p.Input : p.Window);
        Border(ctx, box, check.Enabled ? p.InputBorder : p.Border);
        if (check.Checked)
        {
            foreach (var (cx, cy) in TickCells)
            {
                var x0 = box.X + (int)Math.Round(cx * _scale);
                var x1 = box.X + (int)Math.Round((cx + 1) * _scale);
                var y0 = box.Y + (int)Math.Round(cy * _scale);
                var y1 = box.Y + (int)Math.Round((cy + 2) * _scale);
                Fill(ctx, new PxRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)), text);
            }
        }

        var tx = b.X + size + S(6);
        DrawString(ctx, check.Name, tx, b.Y, text, Math.Max(1, b.Width - size - S(6)));
        if (ReferenceEquals(_focused, check) && _windowFocused)
        {
            using var layout = LayoutText(check.Name, Math.Max(1, b.Width - size - S(6)));
            var ring = new PxRect(tx - S(3), b.Y - 1, (int)Math.Ceiling(layout.Size.Width) + S(6), (int)Math.Ceiling(layout.Size.Height) + 2);
            Border(ctx, ring, p.Focus);
        }
    }

    private void RenderEdit(IDrawContext ctx, D2DFieldWidget field)
    {
        var p = _palette;
        var b = field.BoxBounds;
        var focused = ReferenceEquals(_focused, field) && _windowFocused;
        Fill(ctx, b, field.Enabled ? p.Input : p.Window);
        Border(ctx, b, focused ? p.Accent : p.InputBorder);
        var pad = S(5);
        var inner = new PxRect(b.X + pad, b.Y + 2, Math.Max(1, b.Width - (2 * pad)), b.Height - 4);
        var box = field.Box;
        var caretX = MeasureWidth(box.Text[..Math.Clamp(box.Caret, 0, box.Text.Length)]);
        if (caretX - box.ScrollX > inner.Width - 1)
        {
            box.ScrollX = caretX - inner.Width + 1;
        }
        else if (caretX < box.ScrollX)
        {
            box.ScrollX = caretX;
        }

        ctx.PushClip(R(inner));
        var originX = inner.X - box.ScrollX;
        var textY = b.Y + ((b.Height - _lineHeight) / 2);
        if (focused && box.HasSelection)
        {
            var a = MeasureWidth(box.Text[..box.SelectionStart]);
            var z = MeasureWidth(box.Text[..box.SelectionEnd]);
            Fill(ctx, new PxRect((int)Math.Round(originX + a), textY, Math.Max(1, (int)Math.Round(z - a)), _lineHeight), p.Selection);
        }

        DrawString(ctx, box.Text, originX, textY, field.Enabled ? p.Text : p.TextDisabled);
        if (focused && _caretVisible && !box.HasSelection && field.Enabled)
        {
            Fill(ctx, new PxRect((int)Math.Round(originX + caretX), textY, Math.Max(1, S(1)), _lineHeight), p.Text);
        }

        ctx.PopClip();
    }

    private void RenderCombo(IDrawContext ctx, D2DComboWidget combo)
    {
        var p = _palette;
        var b = combo.BoxBounds;
        var focused = ReferenceEquals(_focused, combo) && _windowFocused;
        Fill(ctx, b, combo.Enabled ? p.Input : p.Window);
        Border(ctx, b, focused || ReferenceEquals(_openCombo, combo) ? p.Accent : p.InputBorder);
        var arrowW = S(20);
        var color = combo.Enabled ? p.Text : p.TextDisabled;
        DrawString(ctx, combo.SelectedText, b.X + S(6), b.Y + ((b.Height - _lineHeight) / 2), color, Math.Max(1, b.Width - arrowW - S(8)));
        // MÅ©i tÃªn: 4 hÃ ng háº¹p dáº§n.
        var cx = b.Right - (arrowW / 2);
        var cy = b.Y + (b.Height / 2) - S(2);
        for (var i = 0; i < 4; i++)
        {
            var w = Math.Max(1, S(7 - (2 * i)));
            Fill(ctx, new PxRect(cx - (w / 2), cy + S(i), w, Math.Max(1, S(1))), color);
        }
    }

    private int SliderThumbX(D2DSliderWidget s)
    {
        var thumbW = S(10);
        var range = Math.Max(1, s.Max - s.Min);
        return s.TrackBounds.X + (int)Math.Round((s.Value - s.Min) * (s.TrackBounds.Width - thumbW) / (double)range);
    }

    private void RenderSlider(IDrawContext ctx, D2DSliderWidget slider, ColorF text)
    {
        var p = _palette;
        DrawString(ctx, slider.Name, slider.LabelBounds.X, slider.LabelBounds.Y, text, slider.LabelBounds.Width);
        var t = slider.TrackBounds;
        var trackH = Math.Max(2, S(4));
        var trackY = t.Y + ((t.Height - trackH) / 2);
        var thumbW = S(10);
        var thumbH = Math.Min(t.Height, S(20));
        var thumbX = SliderThumbX(slider);
        Fill(ctx, new PxRect(t.X, trackY, t.Width, trackH), p.Border);
        Fill(ctx, new PxRect(t.X, trackY, Math.Max(0, thumbX - t.X + (thumbW / 2)), trackH), p.Accent);
        var thumb = new PxRect(thumbX, t.Y + ((t.Height - thumbH) / 2), thumbW, thumbH);
        Fill(ctx, thumb, ReferenceEquals(_pressed, slider) ? p.ButtonPressed : p.Button);
        Border(ctx, thumb, p.InputBorder);
        if (ReferenceEquals(_focused, slider) && _windowFocused)
        {
            Border(ctx, t.Inflate(1), p.Focus);
        }

        DrawString(ctx, slider.ValueText(slider.Value), slider.ValueBounds.X, slider.ValueBounds.Y, text);
    }

    private (PxRect Thumb, bool Visible) ThumbRect()
    {
        if (MaxOffsetPx <= 0 || _scrollTrack.Height <= 0)
        {
            return (default, false);
        }

        var minThumb = S(24);
        var thumbH = Math.Clamp((int)((long)_scrollTrack.Height * _viewport.Height / ContentHeightPx), minThumb, _scrollTrack.Height);
        var room = _scrollTrack.Height - thumbH;
        var y = _scrollTrack.Y + (room == 0 ? 0 : (int)((long)room * OffsetPx / MaxOffsetPx));
        return (new PxRect(_scrollTrack.X + S(2), y, Math.Max(1, _scrollTrack.Width - S(4)), thumbH), true);
    }

    private void RenderScrollBar(IDrawContext ctx)
    {
        Fill(ctx, _scrollTrack, _palette.ScrollTrack);
        var (thumb, visible) = ThumbRect();
        if (visible)
        {
            Fill(ctx, thumb, _draggingThumb ? _palette.ButtonPressed : _palette.ScrollThumb);
        }
    }

    private void RenderFooter(IDrawContext ctx)
    {
        var p = _palette;
        Fill(ctx, new PxRect(0, _footerRect.Y, _clientWidth, 1), p.Border);
        foreach (var button in _footer)
        {
            var b = button.Bounds;
            var fill = ReferenceEquals(_pressed, button) ? p.ButtonPressed : ReferenceEquals(_hot, button) ? p.ButtonHover : p.Button;
            Fill(ctx, b, fill);
            Border(ctx, b, button.IsDefault ? p.Accent : p.InputBorder);
            var w = MeasureWidth(button.Name);
            DrawString(ctx, button.Name, b.X + ((b.Width - w) / 2), b.Y + ((b.Height - _lineHeight) / 2), p.Text);
            if (ReferenceEquals(_focused, button) && _windowFocused)
            {
                Border(ctx, b.Inflate(-S(3)), p.Focus);
            }
        }
    }

    private PxRect PopupRect(D2DComboWidget combo)
    {
        var box = combo.BoxBounds.Offset(0, _viewport.Y - OffsetPx);
        var rowH = _fieldHeight;
        var h = (combo.Items.Count * rowH) + 2;
        var below = box.Bottom;
        var y = below + h <= _clientHeight || box.Y - h < 0 ? below : box.Y - h;
        return new PxRect(box.X, y, box.Width, h);
    }

    private void RenderPopup(IDrawContext ctx)
    {
        if (_openCombo is null)
        {
            return;
        }

        var p = _palette;
        var r = PopupRect(_openCombo);
        Fill(ctx, r, p.Input);
        Border(ctx, r, p.InputBorder);
        for (var i = 0; i < _openCombo.Items.Count; i++)
        {
            var row = new PxRect(r.X + 1, r.Y + 1 + (i * _fieldHeight), r.Width - 2, _fieldHeight);
            if (i == _popupHot)
            {
                Fill(ctx, row, p.Selection);
            }

            DrawString(ctx, _openCombo.Items[i](), row.X + S(5), row.Y + ((row.Height - _lineHeight) / 2), p.Text, Math.Max(1, row.Width - S(10)));
        }
    }

    // ---- cuá»™n -----------------------------------------------------------------------------------------------------------

    public void ScrollTo(int offsetPx)
    {
        var next = Math.Clamp(offsetPx, 0, MaxOffsetPx);
        if (next == OffsetPx)
        {
            return;
        }

        OffsetPx = next;
        NeedsRedraw = true;
    }

    public int LineStepPx => _lineHeight + S(6);

    public void Wheel(int notches)
    {
        CloseCombo();
        ScrollTo(OffsetPx - (notches * 3 * LineStepPx));
    }

    private int PageStep => Math.Max(LineStepPx, _viewport.Height - LineStepPx);

    private void EnsureVisible(D2DWidget widget)
    {
        if (widget is D2DButtonWidget)
        {
            return;
        }

        var b = widget.Bounds;
        var margin = S(8);
        if (b.Y - margin < OffsetPx)
        {
            ScrollTo(b.Y - margin);
        }
        else if (b.Bottom + margin > OffsetPx + _viewport.Height)
        {
            ScrollTo(b.Bottom + margin - _viewport.Height);
        }
    }

    // ---- focus ----------------------------------------------------------------------------------------------------------

    public IReadOnlyList<D2DWidget> FocusOrder()
    {
        var list = new List<D2DWidget>();
        foreach (var widget in AllWidgets())
        {
            if (widget.Focusable)
            {
                list.Add(widget);
            }
        }

        return list;
    }

    public void SetFocus(D2DWidget? widget, bool fromHost = false)
    {
        if (ReferenceEquals(_focused, widget))
        {
            return;
        }

        _focused = widget;
        _caretVisible = true;
        if (widget is not null)
        {
            EnsureVisible(widget);
        }

        NeedsRedraw = true;
        if (!fromHost)
        {
            FocusMoved?.Invoke(widget);
        }
    }

    public void SetWindowFocused(bool focused)
    {
        _windowFocused = focused;
        if (!focused)
        {
            CloseCombo();
        }

        NeedsRedraw = true;
    }

    private void MoveFocus(bool forward)
    {
        var order = FocusOrder();
        if (order.Count == 0)
        {
            return;
        }

        var index = _focused is null ? -1 : order.ToList().IndexOf(_focused);
        var next = index < 0 ? (forward ? 0 : order.Count - 1) : (index + (forward ? 1 : -1) + order.Count) % order.Count;
        SetFocus(order[next]);
    }

    public void BlinkTick()
    {
        if (_focused is D2DFieldWidget)
        {
            _caretVisible = !_caretVisible;
            NeedsRedraw = true;
        }
    }

    private void ResetCaret()
    {
        _caretVisible = true;
        NeedsRedraw = true;
    }

    // ---- chuá»™t ----------------------------------------------------------------------------------------------------------

    public D2DCursorKind CursorAt(int x, int y)
    {
        if (Mode == D2DEditMode.SelfDrawn && _openCombo is null && _viewport.Contains(x, y))
        {
            var cy = y - _viewport.Y + OffsetPx;
            foreach (var w in AllWidgets())
            {
                if (w is D2DFieldWidget f && f.Enabled && f.BoxBounds.Contains(x, cy))
                {
                    return D2DCursorKind.IBeam;
                }
            }
        }

        return D2DCursorKind.Arrow;
    }

    private D2DWidget? HitTest(int x, int y)
    {
        foreach (var button in _footer)
        {
            if (button.Enabled && button.Bounds.Contains(x, y))
            {
                return button;
            }
        }

        if (!_viewport.Contains(x, y))
        {
            return null;
        }

        var cy = y - _viewport.Y + OffsetPx;
        foreach (var widget in AllWidgets())
        {
            if (!widget.Visible || !widget.Enabled)
            {
                continue;
            }

            switch (widget)
            {
                case D2DCheckWidget check when check.Bounds.Contains(x, cy):
                    return check;
                case D2DFieldWidget field when Mode == D2DEditMode.SelfDrawn && field.BoxBounds.Contains(x, cy):
                    return field;
                case D2DComboWidget combo when Mode == D2DEditMode.SelfDrawn && combo.BoxBounds.Contains(x, cy):
                    return combo;
                case D2DSliderWidget slider when slider.TrackBounds.Contains(x, cy):
                    return slider;
            }
        }

        return null;
    }

    public void MouseMove(int x, int y, bool leftDown)
    {
        if (_draggingThumb && leftDown)
        {
            var (thumb, _) = ThumbRect();
            var room = _scrollTrack.Height - thumb.Height;
            if (room > 0)
            {
                var pos = y - _grabTop() - _thumbGrabOffset;
                ScrollTo((int)((long)pos * MaxOffsetPx / room));
            }

            return;
        }

        if (_draggingSlider && leftDown && _pressed is D2DSliderWidget dragged)
        {
            SetSliderFromX(dragged, x);
            return;
        }

        if (_draggingSelection && leftDown && _selectionField is { } sel)
        {
            sel.Box.MoveTo(sel.Box.HitTest(x - (sel.BoxBounds.X + S(5)) + sel.Box.ScrollX, MeasureWidth), true);
            ResetCaret();
            return;
        }

        if (_openCombo is not null)
        {
            var r = PopupRect(_openCombo);
            var hot = r.Contains(x, y) ? (y - r.Y - 1) / _fieldHeight : -1;
            if (hot != _popupHot)
            {
                _popupHot = hot;
                NeedsRedraw = true;
            }

            return;
        }

        var over = HitTest(x, y);
        if (!ReferenceEquals(over, _hot))
        {
            _hot = over;
            NeedsRedraw = true;
        }
    }

    private int _grabTop() => _scrollTrack.Y;

    public void MouseDown(int x, int y, bool doubleClick = false)
    {
        if (_openCombo is not null)
        {
            var r = PopupRect(_openCombo);
            if (r.Contains(x, y))
            {
                var index = Math.Clamp((y - r.Y - 1) / _fieldHeight, 0, _openCombo.Items.Count - 1);
                ChooseComboItem(_openCombo, index);
                CloseCombo();
                return;
            }

            var sameBox = _openCombo.BoxBounds.Offset(0, _viewport.Y - OffsetPx).Contains(x, y);
            CloseCombo();
            if (sameBox)
            {
                return;
            }
        }

        if (_scrollTrack.Contains(x, y))
        {
            var (thumb, visible) = ThumbRect();
            if (visible && thumb.Contains(x, y))
            {
                _draggingThumb = true;
                _thumbGrabOffset = y - thumb.Y;
            }
            else if (visible)
            {
                ScrollTo(OffsetPx + (y < thumb.Y ? -PageStep : PageStep));
            }

            NeedsRedraw = true;
            return;
        }

        var hit = HitTest(x, y);
        _pressed = hit;
        if (hit is null)
        {
            return;
        }

        SetFocus(hit);
        switch (hit)
        {
            case D2DFieldWidget field:
                if (doubleClick)
                {
                    field.Box.SelectAll();
                }
                else
                {
                    field.Box.MoveTo(field.Box.HitTest(x - (field.BoxBounds.X + S(5)) + field.Box.ScrollX, MeasureWidth), false);
                    _draggingSelection = true;
                    _selectionField = field;
                }

                ResetCaret();
                break;
            case D2DComboWidget combo:
                OpenCombo(combo);
                _pressed = null;
                break;
            case D2DSliderWidget slider:
                SetSliderFromX(slider, x);
                _draggingSlider = true;
                break;
        }

        NeedsRedraw = true;
    }

    public void MouseUp(int x, int y)
    {
        var pressed = _pressed;
        _pressed = null;
        _draggingThumb = false;
        _draggingSlider = false;
        _draggingSelection = false;
        _selectionField = null;
        NeedsRedraw = true;
        if (pressed is null)
        {
            return;
        }

        var hit = HitTest(x, y);
        if (!ReferenceEquals(hit, pressed))
        {
            return;
        }

        switch (pressed)
        {
            case D2DCheckWidget check:
                Toggle(check);
                break;
            case D2DButtonWidget button:
                ButtonInvoked?.Invoke(button);
                break;
        }
    }

    /// <summary>CÃ³ chuá»™t Ä‘ang kÃ©o (cá»­a sá»• giá»¯ capture).</summary>
    public bool IsDragging => _draggingThumb || _draggingSlider || _draggingSelection;

    private void SetSliderFromX(D2DSliderWidget s, int x)
    {
        var thumbW = S(10);
        var span = Math.Max(1, s.TrackBounds.Width - thumbW);
        var rel = Math.Clamp(x - s.TrackBounds.X - (thumbW / 2), 0, span);
        SetSlider(s, s.Min + (int)Math.Round(rel * (s.Max - s.Min) / (double)span));
    }

    private void SetSlider(D2DSliderWidget s, int value)
    {
        value = Math.Clamp(value, s.Min, s.Max);
        if (value == s.Value)
        {
            return;
        }

        s.Value = value;
        NeedsRedraw = true;
        Changed?.Invoke(s);
    }

    private void Toggle(D2DCheckWidget check)
    {
        check.Checked = !check.Checked;
        NeedsRedraw = true;
        Changed?.Invoke(check);
    }

    private void OpenCombo(D2DComboWidget combo)
    {
        _openCombo = combo;
        _popupHot = combo.Selected;
        NeedsRedraw = true;
    }

    private void CloseCombo()
    {
        if (_openCombo is null)
        {
            return;
        }

        _openCombo = null;
        _popupHot = -1;
        NeedsRedraw = true;
    }

    private void ChooseComboItem(D2DComboWidget combo, int index)
    {
        if (index == combo.Selected || index < 0 || index >= combo.Items.Count)
        {
            return;
        }

        combo.Selected = index;
        NeedsRedraw = true;
        Changed?.Invoke(combo);
    }

    // ---- bÃ n phÃ­m -------------------------------------------------------------------------------------------------------

    /// <summary>WM_KEYDOWN. Tráº£ true khi Ä‘Ã£ xá»­ lÃ½.</summary>
    public bool KeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (_openCombo is { } open)
        {
            switch (vk)
            {
                case VkEscape:
                    CloseCombo();
                    return true;
                case VkReturn or VkSpace:
                    ChooseComboItem(open, _popupHot);
                    CloseCombo();
                    return true;
                case VkUp:
                    _popupHot = Math.Max(0, _popupHot - 1);
                    NeedsRedraw = true;
                    return true;
                case VkDown:
                    _popupHot = Math.Min(open.Items.Count - 1, _popupHot + 1);
                    NeedsRedraw = true;
                    return true;
                case VkTab:
                    CloseCombo();
                    break;
                default:
                    return true;
            }
        }

        if (vk == VkTab)
        {
            MoveFocus(!shift);
            return true;
        }

        switch (_focused)
        {
            case D2DFieldWidget field when field.Enabled:
                if (HandleEditKey(field, vk, ctrl, shift))
                {
                    return true;
                }

                break;
            case D2DCheckWidget check when vk == VkSpace:
                Toggle(check);
                return true;
            case D2DButtonWidget button when vk is VkSpace or VkReturn:
                ButtonInvoked?.Invoke(button);
                return true;
            case D2DComboWidget combo:
                switch (vk)
                {
                    case VkSpace or VkF4:
                        OpenCombo(combo);
                        return true;
                    case VkDown when alt:
                        OpenCombo(combo);
                        return true;
                    case VkDown:
                        ChooseComboItem(combo, Math.Min(combo.Items.Count - 1, combo.Selected + 1));
                        return true;
                    case VkUp:
                        ChooseComboItem(combo, Math.Max(0, combo.Selected - 1));
                        return true;
                    case VkHome:
                        ChooseComboItem(combo, 0);
                        return true;
                    case VkEnd:
                        ChooseComboItem(combo, combo.Items.Count - 1);
                        return true;
                }

                break;
            case D2DSliderWidget slider:
                switch (vk)
                {
                    case VkLeft or VkDown:
                        SetSlider(slider, slider.Value - 1);
                        return true;
                    case VkRight or VkUp:
                        SetSlider(slider, slider.Value + 1);
                        return true;
                    case VkPrior:
                        SetSlider(slider, slider.Value + 10);
                        return true;
                    case VkNext:
                        SetSlider(slider, slider.Value - 10);
                        return true;
                    case VkHome:
                        SetSlider(slider, slider.Min);
                        return true;
                    case VkEnd:
                        SetSlider(slider, slider.Max);
                        return true;
                }

                break;
        }

        // PhÃ­m Ä‘iá»u hÆ°á»›ng chung: mÅ©i tÃªn di chuyá»ƒn focus (Ã´ tick/nÃºt), PageUp/PageDown/Home/End cuá»™n khung.
        switch (vk)
        {
            case VkDown or VkRight when _focused is D2DCheckWidget or D2DButtonWidget or null:
                MoveFocus(true);
                return true;
            case VkUp or VkLeft when _focused is D2DCheckWidget or D2DButtonWidget or null:
                MoveFocus(false);
                return true;
            case VkNext:
                ScrollTo(OffsetPx + PageStep);
                return true;
            case VkPrior:
                ScrollTo(OffsetPx - PageStep);
                return true;
            case VkEnd when ctrl || _focused is not D2DFieldWidget:
                ScrollTo(int.MaxValue / 2);
                return true;
            case VkHome when ctrl || _focused is not D2DFieldWidget:
                ScrollTo(0);
                return true;
        }

        return false;
    }

    private bool HandleEditKey(D2DFieldWidget field, int vk, bool ctrl, bool shift)
    {
        var box = field.Box;
        var changed = false;
        switch (vk)
        {
            case VkLeft:
                box.MoveLeft(shift, ctrl);
                break;
            case VkRight:
                box.MoveRight(shift, ctrl);
                break;
            case VkHome:
                box.MoveTo(0, shift);
                break;
            case VkEnd:
                box.MoveTo(box.Text.Length, shift);
                break;
            case VkBack:
                changed = box.Backspace(ctrl);
                break;
            case VkDelete:
                changed = box.Delete(ctrl);
                break;
            case 'A' when ctrl:
                box.SelectAll();
                break;
            case 'C' when ctrl:
                box.Copy(_clipboard);
                break;
            case 'X' when ctrl:
                changed = box.Cut(_clipboard);
                break;
            case 'V' when ctrl:
                changed = box.Paste(_clipboard);
                break;
            case 'Z' when ctrl:
                changed = box.Undo();
                break;
            case 'Y' when ctrl:
                changed = box.Redo();
                break;
            case VkReturn or VkEscape or VkUp or VkDown or VkPrior or VkNext:
                return false;
            default:
                return false;
        }

        ResetCaret();
        if (changed)
        {
            Changed?.Invoke(field);
        }

        return true;
    }

    /// <summary>WM_CHAR (kÃ½ tá»± Ä‘Ã£ dá»‹ch, gá»“m káº¿t quáº£ cá»§a IME/Telex).</summary>
    public bool Char(char c)
    {
        if (_focused is D2DFieldWidget { Enabled: true } field && Mode == D2DEditMode.SelfDrawn)
        {
            if (field.Box.TypeChar(c))
            {
                ResetCaret();
                Changed?.Invoke(field);
                return true;
            }

            return !char.IsControl(c); // kÃ½ tá»± bá»‹ tá»« chá»‘i: váº«n "Ä‘Ã£ xá»­ lÃ½" (khÃ´ng bÃ­p lan ra ngoÃ i)
        }

        return false;
    }

    // ---- giÃ¡ trá»‹ tá»« ngoÃ i (cháº¿ Ä‘á»™ lai: control Win32 bÃ¡o) ---------------------------------------------------------------

    public void NotifyChanged(D2DWidget widget)
    {
        NeedsRedraw = true;
        Changed?.Invoke(widget);
    }

    /// <summary>Control tháº­t cho cÃ¡c Ã´: hÃ¬nh chá»¯ nháº­t client (chÆ°a cáº¯t) cá»§a Ã´ nháº­p/combobox, Ä‘á»ƒ cá»­a sá»• Ä‘áº·t vá»‹ trÃ­.</summary>
    public IEnumerable<(D2DWidget Widget, PxRect Client)> HostedAreas()
    {
        if (Mode != D2DEditMode.HostedWin32)
        {
            yield break;
        }

        var dy = _viewport.Y - OffsetPx;
        foreach (var widget in AllWidgets())
        {
            switch (widget)
            {
                case D2DFieldWidget f when f.Visible:
                    yield return (f, f.BoxBounds.Offset(0, dy));
                    break;
                case D2DComboWidget c when c.Visible:
                    yield return (c, c.BoxBounds.Offset(0, dy));
                    break;
            }
        }
    }

    // ---- trá»£ nÄƒng -------------------------------------------------------------------------------------------------------

    /// <summary>CÃ¢y trá»£ nÄƒng tá»‘i thiá»ƒu (tÃªn, vai trÃ², tráº¡ng thÃ¡i, hÃ¬nh chá»¯ nháº­t client) - nguá»“n cá»§a UIA/MSAA.</summary>
    public IReadOnlyList<D2DAccessibleNode> AccessibleNodes()
    {
        var list = new List<D2DAccessibleNode>();
        var dy = _viewport.Y - OffsetPx;
        foreach (var widget in AllWidgets())
        {
            var visibleRect = widget is D2DButtonWidget ? widget.Bounds : widget.Bounds.Offset(0, dy).Intersect(_viewport);
            switch (widget)
            {
                case D2DGroupWidget g:
                    list.Add(Node(g, g.AccessibleName, null, null, 1, visibleRect));
                    break;
                case D2DCheckWidget c:
                    list.Add(Node(c, c.AccessibleName, c.Checked, null, 2, visibleRect));
                    break;
                case D2DNoteWidget n:
                    list.Add(Node(n, n.AccessibleName, null, null, 2, visibleRect));
                    break;
                case D2DFieldWidget f:
                    list.Add(Node(f, f.AccessibleName, null, f.Box.Text, 2, f.BoxBounds.Offset(0, dy).Intersect(_viewport)));
                    break;
                case D2DComboWidget c:
                    list.Add(Node(c, c.AccessibleName, null, c.SelectedText, 2, c.BoxBounds.Offset(0, dy).Intersect(_viewport), ReferenceEquals(_openCombo, c)));
                    break;
                case D2DSliderWidget s:
                    list.Add(Node(s, s.AccessibleName, null, s.ValueText(s.Value), 2, s.TrackBounds.Offset(0, dy).Intersect(_viewport)));
                    break;
                case D2DButtonWidget b:
                    list.Add(Node(b, b.AccessibleName, null, null, 1, visibleRect));
                    break;
            }
        }

        return list;
    }

    private D2DAccessibleNode Node(D2DWidget w, string name, bool? isChecked, string? value, int level, PxRect bounds, bool? expanded = null)
        => new(w.Id, name, w.Role, bounds, w.Enabled, w.Focusable, ReferenceEquals(_focused, w), isChecked, value, expanded, level);
}
