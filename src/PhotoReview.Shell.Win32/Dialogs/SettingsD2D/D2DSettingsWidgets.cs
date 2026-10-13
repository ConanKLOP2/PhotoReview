namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>Hình chữ nhật nguyên (pixel thiết bị). Toàn bộ bố cục của trang tự vẽ tính bằng px nguyên để không có toạ độ lẻ khi cuộn.</summary>
internal readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PxRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public bool Intersects(PxRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public PxRect Intersect(PxRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? new PxRect(x, y, 0, 0) : new PxRect(x, y, r - x, b - y);
    }

    public PxRect Inflate(int d) => new(X - d, Y - d, Width + (2 * d), Height + (2 * d));
}

/// <summary>Vai trò trợ năng (khớp UIA ControlType).</summary>
internal enum D2DRole
{
    Window,
    Group,
    CheckBox,
    Text,
    Edit,
    ComboBox,
    Slider,
    Button,
    ScrollBar,
}

/// <summary>Cách làm ô nhập/combobox: tự vẽ hoàn toàn (c2) hay control Win32 thật đặt chồng lên bề mặt (c1, "lai").</summary>
internal enum D2DEditMode
{
    SelfDrawn,
    HostedWin32,
}

/// <summary>Một phần tử của trang tự vẽ (không HWND). Bố cục viết <see cref="Bounds"/> theo toạ độ NỘI DUNG (chưa trừ offset cuộn).</summary>
internal abstract class D2DWidget(string id, Func<string> label)
{
    /// <summary>Id ổn định (tên thuộc tính của <c>SettingsFormState</c>) cho test và UIA AutomationId.</summary>
    public string Id { get; } = id;

    public Func<string> Label { get; } = label;

    public bool Enabled { get; set; } = true;

    public bool Visible { get; set; } = true;

    public PxRect Bounds { get; set; }

    public abstract D2DRole Role { get; }

    public virtual bool Focusable => false;

    public string Name => Label();

    /// <summary>Tên trợ năng riêng (khoá <c>settings.*.automationName</c>), mặc định = <see cref="Name"/>.</summary>
    public Func<string>? AutomationLabel { get; set; }

    public string AccessibleName => AutomationLabel?.Invoke() ?? Name;
}

internal sealed class D2DGroupWidget(string id, Func<string> title) : D2DWidget(id, title)
{
    public List<D2DWidget> Children { get; } = [];

    public override D2DRole Role => D2DRole.Group;
}

internal sealed class D2DCheckWidget(string id, Func<string> label, int indentDip = 0) : D2DWidget(id, label)
{
    public bool Checked { get; set; }

    public int IndentDip { get; } = indentDip;

    public override D2DRole Role => D2DRole.CheckBox;

    public override bool Focusable => Enabled && Visible;
}

internal sealed class D2DNoteWidget(string id, Func<string> text, bool aligned = true) : D2DWidget(id, text)
{
    public bool Aligned { get; } = aligned;

    public override D2DRole Role => D2DRole.Text;
}

/// <summary>Nhãn + ô nhập một dòng (+ đơn vị, ví dụ "ms").</summary>
internal sealed class D2DFieldWidget(string id, Func<string> label, int widthDip, string? unit, bool numericOnly = true, bool allowDecimal = false)
    : D2DWidget(id, label)
{
    public D2DTextBox Box { get; } = new(numericOnly, 9, allowDecimal);

    public int WidthDip { get; } = widthDip;

    public string? Unit { get; } = unit;

    public PxRect LabelBounds { get; set; }

    public PxRect BoxBounds { get; set; }

    public PxRect UnitBounds { get; set; }

    public override D2DRole Role => D2DRole.Edit;

    public override bool Focusable => Enabled && Visible;
}

internal sealed class D2DComboWidget(string id, Func<string> label, IReadOnlyList<Func<string>> items, int widthDip = 0) : D2DWidget(id, label)
{
    public IReadOnlyList<Func<string>> Items { get; } = items;

    public int Selected { get; set; }

    public int WidthDip { get; } = widthDip;

    public PxRect LabelBounds { get; set; }

    public PxRect BoxBounds { get; set; }

    public override D2DRole Role => D2DRole.ComboBox;

    public override bool Focusable => Enabled && Visible;

    public string SelectedText => Selected >= 0 && Selected < Items.Count ? Items[Selected]() : string.Empty;
}

internal sealed class D2DSliderWidget(string id, Func<string> label, int min, int max, Func<int, string> valueText) : D2DWidget(id, label)
{
    public int Min { get; } = min;

    public int Max { get; } = max;

    public int Value { get; set; } = min;

    public Func<int, string> ValueText { get; } = valueText;

    public PxRect LabelBounds { get; set; }

    public PxRect TrackBounds { get; set; }

    public PxRect ValueBounds { get; set; }

    public override D2DRole Role => D2DRole.Slider;

    public override bool Focusable => Enabled && Visible;
}

/// <summary>Dãy ô tick xếp cột (EXIF, thanh tiêu đề): bố cục chia cột theo độ rộng.</summary>
internal sealed class D2DCheckFlowWidget(string id, int indentDip) : D2DWidget(id, () => id)
{
    public List<D2DCheckWidget> Checks { get; } = [];

    public int IndentDip { get; } = indentDip;

    public override D2DRole Role => D2DRole.Group;
}

/// <summary>Nút ở chân (cố định, ngoài vùng cuộn).</summary>
internal sealed class D2DButtonWidget(string id, Func<string> label, bool isDefault = false) : D2DWidget(id, label)
{
    public bool IsDefault { get; } = isDefault;

    public override D2DRole Role => D2DRole.Button;

    public override bool Focusable => Enabled && Visible;
}

/// <summary>Nút trợ năng (đầu vào của UIA/Narrator): tên, vai trò, trạng thái, hình chữ nhật client.</summary>
internal sealed record D2DAccessibleNode(
    string AutomationId, string Name, D2DRole Role, PxRect ClientBounds, bool IsEnabled, bool IsFocusable, bool HasFocus, bool? IsChecked,
    string? Value, bool? IsExpanded, int Level);
