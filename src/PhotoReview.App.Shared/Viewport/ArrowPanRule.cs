namespace PhotoReview.App.Viewport;

/// <summary>Kết quả một lần nhấn mũi tên khi đang zoom (cùng nghĩa với <c>KeyboardPanResult</c> của App).</summary>
internal enum ArrowPanOutcome
{
    NotScrollable = 0,
    AtEdge = 1,
    Panned = 2,
}

/// <summary>
/// WP-16: quy tắc bước pan bằng phím mũi tên cho engine thuần - đặt riêng ở đây để đổi một chỗ.
/// <para><b>Quy tắc MỚI (chủ dự án yêu cầu, lead báo 2026-10-10):</b> bước theo DIP BẰNG NHAU ở hai trục =
/// <c>ArrowPanStepPercent</c>% x min(ViewportWidth, ViewportHeight). Quy tắc cũ của bản WPF (<c>KeyboardPan.Step</c> trong
/// App/Input/KineticPan.cs: mỗi trục x kích thước viewport của trục đó) sẽ được đổi theo ở một PR riêng; golden G-INPUT cũ về
/// pan phím không phải chuẩn cho quy tắc này.</para>
/// <para>Xung lượng kinetic tương ứng: <c>PointerInputController</c> tính vận tốc từ quãng thực đi được
/// (<c>target - current</c>, <c>KeyboardPan.ImpulseVelocity</c>), nên dùng <see cref="Step"/> là xung lượng tự theo bước mới.</para>
/// Ngưỡng 0,5 DIP (trục không cuộn được / đã ở mép) giống <c>KeyboardPan</c>.
/// </summary>
internal static class ArrowPanRule
{
    /// <summary>Phần viewport mặc định (AppSettings.DefaultArrowPanStepPercent = 10).</summary>
    public const double DefaultStepFraction = 0.1;

    private const double Epsilon = 0.5;

    /// <summary>Độ dài một bước (DIP), như nhau cho cả hai trục; 0 khi viewport chưa đo hoặc tham số không hợp lệ.</summary>
    public static double StepLength(double viewportWidth, double viewportHeight, double stepFraction)
    {
        var shorter = Math.Min(viewportWidth, viewportHeight);
        var length = shorter * stepFraction;
        return double.IsFinite(length) && length > 0 ? length : 0;
    }

    /// <summary>
    /// Một lần nhấn theo hướng (<paramref name="dx"/>, <paramref name="dy"/>) (một trong hai khác 0, giá trị -1/1): offset đích
    /// đã kẹp vào [0, Max] của <paramref name="layout"/>.
    /// </summary>
    public static (ArrowPanOutcome Outcome, double Horizontal, double Vertical) Step(int dx, int dy, double horizontal, double vertical,
        in ViewportLayout layout, double stepFraction = DefaultStepFraction)
    {
        var horizontalAxis = dx != 0;
        var max = horizontalAxis ? layout.MaxHorizontalOffset : layout.MaxVerticalOffset;
        if (max <= Epsilon) return (ArrowPanOutcome.NotScrollable, horizontal, vertical);
        var current = horizontalAxis ? horizontal : vertical;
        var step = StepLength(layout.ViewportWidth, layout.ViewportHeight, stepFraction) * (dx + dy);
        var target = Math.Clamp(current + step, 0, max);
        if (Math.Abs(target - current) < Epsilon) return (ArrowPanOutcome.AtEdge, horizontal, vertical);
        return horizontalAxis ? (ArrowPanOutcome.Panned, target, vertical) : (ArrowPanOutcome.Panned, horizontal, target);
    }
}
