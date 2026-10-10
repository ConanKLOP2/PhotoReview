using System.Numerics;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;

namespace PhotoReview.Shell.Rendering;

/// <summary>Màu nền theo theme của app (WP-15). Giá trị = <c>Themes/DarkPalette.xaml</c> của bản WPF.</summary>
public static class RenderColors
{
    /// <summary><c>Dark.Canvas</c> = #101010 (nền vùng ảnh của MainWindow).</summary>
    public static ColorF DarkCanvas { get; } = FromRgb(0x10, 0x10, 0x10);

    /// <summary>Màu 8 bit sRGB (alpha thẳng, như <c>D2D1_COLOR_F</c>) sang <see cref="ColorF"/>.</summary>
    public static ColorF FromRgb(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);
}

/// <summary>Ánh xạ thiết lập của app sang <see cref="ImageInterpolation"/> (C-09).</summary>
public static class ImageInterpolations
{
    /// <summary>
    /// Như <c>ScalingQualityConverter</c> của bản WPF: <c>Linear</c> -&gt; <see cref="ImageInterpolation.Linear"/>,
    /// <c>HighQuality</c> (WPF Fant khi thu nhỏ) -&gt; <see cref="ImageInterpolation.HighQualityCubic"/> (NE-9 a).
    /// </summary>
    public static ImageInterpolation FromScalingQuality(ScalingQuality quality) =>
        quality == ScalingQuality.Linear ? ImageInterpolation.Linear : ImageInterpolation.HighQualityCubic;
}

/// <summary>
/// Ma trận vẽ ảnh theo orientation EXIF (WP-15). Ảnh được upload ở hướng thô (chưa xoay); vẽ
/// <see cref="UnorientedRect"/> dưới ma trận <see cref="ForExifOrientation"/> cho đúng kết quả của
/// <c>PixelOps.ApplyOrientation</c> trong <paramref name="destination"/> (kích thước đã xoay).
/// </summary>
public static class ImageTransforms
{
    /// <summary>5..8 hoán đổi chiều rộng và chiều cao (như <c>ExifOrientation.IsTransposed</c>).</summary>
    public static bool IsTransposed(int exifOrientation) => exifOrientation is >= 5 and <= 8;

    /// <summary>Hình chữ nhật (gốc 0,0) để vẽ ảnh thô trước khi áp <see cref="ForExifOrientation"/>.</summary>
    public static RectD UnorientedRect(int exifOrientation, RectD destination) =>
        IsTransposed(exifOrientation)
            ? new RectD(0, 0, destination.Height, destination.Width)
            : new RectD(0, 0, destination.Width, destination.Height);

    /// <summary>
    /// Ma trận hàng-vector (x' = x*M11 + y*M21 + M31) đưa <see cref="UnorientedRect"/> vào <paramref name="destination"/>
    /// với cùng ánh xạ pixel như <c>PixelOps.ApplyOrientation</c> (giá trị ngoài 1..8 = 1).
    /// </summary>
    public static Matrix3x2 ForExifOrientation(int exifOrientation, RectD destination)
    {
        RectD raw = UnorientedRect(exifOrientation, destination);
        float w = (float)raw.Width;
        float h = (float)raw.Height;

        // Toạ độ liên tục: (u, v) trong ảnh thô W x H -> (x, y) trong ảnh đã xoay (xem PixelOps.CopyTransposed).
        Matrix3x2 orient = exifOrientation switch
        {
            2 => new Matrix3x2(-1, 0, 0, 1, w, 0),
            3 => new Matrix3x2(-1, 0, 0, -1, w, h),
            4 => new Matrix3x2(1, 0, 0, -1, 0, h),
            5 => new Matrix3x2(0, 1, 1, 0, 0, 0),
            6 => new Matrix3x2(0, 1, -1, 0, h, 0),
            7 => new Matrix3x2(0, -1, -1, 0, h, w),
            8 => new Matrix3x2(0, -1, 1, 0, 0, w),
            _ => Matrix3x2.Identity,
        };

        return orient * Matrix3x2.CreateTranslation((float)destination.X, (float)destination.Y);
    }
}

/// <summary>Hình chữ nhật pixel nguyên (tile, vùng upload).</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Một tile: <see cref="Texture"/> = vùng nằm trong texture (có viền chồng); <see cref="Core"/> = vùng tile này vẽ.</summary>
internal readonly record struct TileSpec(PixelRect Texture, PixelRect Core);

/// <summary>
/// Chia ảnh lớn hơn giới hạn texture (D3D11 FL11: 16384) thành tile. Các <see cref="TileSpec.Core"/> phủ kín ảnh, không
/// chồng nhau; mỗi texture chứa thêm <c>gutter</c> pixel quanh core để nội suy ở mép tile lấy đúng pixel hàng xóm
/// (không có đường nối). Thuần, không cấp phát ngoài mảng kết quả.
/// </summary>
internal static class TileLayout
{
    /// <summary>Viền chồng mặc định: đủ cho cubic (2 px) và tiền-thu-nhỏ của HighQualityCubic tới khoảng 1/30.</summary>
    public const int DefaultGutter = 64;

    /// <summary>D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION (feature level 11).</summary>
    public const int D3D11MaxTextureDimension = 16384;

    public static TileSpec[] Compute(int width, int height, int maxTextureSize, int gutter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTextureSize, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(gutter);
        gutter = Math.Min(gutter, maxTextureSize / 4);

        (int Start, int Length, int TexStart, int TexLength)[] columns = Split(width, maxTextureSize, gutter);
        (int Start, int Length, int TexStart, int TexLength)[] rows = Split(height, maxTextureSize, gutter);
        var tiles = new TileSpec[columns.Length * rows.Length];
        int i = 0;
        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                tiles[i++] = new TileSpec(
                    new PixelRect(column.TexStart, row.TexStart, column.TexLength, row.TexLength),
                    new PixelRect(column.Start, row.Start, column.Length, row.Length));
            }
        }

        return tiles;
    }

    private static (int Start, int Length, int TexStart, int TexLength)[] Split(int length, int max, int gutter)
    {
        if (length <= max)
        {
            return [(0, length, 0, length)];
        }

        int core = max - (2 * gutter);
        int count = (length + core - 1) / core;
        var result = new (int, int, int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int start = i * core;
            int end = Math.Min(start + core, length);
            int texStart = Math.Max(0, start - gutter);
            int texEnd = Math.Min(length, end + gutter);
            result[i] = (start, end - start, texStart, texEnd - texStart);
        }

        return result;
    }
}

/// <summary>Hình học thuần của <see cref="D2DDrawContext.DrawImage"/> (tách để unit test không cần GPU).</summary>
internal static class DrawGeometry
{
    private const double Epsilon = 1e-6;

    public static RectD Intersect(RectD a, RectD b)
    {
        double x = Math.Max(a.X, b.X);
        double y = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y ? new RectD(x, y, right - x, bottom - y) : default;
    }

    public static bool IsEmpty(RectD rect) => !(rect.Width > 0 && rect.Height > 0);

    public static RectD ToRectD(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Ánh xạ tuyến tính <paramref name="source"/> -&gt; <paramref name="destination"/> áp cho vùng con <paramref name="part"/>.</summary>
    public static RectD MapToDestination(RectD source, RectD destination, RectD part)
    {
        double sx = destination.Width / source.Width;
        double sy = destination.Height / source.Height;
        return new RectD(
            destination.X + ((part.X - source.X) * sx),
            destination.Y + ((part.Y - source.Y) * sy),
            part.Width * sx,
            part.Height * sy);
    }

    /// <summary>
    /// True khi vẽ <paramref name="source"/> (pixel) vào <paramref name="destination"/> dưới <paramref name="deviceTransform"/>
    /// (local -&gt; pixel thiết bị, đã gồm DPI) là 1 pixel nguồn = 1 pixel thiết bị (chỉ hoán vị/lật trục, tỉ lệ 1).
    /// Khi đó <paramref name="snapped"/> = <paramref name="destination"/> dời tối đa 0,5 px thiết bị để góc rơi đúng lưới
    /// pixel: renderer vẽ bằng NearestNeighbor và kết quả giống từng byte (G-PIX "giống hệt ở 100 %").
    /// </summary>
    public static bool TrySnapPixelExact(Matrix3x2 deviceTransform, RectD destination, RectD source, out RectD snapped)
    {
        snapped = destination;
        if (IsEmpty(source) || IsEmpty(destination))
        {
            return false;
        }

        double sx = destination.Width / source.Width;
        double sy = destination.Height / source.Height;

        // Ma trận tuyến tính nguồn -> thiết bị = diag(sx, sy) * linear(deviceTransform).
        double a = sx * deviceTransform.M11;
        double b = sx * deviceTransform.M12;
        double c = sy * deviceTransform.M21;
        double d = sy * deviceTransform.M22;
        bool axisAligned = IsZero(b) && IsZero(c) && IsUnit(a) && IsUnit(d);
        bool swapped = IsZero(a) && IsZero(d) && IsUnit(b) && IsUnit(c);
        if (!axisAligned && !swapped)
        {
            return false;
        }

        // Toạ độ thiết bị của góc tính bằng double (Vector2.Transform là float: sai 1e-7 làm lệch phép làm tròn).
        double cornerX = (destination.X * deviceTransform.M11) + (destination.Y * deviceTransform.M21) + deviceTransform.M31;
        double cornerY = (destination.X * deviceTransform.M12) + (destination.Y * deviceTransform.M22) + deviceTransform.M32;
        double dx = Math.Round(cornerX) - cornerX;
        double dy = Math.Round(cornerY) - cornerY;
        if (IsZero(dx) && IsZero(dy))
        {
            return true;
        }

        // Đưa độ lệch thiết bị về toạ độ local qua nghịch đảo phần tuyến tính của deviceTransform.
        double m11 = deviceTransform.M11;
        double m12 = deviceTransform.M12;
        double m21 = deviceTransform.M21;
        double m22 = deviceTransform.M22;
        double det = (m11 * m22) - (m12 * m21);
        if (Math.Abs(det) < Epsilon)
        {
            return false;
        }

        // Hàng-vector: [dx dy] = [lx ly] * L  =>  [lx ly] = [dx dy] * L^-1.
        double lx = ((dx * m22) - (dy * m21)) / det;
        double ly = ((dy * m11) - (dx * m12)) / det;
        snapped = destination with { X = destination.X + lx, Y = destination.Y + ly };
        return true;
    }

    private static bool IsZero(double value) => Math.Abs(value) < Epsilon;

    private static bool IsUnit(double value) => Math.Abs(Math.Abs(value) - 1) < Epsilon;
}
