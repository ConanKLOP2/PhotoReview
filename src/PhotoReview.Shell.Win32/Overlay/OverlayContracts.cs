using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

// C-11 phần overlay/animation (NO-WPF-EXEC-PLAN mục 5). Thực thi ở WP-17.

public readonly record struct OverlayLayoutContext(SizeD Client, double DpiScale, ITextRenderer Text);

public interface IOverlayElement
{
    /// <summary>Khớp x:Name WPF: "ToolbarPanel", "StatusPanel", "FolderInfoPanel", "ZoomIndicatorPanel", "CapturePairBadge", "ComparePanel".</summary>
    string Id { get; }

    bool IsVisible { get; }

    float Opacity { get; }

    /// <summary>Sau Arrange.</summary>
    RectD Bounds { get; }

    void Arrange(in OverlayLayoutContext context);

    void Render(IDrawContext dc);

    /// <summary>Overlay ẩn/opacity 0 =&gt; false (click-through như IsHitTestVisible=false).</summary>
    bool HitTest(PointD point);
}

/// <summary>Tween opacity theo IFrameClock; FadeTo huỷ tween cũ (như FadeTargetGate).</summary>
public interface IAnimator
{
    void FadeTo(string elementId, float target, TimeSpan duration, Easing easing);

    bool IsAnimating { get; }
}

public enum Easing
{
    Linear = 0,
    QuadraticOut = 1,
}
