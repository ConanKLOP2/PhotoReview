using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi;

/// <summary>Một hàng "nhãn + điều khiển" đã khai báo: id của nhãn và của điều khiển.</summary>
internal readonly record struct SettingsRow(int Label, int Control);

/// <summary>Một hàng "nhãn + thanh trượt + chữ giá trị".</summary>
internal readonly record struct SettingsSliderRow(int Label, int Slider, int Value);

/// <summary>Bề mặt đo/đặt mà <see cref="SettingsLayoutElement"/> dùng (khung cài đặt thật; test thay bản giả để kiểm công thức).</summary>
internal interface ISettingsLayoutSurface
{
    int Dx(int dialogUnits);

    int Dy(int dialogUnits);

    int LineHeight { get; }

    /// <summary>Chiều cao ô nhập/combobox/nút (px).</summary>
    int FieldHeight { get; }

    string TextOf(int id);

    /// <summary>Chiều cao (px) chữ của điều khiển <paramref name="id"/> khi xuống dòng trong <paramref name="width"/>.</summary>
    int TextHeight(int id, int width);

    int TextWidth(string text);

    /// <summary>Điều khiển bị trang ẩn (<see cref="SettingsPageContext.SetVisible"/>): không chiếm chỗ.</summary>
    bool IsHidden(int id);

    /// <summary>Chiều cao thêm của danh sách thả combobox (tính vào cửa sổ combobox).</summary>
    int ComboDropHeight(int id);

    void Place(int id, int x, int y, int width, int height);

    void Hide(int id);
}

/// <summary>
/// Một phần tử bố cục của trang (nhóm, hàng nhãn+điều khiển, ô tick, ghi chú, dãy nút...). <see cref="Place"/> đặt các điều khiển của nó
/// theo bề rộng cho trước và trả CHIỀU CAO đã dùng (gồm khoảng cách dưới); ẩn thì trả 0. Bố cục tính lại mỗi lần đổi cỡ/DPI/ngôn ngữ/cuộn.
/// </summary>
internal abstract class SettingsLayoutElement
{
    /// <summary>False = phần tử (và con) ẩn, không chiếm chỗ.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Mọi id điều khiển của phần tử (kể cả con).</summary>
    public abstract IEnumerable<int> ControlIds { get; }

    /// <summary>Đặt điều khiển tại (x, y) rộng <paramref name="width"/>; trả chiều cao đã dùng.</summary>
    public int Place(ISettingsLayoutSurface surface, int x, int y, int width)
    {
        if (!Visible)
        {
            foreach (var id in ControlIds)
            {
                surface.Hide(id);
            }

            return 0;
        }

        return PlaceVisible(surface, x, y, width);
    }

    protected abstract int PlaceVisible(ISettingsLayoutSurface surface, int x, int y, int width);

    /// <summary>Cột nhãn của các hàng: ~38 % bề rộng, kẹp 80..140 dlu (nhãn tiếng Việt dài thì xuống dòng).</summary>
    internal static int LabelWidth(ISettingsLayoutSurface surface, int width) => Math.Clamp(width * 38 / 100, surface.Dx(80), surface.Dx(140));
}

/// <summary>Nhóm (GroupBox) bao các phần tử con.</summary>
internal sealed class SettingsGroupElement(int titleId, List<SettingsLayoutElement> children) : SettingsLayoutElement
{
    public int TitleId => titleId;

    public IReadOnlyList<SettingsLayoutElement> Children => children;

    public override IEnumerable<int> ControlIds => children.SelectMany(child => child.ControlIds).Prepend(titleId);

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        var pad = s.Dx(8);
        var top = s.LineHeight + s.Dy(5);
        var innerX = x + pad;
        var innerWidth = Math.Max(s.Dx(40), width - (2 * pad));
        var cursor = y + top;
        foreach (var child in children)
        {
            cursor += child.Place(s, innerX, cursor, innerWidth);
        }

        cursor += s.Dy(2);
        s.Place(titleId, x, y, width, cursor - y);
        return (cursor - y) + s.Dy(8);
    }
}

/// <summary>Ô tick (có thể thụt vào và xuống dòng).</summary>
internal sealed class SettingsCheckElement(int id, int indentDlu) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => [id];

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        if (s.IsHidden(id))
        {
            s.Hide(id);
            return 0;
        }

        var indent = s.Dx(indentDlu);
        var textWidth = Math.Max(s.Dx(20), width - indent - s.Dx(14));
        var height = Math.Max(s.LineHeight + s.Dy(2), s.TextHeight(id, textWidth) + s.Dy(1));
        s.Place(id, x + indent, y, width - indent, height);
        return height + s.Dy(4);
    }
}

/// <summary>Hàng nhãn (cột trái) + một điều khiển (cột phải): combobox, ô nhập, nút...</summary>
internal sealed class SettingsRowElement(int labelId, int controlId, SettingsRowKind kind, int widthDlu) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => [labelId, controlId];

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        if (s.IsHidden(controlId))
        {
            s.Hide(labelId);
            s.Hide(controlId);
            return 0;
        }

        var labelWidth = LabelWidth(s, width);
        var gap = s.Dx(6);
        var controlX = x + labelWidth + gap;
        var available = Math.Max(s.Dx(30), width - labelWidth - gap);
        var controlWidth = widthDlu > 0 ? Math.Min(available, s.Dx(widthDlu)) : Math.Min(available, s.Dx(240));
        var controlHeight = s.FieldHeight;
        var labelHeight = s.TextHeight(labelId, labelWidth);
        var rowHeight = Math.Max(controlHeight, labelHeight);
        s.Place(labelId, x, y + ((rowHeight - labelHeight) / 2), labelWidth, labelHeight);
        var windowHeight = kind == SettingsRowKind.Combo ? controlHeight + s.ComboDropHeight(controlId) : controlHeight;
        s.Place(controlId, controlX, y + ((rowHeight - controlHeight) / 2), controlWidth, windowHeight);
        return rowHeight + s.Dy(5);
    }
}

internal enum SettingsRowKind
{
    Combo,
    Edit,
}

/// <summary>Hàng nhãn + thanh trượt + chữ giá trị bên phải.</summary>
internal sealed class SettingsSliderElement(int labelId, int sliderId, int valueId) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => [labelId, sliderId, valueId];

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        var labelWidth = LabelWidth(s, width);
        var gap = s.Dx(6);
        var sliderX = x + labelWidth + gap;
        var valueWidth = Math.Max(s.Dx(34), Math.Max(s.TextWidth(s.TextOf(valueId)), s.TextWidth("100 %")) + s.Dx(6));
        var sliderWidth = Math.Clamp(width - labelWidth - gap - valueWidth - gap, s.Dx(60), s.Dx(150));
        var sliderHeight = Math.Max(s.FieldHeight, s.LineHeight + s.Dy(8));
        var labelHeight = s.TextHeight(labelId, labelWidth);
        var rowHeight = Math.Max(sliderHeight, labelHeight);
        s.Place(labelId, x, y + ((rowHeight - labelHeight) / 2), labelWidth, labelHeight);
        s.Place(sliderId, sliderX, y + ((rowHeight - sliderHeight) / 2), sliderWidth, sliderHeight);
        var valueHeight = s.TextHeight(valueId, valueWidth);
        s.Place(valueId, sliderX + sliderWidth + gap, y + ((rowHeight - valueHeight) / 2), valueWidth, valueHeight);
        return rowHeight + s.Dy(5);
    }
}

/// <summary>Chữ nhiều dòng: ghi chú (mờ, thẳng cột điều khiển) hoặc dòng trạng thái (toàn bề rộng).</summary>
internal sealed class SettingsTextElement(int id, bool alignedToControls) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => [id];

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        if (s.IsHidden(id) || s.TextOf(id).Length == 0)
        {
            s.Hide(id); // an empty status line takes no room (the page relays out when it sets the text)
            return 0;
        }

        var indent = alignedToControls ? LabelWidth(s, width) + s.Dx(6) : 0;
        var textWidth = Math.Max(s.Dx(30), width - indent);
        var height = s.TextHeight(id, textWidth);
        s.Place(id, x + indent, y, textWidth, height);
        return height + s.Dy(5);
    }
}

/// <summary>Dãy nút xuống dòng khi hết bề rộng (nút ẩn bị bỏ qua).</summary>
internal sealed class SettingsButtonsElement(IReadOnlyList<int> ids, bool alignedToControls) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => ids;

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        var indent = alignedToControls ? LabelWidth(s, width) + s.Dx(6) : 0;
        var left = x + indent;
        var right = x + width;
        var gap = s.Dx(6);
        var height = s.FieldHeight;
        var cursorX = left;
        var cursorY = y;
        var any = false;
        foreach (var id in ids)
        {
            if (s.IsHidden(id))
            {
                s.Hide(id);
                continue;
            }

            var buttonWidth = Math.Max(s.Dx(50), s.TextWidth(s.TextOf(id)) + s.Dx(16));
            if (cursorX > left && cursorX + buttonWidth > right)
            {
                cursorX = left;
                cursorY += height + s.Dy(4);
            }

            s.Place(id, cursorX, cursorY, buttonWidth, height);
            cursorX += buttonWidth + gap;
            any = true;
        }

        return any ? (cursorY - y) + height + s.Dy(5) : 0;
    }
}

/// <summary>Dãy ô tick xuống dòng theo bề rộng (WrapPanel của bản WPF).</summary>
internal sealed class SettingsCheckFlowElement(IReadOnlyList<int> ids, int indentDlu) : SettingsLayoutElement
{
    public override IEnumerable<int> ControlIds => ids;

    protected override int PlaceVisible(ISettingsLayoutSurface s, int x, int y, int width)
    {
        var indent = s.Dx(indentDlu);
        var left = x + indent;
        var right = x + width;
        var gap = s.Dx(10);
        var height = s.LineHeight + s.Dy(2);
        var cursorX = left;
        var cursorY = y;
        foreach (var id in ids)
        {
            var itemWidth = s.TextWidth(s.TextOf(id)) + s.Dx(16);
            if (cursorX > left && cursorX + itemWidth > right)
            {
                cursorX = left;
                cursorY += height + s.Dy(4);
            }

            s.Place(id, cursorX, cursorY, Math.Min(itemWidth, right - left), height);
            cursorX += itemWidth + gap;
        }

        return (cursorY - y) + height + s.Dy(5);
    }
}

/// <summary>
/// WP-35 GĐ3a: "builder" bố cục của một trang Settings. Trang khai báo điều khiển theo thứ tự đọc (= thứ tự Tab) và nhận về id; khung
/// tạo template, đo và đặt theo dialog-unit (chiều cao chữ nhiều dòng ĐO thật nên chuỗi tiếng Việt dài không bị cắt, kéo giãn/DPI
/// đúng). Chữ khai báo bằng <c>Func&lt;string&gt;</c> nên tự đổi khi đổi ngôn ngữ. Id của trang nằm trong [<see cref="IdBase"/> + 1,
/// <see cref="IdBase"/> + 999]; khung cấp <see cref="IdBase"/> theo vị trí trang nên trang không bao giờ trùng id với trang khác.
/// </summary>
internal sealed class SettingsPageBuilder
{
    private const uint Child = DialogStyles.Child;
    private const uint TabStop = DialogConstants.WsTabStop;
    private const uint StaticStyle = DialogStyles.Child | DialogConstants.SsNoPrefix;
    private const uint BsMultiline = 0x2000;

    private readonly List<DialogItem> _items = [];
    private readonly Stack<List<SettingsLayoutElement>> _containers = new();
    private readonly List<SettingsLayoutElement> _root = [];
    private readonly Dictionary<int, Func<string>> _texts = [];
    private readonly HashSet<int> _notes = [];
    private readonly Dictionary<int, IReadOnlyList<Func<string>>> _comboItems = [];
    private readonly Dictionary<int, (int Min, int Max)> _sliders = [];
    private int _counter;

    internal SettingsPageBuilder(int idBase)
    {
        IdBase = idBase;
        _containers.Push(_root);
    }

    /// <summary>Id đầu của dải id của trang (id điều khiển = IdBase + 1, + 2, ...).</summary>
    public int IdBase { get; }

    internal IReadOnlyList<DialogItem> Items => _items;

    internal IReadOnlyList<SettingsLayoutElement> Elements => _root;

    internal IReadOnlyDictionary<int, Func<string>> Texts => _texts;

    internal IReadOnlySet<int> Notes => _notes;

    internal IReadOnlyDictionary<int, IReadOnlyList<Func<string>>> ComboItems => _comboItems;

    internal IReadOnlyDictionary<int, (int Min, int Max)> Sliders => _sliders;

    private int Add(DialogControlClass controlClass, Func<string>? text, uint style, uint exStyle = 0)
    {
        var id = IdBase + (++_counter);
        if (_counter > 999)
        {
            throw new InvalidOperationException("A settings page can declare at most 999 controls.");
        }

        if (text is not null && controlClass == DialogControlClass.Button)
        {
            // Button-class texts treat '&' as a mnemonic prefix: "Display & performance" must show the ampersand.
            var plain = text;
            text = () => plain().Replace("&", "&&", StringComparison.Ordinal);
        }

        _items.Add(new DialogItem(controlClass, id, text?.Invoke() ?? string.Empty, style, exStyle));
        if (text is not null)
        {
            _texts[id] = text;
        }

        return id;
    }

    private void AddElement(SettingsLayoutElement element) => _containers.Peek().Add(element);

    /// <summary>Nhóm có tiêu đề (GroupBox); <paramref name="content"/> khai báo các phần tử bên trong. Trả id GroupBox (để ẩn/hiện nhóm).</summary>
    public int Group(Func<string> title, Action<SettingsPageBuilder> content)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(content);
        var id = Add(DialogControlClass.Button, title, Child | ControlConstants.BsGroupBox);
        var children = new List<SettingsLayoutElement>();
        _containers.Push(children);
        try
        {
            content(this);
        }
        finally
        {
            _containers.Pop();
        }

        AddElement(new SettingsGroupElement(id, children));
        return id;
    }

    /// <summary>Ô tick (BS_AUTOCHECKBOX, tự xuống dòng); <paramref name="indentDlu"/> = thụt vào của ô phụ thuộc.</summary>
    public int Check(Func<string> text, int indentDlu = 0)
    {
        var id = Add(DialogControlClass.Button, text, Child | TabStop | ControlConstants.BsAutoCheckBox | BsMultiline);
        AddElement(new SettingsCheckElement(id, indentDlu));
        return id;
    }

    /// <summary>Hàng nhãn + combobox chọn một (danh sách tĩnh theo thứ tự mục = chỉ số của <c>SettingsFormState</c>).</summary>
    public SettingsRow Combo(Func<string> label, IReadOnlyList<Func<string>>? items = null, int widthDlu = 0)
    {
        var labelId = Add(DialogControlClass.Static, label, StaticStyle);
        var comboId = Add(DialogControlClass.ComboBox, null, Child | ComboBoxControl.DefaultStyle);
        if (items is not null)
        {
            _comboItems[comboId] = items;
        }

        AddElement(new SettingsRowElement(labelId, comboId, SettingsRowKind.Combo, widthDlu));
        return new SettingsRow(labelId, comboId);
    }

    /// <summary>Hàng nhãn + ô nhập một dòng (số hoặc chữ; kiểm hợp lệ do presenter khi Save).</summary>
    public SettingsRow Edit(Func<string> label, int widthDlu = 60)
    {
        var labelId = Add(DialogControlClass.Static, label, StaticStyle);
        var editId = Add(DialogControlClass.Edit, null, Child | TabStop | DialogConstants.EsAutoHScroll, DialogConstants.WsExClientEdge);
        AddElement(new SettingsRowElement(labelId, editId, SettingsRowKind.Edit, widthDlu));
        return new SettingsRow(labelId, editId);
    }

    /// <summary>Hàng nhãn + thanh trượt [<paramref name="min"/>, <paramref name="max"/>] + chữ giá trị (trang đặt chữ giá trị).</summary>
    public SettingsSliderRow Slider(Func<string> label, int min, int max)
    {
        var labelId = Add(DialogControlClass.Static, label, StaticStyle);
        var sliderId = Add(DialogControlClass.Trackbar, null, Child | TabStop | ControlConstants.TbsNoTicks);
        var valueId = Add(DialogControlClass.Static, null, StaticStyle); // the page sets the value text (Load / OnScroll)
        _sliders[sliderId] = (min, max);
        AddElement(new SettingsSliderElement(labelId, sliderId, valueId));
        return new SettingsSliderRow(labelId, sliderId, valueId);
    }

    /// <summary>Ghi chú mờ (gợi ý) thẳng cột điều khiển (<paramref name="aligned"/>) hoặc toàn bề rộng; <paramref name="text"/> null = trang đặt chữ sau.</summary>
    public int Note(Func<string>? text, bool aligned = true)
    {
        var id = Add(DialogControlClass.Static, text, StaticStyle);
        _notes.Add(id);
        AddElement(new SettingsTextElement(id, aligned));
        return id;
    }

    /// <summary>Chữ thường toàn bề rộng (trạng thái, mô tả); <paramref name="text"/> null = trang đặt chữ sau.</summary>
    public int Text(Func<string>? text = null)
    {
        var id = Add(DialogControlClass.Static, text, StaticStyle);
        AddElement(new SettingsTextElement(id, alignedToControls: false));
        return id;
    }

    /// <summary>Dãy nút bấm (xuống dòng khi hết chỗ); trả id theo thứ tự khai báo.</summary>
    public IReadOnlyList<int> Buttons(bool aligned, params Func<string>[] texts)
    {
        var ids = texts.Select(text => Add(DialogControlClass.Button, text, Child | TabStop | DialogConstants.BsPushButton)).ToArray();
        AddElement(new SettingsButtonsElement(ids, aligned));
        return ids;
    }

    /// <summary>Dãy ô tick xuống dòng như WrapPanel; trả id theo thứ tự khai báo.</summary>
    public IReadOnlyList<int> CheckFlow(IReadOnlyList<Func<string>> texts, int indentDlu = 0)
    {
        var ids = texts.Select(text => Add(DialogControlClass.Button, text, Child | TabStop | ControlConstants.BsAutoCheckBox)).ToArray();
        AddElement(new SettingsCheckFlowElement(ids, indentDlu));
        return ids;
    }
}
