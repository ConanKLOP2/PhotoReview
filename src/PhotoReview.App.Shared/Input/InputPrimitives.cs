namespace PhotoReview.App.Input;

// C-06 (NO-WPF-EXEC-PLAN mục 5, đóng băng ở WP-01): kiểu nhập liệu không-WPF. Toạ độ là DIP trừ khi tên có "Pixel".
// Phần thực thi (Contains, ánh xạ phím) thuộc WP-07.

/// <summary>Điểm (DIP) thay <c>System.Windows.Point</c> ở ranh giới Input.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>Kích thước (DIP) thay <c>System.Windows.Size</c>.</summary>
public readonly record struct SizeD(double Width, double Height);

/// <summary>Hình chữ nhật (DIP) thay <c>System.Windows.Rect</c>.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>WP-07 thực thi (ngữ nghĩa như <c>System.Windows.Rect.Contains</c>).</summary>
    public bool Contains(PointD p) => throw new NotImplementedException();
}

/// <summary>Giá trị = <c>System.Windows.Input.MouseButton</c>.</summary>
public enum PointerButton
{
    Left = 0,
    Middle = 1,
    Right = 2,
    XButton1 = 3,
    XButton2 = 4,
}

/// <summary>Giá trị = <c>System.Windows.Input.ModifierKeys</c>.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}
