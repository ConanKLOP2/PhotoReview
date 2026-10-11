using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>Đo chữ tất định: rộng = số ký tự x (cỡ chữ / 2), cao = cỡ chữ x 1,5; cắt "…" nếu vượt maxWidth.</summary>
internal sealed class FakeTextRenderer : ITextRenderer
{
    public List<(string Text, TextStyle Style, double MaxWidth, TextTrimming Trimming)> Requests { get; } = [];

    public int Live { get; private set; }

    public int Disposed { get; private set; }

    public ITextLayout CreateLayout(string text, TextStyle style, double maxWidth, TextTrimming trimming)
    {
        Requests.Add((text, style, maxWidth, trimming));
        double charWidth = style.FontSizeDip / 2;
        double width = text.Length * charWidth;
        if (trimming == TextTrimming.CharacterEllipsis && width > maxWidth)
        {
            width = Math.Max(0, Math.Floor(maxWidth / charWidth) * charWidth);
        }

        Live++;
        return new FakeLayout(this, text, new SizeD(width, style.FontSizeDip * 1.5));
    }

    private sealed class FakeLayout(FakeTextRenderer owner, string text, SizeD size) : ITextLayout
    {
        private bool _disposed;

        public SizeD Size => size;

        public string Text => text;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Live--;
            owner.Disposed++;
        }
    }
}

/// <summary>Ghi lại lệnh vẽ (loại + tham số chính) để test thứ tự, opacity và clip.</summary>
internal sealed class RecordingDrawContext : IDrawContext
{
    public List<string> Calls { get; } = [];

    public List<(RectD Rect, double Radius, ColorF Color, float Opacity)> Fills { get; } = [];

    public List<(string Text, PointD Origin, ColorF Color, float Opacity)> Texts { get; } = [];

    public int OpacityDepth { get; private set; }

    public int ClipDepth { get; private set; }

    private readonly Stack<float> _opacity = new([1f]);

    public void Clear(ColorF color) => Calls.Add("Clear");

    public void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f) =>
        Calls.Add("DrawImage");

    public void FillRectangle(RectD rect, ColorF color)
    {
        Calls.Add("FillRectangle");
        Fills.Add((rect, 0, color, _opacity.Peek()));
    }

    public void FillRoundedRectangle(RectD rect, double radius, ColorF color)
    {
        Calls.Add("FillRoundedRectangle");
        Fills.Add((rect, radius, color, _opacity.Peek()));
    }

    public void DrawRectangle(RectD rect, ColorF color, double strokeWidth) => Calls.Add("DrawRectangle");

    public void DrawText(ITextLayout layout, PointD origin, ColorF color)
    {
        Calls.Add("DrawText");
        Texts.Add((layout.Text, origin, color, _opacity.Peek()));
    }

    public void PushClip(RectD rect)
    {
        Calls.Add("PushClip");
        ClipDepth++;
    }

    public void PopClip()
    {
        Calls.Add("PopClip");
        ClipDepth--;
    }

    public void PushOpacity(float opacity)
    {
        Calls.Add("PushOpacity");
        OpacityDepth++;
        _opacity.Push(_opacity.Peek() * opacity);
    }

    public void PopOpacity()
    {
        Calls.Add("PopOpacity");
        OpacityDepth--;
        _opacity.Pop();
    }

    public bool TryReadPixels(out PixelBuffer pixels)
    {
        pixels = null!;
        return false;
    }
}
