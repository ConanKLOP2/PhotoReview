using PhotoReview.App.Input;

namespace PhotoReview.Shell.Rendering;

// C-11 phần text (NO-WPF-EXEC-PLAN mục 5): DirectWrite. Thực thi ở WP-17.

public enum TextTrimming
{
    None = 0,
    CharacterEllipsis = 1,
}

public sealed record TextStyle(string FontFamily = "Segoe UI", double FontSizeDip = 12, bool Bold = false);

public interface ITextLayout : IDisposable
{
    SizeD Size { get; }

    string Text { get; }
}

/// <summary>DirectWrite; cache layout theo (text, style, maxWidth, trimming).</summary>
public interface ITextRenderer
{
    ITextLayout CreateLayout(string text, TextStyle style, double maxWidth, TextTrimming trimming);
}
