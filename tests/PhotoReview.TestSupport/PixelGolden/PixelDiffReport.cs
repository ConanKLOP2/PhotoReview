namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>
/// WP-28: the diff image written when a G-PIX case misses its bar. One PNG: expected | actual | difference (per-channel absolute
/// difference x 4, saturated, on black), separated by 4 px white gutters, so the reader sees where and how the renderers differ.
/// </summary>
public static class PixelDiffReport
{
    private const int Gutter = 4;
    private const int Gain = 4;

    /// <summary>Writes <c>{directory}/{name}-diff.png</c> and returns its path.</summary>
    public static string Write(string directory, string name, PixelFrame expected, PixelFrame actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var path = Path.Combine(directory, SafeName(name) + "-diff.png");
        PngFile.Write(path, Compose(expected, actual));
        return path;
    }

    public static PixelFrame Compose(PixelFrame expected, PixelFrame actual)
    {
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        var total = (width * 3) + (Gutter * 2);
        var data = new byte[total * height * 4];
        Array.Fill(data, (byte)255); // white gutters and padding
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inExpected = x < expected.Width && y < expected.Height;
                var inActual = x < actual.Width && y < actual.Height;
                var target = ((y * total) + x) * 4;
                if (inExpected)
                {
                    Copy(expected, x, y, data, target);
                }

                if (inActual)
                {
                    Copy(actual, x, y, data, target + ((width + Gutter) * 4));
                }

                var diff = ((y * total) + x + ((width + Gutter) * 2)) * 4;
                if (inExpected && inActual)
                {
                    var e = ((y * expected.Width) + x) * 4;
                    var a = ((y * actual.Width) + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        data[diff + c] = (byte)Math.Min(255, Math.Abs(expected.Bgra[e + c] - actual.Bgra[a + c]) * Gain);
                    }

                    data[diff + 3] = 255;
                }
                else
                {
                    data[diff] = 0;
                    data[diff + 1] = 0;
                    data[diff + 2] = 255; // outside one of the frames: solid red
                    data[diff + 3] = 255;
                }
            }
        }

        return new PixelFrame(total, height, data);
    }

    private static void Copy(PixelFrame source, int x, int y, byte[] target, int targetIndex)
    {
        var i = ((y * source.Width) + x) * 4;
        target[targetIndex] = source.Bgra[i];
        target[targetIndex + 1] = source.Bgra[i + 1];
        target[targetIndex + 2] = source.Bgra[i + 2];
        target[targetIndex + 3] = 255;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)]);
    }
}
