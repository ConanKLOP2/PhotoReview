namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>
/// WP-28 (G-PIX): a frame of opaque BGRA8 pixels (B, G, R, 255 per pixel, row-major, no row padding). The common currency of the
/// WPF recorder (RenderTargetBitmap), the Win32 shell test (D2D offscreen read-back) and the metrics: it carries no WPF or D2D type,
/// so the same comparison code runs on both sides.
/// </summary>
public sealed class PixelFrame
{
    public PixelFrame(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes for {width}x{height}, got {bgra.Length}.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>B, G, R, A per pixel. A is always 255 for a rendered frame.</summary>
    public byte[] Bgra { get; }

    public static PixelFrame Solid(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = 255;
        }

        return new PixelFrame(width, height, data);
    }

    public (byte B, byte G, byte R) At(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (Bgra[i], Bgra[i + 1], Bgra[i + 2]);
    }

    /// <summary>A copy with the pixel at (<paramref name="x"/>, <paramref name="y"/>) replaced (test helper).</summary>
    public PixelFrame With(int x, int y, byte b, byte g, byte r)
    {
        var copy = (byte[])Bgra.Clone();
        var i = ((y * Width) + x) * 4;
        copy[i] = b;
        copy[i + 1] = g;
        copy[i + 2] = r;
        return new PixelFrame(Width, Height, copy);
    }
}

/// <summary>Integer pixel rectangle (frame coordinates).</summary>
public readonly record struct FrameRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public FrameRect Intersect(FrameRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new FrameRect(x, y, right - x, bottom - y) : default;
    }

    public FrameRect Inflate(int amount) => new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}
