using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP-15: ảnh thử + ảnh tham chiếu CPU + PSNR cho test renderer. PSNR dùng đúng công thức của
/// <c>TestSupport.Windows.PixelAssert.Psnr</c> (đỉnh 255, kênh B,G,R + A khi cả hai Pbgra32); không tham chiếu helper đó vì
/// TestSupport.Windows kéo WPF vào Shell.Tests (dự án test không-WPF).
/// </summary>
internal static class RenderTestImages
{
    /// <summary>Ảnh "giống ảnh chụp": nền biến thiên chậm (sin/gradient) + vài cạnh sắc. Pbgra32 alpha 255 hoặc Bgr32 (X=0x55).</summary>
    public static PixelBuffer CreateSmooth(int width, int height, PixelLayout layout = PixelLayout.Pbgra32)
    {
        var buffer = PixelBuffer.Allocate(width, height, layout);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.GetRow(y);
            for (int x = 0; x < width; x++)
            {
                double fx = x / (double)width;
                double fy = y / (double)height;
                double r = 128 + (100 * Math.Sin((x / 37.0) + (y / 53.0)));
                double g = 30 + (190 * fx * (1 - (0.5 * fy)));
                double b = 128 + (90 * Math.Cos((x / 71.0) - (y / 29.0)));
                bool box = fx is > 0.3 and < 0.45 && fy is > 0.2 and < 0.7;
                if (box)
                {
                    r = 240;
                    g = 230;
                    b = 20;
                }

                int i = x * 4;
                row[i] = ToByte(b);
                row[i + 1] = ToByte(g);
                row[i + 2] = ToByte(r);
                row[i + 3] = layout == PixelLayout.Pbgra32 ? (byte)255 : (byte)0x55;
            }
        }

        return buffer;
    }

    /// <summary>Nhiễu có hạt giống; Pbgra32 premultiplied hợp lệ (B,G,R &lt;= A), ~1/4 pixel trong suốt một phần.</summary>
    public static PixelBuffer CreateNoise(int width, int height, int seed, bool translucent = true)
    {
        var rng = new Random(seed);
        var buffer = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.GetRow(y);
            rng.NextBytes(row);
            for (int i = 0; i < row.Length; i += 4)
            {
                byte alpha = translucent && rng.Next(4) == 0 ? (byte)rng.Next(256) : (byte)255;
                row[i + 3] = alpha;
                row[i] = (byte)(row[i] * alpha / 255);
                row[i + 1] = (byte)(row[i + 1] * alpha / 255);
                row[i + 2] = (byte)(row[i + 2] * alpha / 255);
            }
        }

        return buffer;
    }

    /// <summary>Ảnh <paramref name="width"/> x <paramref name="height"/> tô một màu (B,G,R,A premultiplied).</summary>
    public static PixelBuffer CreateSolid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var buffer = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.GetRow(y);
            for (int i = 0; i < row.Length; i += 4)
            {
                row[i] = b;
                row[i + 1] = g;
                row[i + 2] = r;
                row[i + 3] = a;
            }
        }

        return buffer;
    }

    /// <summary>Sao vùng (x, y, w, h) của <paramref name="source"/> sang buffer Pbgra32 mới; Bgr32 -&gt; A = 255 (như D2D ALPHA_MODE_IGNORE).</summary>
    public static PixelBuffer Crop(PixelBuffer source, int x, int y, int width, int height)
    {
        var result = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        for (int row = 0; row < height; row++)
        {
            source.GetRow(y + row).Slice(x * 4, width * 4).CopyTo(result.GetRow(row));
            if (source.Layout == PixelLayout.Bgr32)
            {
                Span<byte> target = result.GetRow(row);
                for (int i = 3; i < target.Length; i += 4)
                {
                    target[i] = 255;
                }
            }
        }

        return result;
    }

    /// <summary>Tham chiếu thu nhỏ theo diện tích (box/area-weighted, cùng họ với WIC/WPF Fant): mỗi pixel đích = trung bình có trọng số vùng nguồn phủ.</summary>
    public static PixelBuffer ResizeArea(PixelBuffer source, int width, int height)
    {
        double[] horizontal = new double[(long)width * source.Height * 4];
        double sx = source.Width / (double)width;
        for (int y = 0; y < source.Height; y++)
        {
            ReadOnlySpan<byte> row = source.GetRow(y);
            for (int dx = 0; dx < width; dx++)
            {
                double start = dx * sx;
                double end = start + sx;
                double b = 0, g = 0, r = 0, a = 0;
                for (int x = (int)start; x < Math.Min(source.Width, (int)Math.Ceiling(end)); x++)
                {
                    double weight = Math.Min(end, x + 1) - Math.Max(start, x);
                    int i = x * 4;
                    b += row[i] * weight;
                    g += row[i + 1] * weight;
                    r += row[i + 2] * weight;
                    a += (source.Layout == PixelLayout.Bgr32 ? 255 : row[i + 3]) * weight;
                }

                long o = (((long)y * width) + dx) * 4;
                horizontal[o] = b / sx;
                horizontal[o + 1] = g / sx;
                horizontal[o + 2] = r / sx;
                horizontal[o + 3] = a / sx;
            }
        }

        var result = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        double sy = source.Height / (double)height;
        for (int dy = 0; dy < height; dy++)
        {
            double start = dy * sy;
            double end = start + sy;
            Span<byte> target = result.GetRow(dy);
            for (int dx = 0; dx < width; dx++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (int y = (int)start; y < Math.Min(source.Height, (int)Math.Ceiling(end)); y++)
                    {
                        double weight = Math.Min(end, y + 1) - Math.Max(start, y);
                        sum += horizontal[(((long)y * width) + dx) * 4 + c] * weight;
                    }

                    target[(dx * 4) + c] = ToByte(sum / sy);
                }
            }
        }

        return result;
    }

    /// <summary>Tham chiếu song tuyến (D2D LINEAR: tâm pixel, kẹp mép) cho phóng to.</summary>
    public static PixelBuffer ResizeBilinear(PixelBuffer source, int width, int height)
    {
        var result = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        double sx = source.Width / (double)width;
        double sy = source.Height / (double)height;
        for (int dy = 0; dy < height; dy++)
        {
            double fy = Math.Clamp(((dy + 0.5) * sy) - 0.5, 0, source.Height - 1);
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            double wy = fy - y0;
            ReadOnlySpan<byte> row0 = source.GetRow(y0);
            ReadOnlySpan<byte> row1 = source.GetRow(y1);
            Span<byte> target = result.GetRow(dy);
            for (int dx = 0; dx < width; dx++)
            {
                double fx = Math.Clamp(((dx + 0.5) * sx) - 0.5, 0, source.Width - 1);
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                double wx = fx - x0;
                for (int c = 0; c < 4; c++)
                {
                    double top = (row0[(x0 * 4) + c] * (1 - wx)) + (row0[(x1 * 4) + c] * wx);
                    double bottom = (row1[(x0 * 4) + c] * (1 - wx)) + (row1[(x1 * 4) + c] * wx);
                    target[(dx * 4) + c] = ToByte((top * (1 - wy)) + (bottom * wy));
                }
            }
        }

        return result;
    }

    /// <summary>PSNR (dB) như <c>PixelAssert.Psnr</c>; giống hệt -&gt; +vô cực.</summary>
    public static double Psnr(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int channels = expected.Layout == PixelLayout.Pbgra32 && actual.Layout == PixelLayout.Pbgra32 ? 4 : 3;
        double squares = 0;
        long count = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> e = expected.GetRow(y);
            ReadOnlySpan<byte> a = actual.GetRow(y);
            for (int i = 0; i < e.Length; i += 4)
            {
                for (int c = 0; c < channels; c++)
                {
                    int d = e[i + c] - a[i + c];
                    squares += d * d;
                    count++;
                }
            }
        }

        return squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squares / count));
    }

    /// <summary>Giống từng byte; báo pixel lệch đầu tiên.</summary>
    public static void AssertIdentical(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> e = expected.GetRow(y);
            ReadOnlySpan<byte> a = actual.GetRow(y);
            if (e.SequenceEqual(a))
            {
                continue;
            }

            for (int i = 0; i < e.Length; i++)
            {
                Assert.True(e[i] == a[i], $"pixel ({i / 4}, {y}) channel {"BGRA"[i % 4]}: expected {e[i]}, got {a[i]}");
            }
        }
    }

    /// <summary>Đọc một pixel (B, G, R, A).</summary>
    public static (byte B, byte G, byte R, byte A) Pixel(PixelBuffer buffer, int x, int y)
    {
        ReadOnlySpan<byte> row = buffer.GetRow(y);
        return (row[x * 4], row[(x * 4) + 1], row[(x * 4) + 2], row[(x * 4) + 3]);
    }

    /// <summary>Vẽ một khung bằng <paramref name="draw"/> rồi đọc lại toàn bộ đích.</summary>
    public static PixelBuffer Render(D2DRenderSurface surface, Action<D2DDrawContext> draw)
    {
        D2DDrawContext dc = surface.BeginDrawContext();
        PixelBuffer? pixels;
        try
        {
            draw(dc);
            Assert.True(dc.TryReadPixels(out pixels));
        }
        finally
        {
            Assert.Equal(PresentResult.Presented, surface.EndDrawAndPresent());
        }

        return pixels!;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

/// <summary>Dispatcher thủ công: việc được xếp hàng, test chạy chúng bằng <see cref="RunAll"/> (không thread, không chờ thời gian).</summary>
internal sealed class ManualDispatcher : IUiDispatcher
{
    private readonly Queue<(Action Action, UiPriority Priority)> _queue = new();

    public int PendingCount => _queue.Count;

    public List<UiPriority> PostedPriorities { get; } = [];

    public bool CheckAccess() => true;

    public void Post(Action action) => Post(action, UiPriority.Normal);

    public void Post(Action action, UiPriority priority)
    {
        PostedPriorities.Add(priority);
        _queue.Enqueue((action, priority));
    }

    public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

    public Task InvokeAsync(Action action, UiPriority priority)
    {
        action();
        return Task.CompletedTask;
    }

    public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Chạy đúng một việc; false nếu hàng rỗng.</summary>
    public bool RunOne()
    {
        if (!_queue.TryDequeue(out var item))
        {
            return false;
        }

        item.Action();
        return true;
    }

    /// <summary>Chạy tới khi hàng rỗng (kể cả việc mới được post trong lúc chạy); trả số việc đã chạy.</summary>
    public int RunAll(int limit = 1_000_000)
    {
        int count = 0;
        while (count < limit && RunOne())
        {
            count++;
        }

        return count;
    }
}
