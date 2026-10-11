using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: đếm số lần nhấn liên tiếp (<c>MouseButtonEventArgs.ClickCount</c> của WPF). Lớp cửa sổ không có CS_DBLCLKS nên không
/// có WM_LBUTTONDBLCLK; số lần nhấn là: cùng nút, cách lần trước không quá <c>GetDoubleClickTime</c>, và nằm trong hình chữ nhật
/// <c>SM_CXDOUBLECLK x SM_CYDOUBLECLK</c> đặt giữa lần nhấn trước. Lần nhấn thứ ba trở đi vẫn tăng (WPF cũng vậy; Fit khi
/// <c>ClickCount &gt;= 2</c>). Thuần, không đọc đồng hồ hay hệ thống (người gọi đưa dấu thời gian và ngưỡng).
/// </summary>
internal sealed class ClickCounter
{
    private bool _has;
    private PointerButton _button;
    private int _x;
    private int _y;
    private int _time;
    private int _count;

    /// <summary>Ghi một lần nhấn; trả số lần nhấn liên tiếp tính cả lần này (>= 1).</summary>
    /// <param name="x">Pixel client.</param>
    /// <param name="y">Pixel client.</param>
    /// <param name="time">Mili giây (GetMessageTime; tràn số nguyên vẫn đúng nhờ phép trừ unchecked).</param>
    /// <param name="doubleClickTimeMs">GetDoubleClickTime.</param>
    /// <param name="slopWidth">SM_CXDOUBLECLK (pixel).</param>
    /// <param name="slopHeight">SM_CYDOUBLECLK (pixel).</param>
    public int Register(PointerButton button, int x, int y, int time, int doubleClickTimeMs, int slopWidth, int slopHeight)
    {
        var elapsed = unchecked(time - _time);
        var continues = _has
            && button == _button
            && elapsed >= 0
            && elapsed <= doubleClickTimeMs
            && Math.Abs(x - _x) <= slopWidth / 2
            && Math.Abs(y - _y) <= slopHeight / 2;
        _count = continues ? _count + 1 : 1;
        _has = true;
        _button = button;
        _x = x;
        _y = y;
        _time = time;
        return _count;
    }

    /// <summary>Quên chuỗi nhấn hiện tại (lần nhấn kế bắt đầu từ 1).</summary>
    public void Reset() => _has = false;

    /// <summary>Test: đặt trạng thái như thể đã có <paramref name="count"/> lần nhấn liên tiếp, lần cuối tại <paramref name="time"/>.</summary>
    internal void Seed(PointerButton button, int x, int y, int time, int count)
    {
        _has = true;
        _button = button;
        _x = x;
        _y = y;
        _time = time;
        _count = count;
    }
}
