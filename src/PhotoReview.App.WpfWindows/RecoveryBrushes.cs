using System.Windows.Media;
using PhotoReview.App.Presenters;

namespace PhotoReview.App;

/// <summary>The WPF paint for <see cref="RecoveryTone"/>: frozen brushes (shared statics, usable from any thread).</summary>
internal static class RecoveryBrushes
{
    private static readonly Brush Good = Frozen(RecoveryTone.Good);
    private static readonly Brush Info = Frozen(RecoveryTone.Info);
    private static readonly Brush Warn = Frozen(RecoveryTone.Warn);
    private static readonly Brush Bad = Frozen(RecoveryTone.Bad);
    private static readonly Brush Neutral = Frozen(RecoveryTone.Neutral);

    private static SolidColorBrush Frozen(RecoveryTone tone)
    {
        var (r, g, b) = RecoveryPresentation.Rgb(tone);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush NeutralBrush => Neutral;

    public static Brush For(RecoveryTone tone) => tone switch
    {
        RecoveryTone.Good => Good,
        RecoveryTone.Info => Info,
        RecoveryTone.Warn => Warn,
        RecoveryTone.Bad => Bad,
        _ => Neutral,
    };
}
