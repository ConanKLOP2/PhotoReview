namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>
/// WP-28: the source pictures of the G-PIX cases, generated procedurally (integer arithmetic only, so the bytes are identical on
/// every machine and nothing but the WPF reference frames is committed). The WPF recorder and the Win32 shell test build the SAME
/// pixels from the same call. Opaque BGRA8.
/// <list type="bullet">
/// <item><c>photo</c>: smooth colour gradients + a low-contrast texture + a few hard-edged shapes (a photograph's statistics).</item>
/// <item><c>chart</c>: worst case for a resampler - 1 px lines, 2 px checkers, ramps and colour bars (separates Fant from cubic).</item>
/// <item><c>orient</c>: an asymmetric picture (corner colours + an F glyph) that exposes any wrong EXIF orientation mapping.</item>
/// </list>
/// </summary>
public static class PixelGoldenScenes
{
    public const string Photo = "photo";
    public const string Chart = "chart";
    public const string Orient = "orient";

    /// <summary>The picture (<paramref name="width"/> x <paramref name="height"/>, opaque BGRA8, row-major).</summary>
    public static PixelFrame Create(string scene, int width, int height) => scene switch
    {
        Photo => Build(width, height, PhotoPixel),
        Chart => Build(width, height, ChartPixel),
        Orient => Build(width, height, OrientPixel),
        _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown G-PIX scene."),
    };

    private delegate (int R, int G, int B) PixelFunction(int x, int y, int width, int height);

    private static PixelFrame Build(int width, int height, PixelFunction function)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = function(x, y, width, height);
                var i = ((y * width) + x) * 4;
                data[i] = (byte)Math.Clamp(b, 0, 255);
                data[i + 1] = (byte)Math.Clamp(g, 0, 255);
                data[i + 2] = (byte)Math.Clamp(r, 0, 255);
                data[i + 3] = 255;
            }
        }

        return new PixelFrame(width, height, data);
    }

    // Smooth part: bilinear colour field (corners differ) + a diagonal sweep; low-contrast texture from a 64-entry integer
    // triangle wave; hard-edged ellipse, bar and stripe so edges exist at every scale.
    private static (int R, int G, int B) PhotoPixel(int x, int y, int width, int height)
    {
        var fx = (x * 1024L) / Math.Max(1, width - 1);   // 0..1024
        var fy = (y * 1024L) / Math.Max(1, height - 1);
        var r = (int)(40 + ((fx * 160) >> 10) + ((fy * 30) >> 10));
        var g = (int)(60 + ((fy * 120) >> 10) + ((((fx + fy) & 1023) * 40) >> 10));
        var b = (int)(200 - ((fx * 120) >> 10) + ((fy * 40) >> 10));

        var texture = Triangle((x * 3) + y) + Triangle(x - (2 * y)) - 32; // about -16..+16
        r += texture / 2;
        g += texture / 3;
        b += texture / 2;

        // Ellipse (centre 38 % / 45 %, radii 14 % / 22 %): warm, hard edge.
        var ex = x - (width * 38 / 100);
        var ey = y - (height * 45 / 100);
        var rx = Math.Max(2, width * 14 / 100);
        var ry = Math.Max(2, height * 22 / 100);
        if ((((long)ex * ex * ry * ry) + ((long)ey * ey * rx * rx)) <= (long)rx * rx * ry * ry)
        {
            r = 235;
            g = 190 + (Triangle(x + y) / 2);
            b = 40;
        }

        // Dark bar and bright stripe (1 px / 3 px) to expose ringing and blur.
        if (x > width * 62 / 100 && x < width * 80 / 100 && y > height * 20 / 100 && y < height * 70 / 100)
        {
            r = 18;
            g = 24;
            b = 30;
        }

        if (y == height * 80 / 100 || ((y - (height * 82 / 100)) is >= 0 and < 3))
        {
            r = 245;
            g = 245;
            b = 245;
        }

        return (r, g, b);
    }

    private static int Triangle(int value)
    {
        var v = value & 63;
        return v < 32 ? v : 63 - v;
    }

    private static (int R, int G, int B) ChartPixel(int x, int y, int width, int height)
    {
        // Colour bars along the bottom 12 %: K, R, G, B, C, M, Y, W (channel order check).
        if (y >= height * 88 / 100)
        {
            var bar = Math.Min(7, x * 8 / Math.Max(1, width));
            return bar switch
            {
                0 => (0, 0, 0),
                1 => (255, 0, 0),
                2 => (0, 255, 0),
                3 => (0, 0, 255),
                4 => (0, 255, 255),
                5 => (255, 0, 255),
                6 => (255, 255, 0),
                _ => (255, 255, 255),
            };
        }

        // Horizontal grey ramp strip (band 0..12 %).
        if (y < height * 12 / 100)
        {
            var level = x * 255 / Math.Max(1, width - 1);
            return (level, level, level);
        }

        var cell = (x / 2) + (y / 2);
        var section = x * 4 / Math.Max(1, width);
        switch (section)
        {
            case 0:
                // 2 px checker.
                return (cell & 1) == 0 ? (230, 230, 230) : (25, 25, 25);
            case 1:
                // 1 px vertical lines on mid grey.
                return (x & 1) == 0 ? (250, 80, 40) : (60, 60, 70);
            case 2:
                // 1 px horizontal lines + a diagonal.
                return ((y & 1) == 0) ^ (((x - y) & 7) == 0) ? (40, 200, 240) : (35, 35, 50);
            default:
                // Concentric squares, 3 px wide.
                var d = Math.Max(Math.Abs(x - (width * 7 / 8)), Math.Abs(y - (height / 2)));
                return ((d / 3) & 1) == 0 ? (250, 250, 120) : (70, 20, 110);
        }
    }

    private static (int R, int G, int B) OrientPixel(int x, int y, int width, int height)
    {
        var corner = Math.Max(6, Math.Min(width, height) / 6);
        if (x < corner && y < corner)
        {
            return (230, 30, 30);          // top-left: red
        }

        if (x >= width - corner && y < corner)
        {
            return (30, 200, 60);          // top-right: green
        }

        if (x < corner && y >= height - corner)
        {
            return (40, 60, 230);          // bottom-left: blue
        }

        if (x >= width - corner && y >= height - corner)
        {
            return (245, 245, 245);        // bottom-right: white
        }

        // An "F" glyph (asymmetric under every flip/rotation) in the middle, stroke = a tenth of the shorter side.
        var stroke = Math.Max(3, Math.Min(width, height) / 10);
        var gx = x - (width / 4);
        var gy = y - (height / 5);
        var gw = width / 2;
        var gh = height * 3 / 5;
        if (gx >= 0 && gy >= 0 && gx < gw && gy < gh)
        {
            var stem = gx < stroke;
            var top = gy < stroke;
            var middle = gy >= (gh / 2) - (stroke / 2) && gy < (gh / 2) + (stroke / 2) && gx < gw * 2 / 3;
            if (stem || top || middle)
            {
                return (250, 220, 40);
            }
        }

        // Background: gradient so a mirrored or rotated copy differs everywhere.
        return (20 + (x * 90 / Math.Max(1, width)), 20 + (y * 90 / Math.Max(1, height)), 70);
    }
}
