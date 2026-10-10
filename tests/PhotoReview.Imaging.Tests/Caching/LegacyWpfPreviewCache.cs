using PhotoReview.Core.Model;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// WP-04: bản sao CHỈ DÙNG TRONG TEST của đường cache WPF trước WP-04 (commit 14d6bf83): ghi = header + EXIF block +
/// <c>JpegBitmapEncoder { QualityLevel = 95 }</c> / <c>PngBitmapEncoder</c>; đọc = parse header + <c>BitmapImage</c> (OnLoad) +
/// <c>FormatConvertedBitmap</c> sang Bgr32/Pbgra32. Là "bản cũ" cho test tương thích hai chiều và benchmark trước/sau.
/// </summary>
internal static class LegacyWpfPreviewCache
{
    /// <summary>Mẫu tất định dùng cho fixture <c>tests/Fixtures/PreviewCache</c> (giống hệt chương trình sinh fixture).</summary>
    public static byte[] Pattern(int width, int height, bool alpha)
    {
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                px[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                px[i + 2] = (byte)(((((x / 8) + (y / 8)) % 2) * 200) + 28);
                px[i + 3] = alpha ? (byte)(255 - (x * 255 / Math.Max(1, width - 1))) : (byte)255;
            }
        }
        return px;
    }

    public static BitmapSource PatternBitmap(int width, int height, PixelFormat format, bool alpha = false)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, Pattern(width, height, alpha), width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static PixelBuffer PatternPixels(int width, int height, PixelLayout layout)
    {
        var bytes = Pattern(width, height, alpha: false);
        var pixels = PixelBuffer.Allocate(width, height, layout);
        for (var y = 0; y < height; y++) bytes.AsSpan(y * width * 4, width * 4).CopyTo(pixels.GetRow(y));
        return pixels;
    }

    /// <summary>Byte entry v7 đúng như <c>PreviewCacheFile.WriteAtomicallyAsync(BitmapSource, ...)</c> trước WP-04.</summary>
    public static byte[] WriteV7(BitmapSource bitmap, DecoderBackend backend, int orientation, int originalWidth, int originalHeight,
        ExifSummary? exif, int quality = PreviewCacheFile.DefaultJpegQuality)
    {
        using var stream = new MemoryStream();
        var header = new byte[24];
        "PRVC"u8.CopyTo(header);
        header[4] = 7;
        header[5] = (byte)backend;
        header[6] = (byte)orientation;
        header[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bitmap.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), bitmap.PixelHeight);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), originalWidth > 0 ? originalWidth : bitmap.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), originalHeight > 0 ? originalHeight : bitmap.PixelHeight);
        var exifBytes = ExifSummaryCodec.Encode(exif);
        var exifLength = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(exifLength, (ushort)exifBytes.Length);
        stream.Write(header);
        stream.Write(exifLength);
        stream.Write(exifBytes);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Kết quả đọc bằng đường WPF cũ.</summary>
    public sealed record Entry(BitmapSource Bitmap, DecoderBackend Backend, int Orientation, int OriginalWidth, int OriginalHeight, ExifSummary? Exif);

    /// <summary>Đọc entry (v6/v7) đúng như <c>PreviewCacheFile.Read</c> trước WP-04.</summary>
    public static Entry Read(byte[] file)
    {
        if (!file.AsSpan(0, 4).SequenceEqual("PRVC"u8)) throw new InvalidDataException("magic");
        var version = file[4];
        if (version is not (6 or 7)) throw new InvalidDataException("version");
        var hasAlpha = file[7] != 0;
        var width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        var height = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        var originalWidth = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16));
        var originalHeight = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(20));
        var offset = 24;
        ExifSummary? exif = null;
        if (version == 7)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(24));
            if (length > 0) exif = ExifSummaryCodec.Decode(file.AsSpan(26, length));
            offset = 26 + length;
        }
        var payload = file.AsSpan(offset);
        if (payload.Length < 4 || payload[^2] != 0xFF || payload[^1] != 0xD9) throw new InvalidDataException("truncated");
        var bitmap = DecodeWpf(payload.ToArray());
        if (bitmap.PixelWidth != width || bitmap.PixelHeight != height) throw new InvalidDataException("size");
        var target = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        BitmapSource native = bitmap.Format == target ? bitmap : new FormatConvertedBitmap(bitmap, target, null, 0);
        native.Freeze();
        return new Entry(native, (DecoderBackend)file[5], file[6], originalWidth, originalHeight, exif);
    }

    /// <summary><c>BitmapImage</c> OnLoad trên một payload ảnh (đường đọc cache/thumbnail WPF cũ).</summary>
    public static BitmapImage DecodeWpf(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>PNG đúng như <c>DiskCacheStore.WriteAtomicallyAsync(BitmapSource, ...)</c> trước WP-04.</summary>
    public static byte[] WritePng(BitmapSource bitmap)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static PixelBuffer ToPixels(BitmapSource bitmap, PixelLayout layout) => PixelAssert.FromBitmapSource(bitmap, layout);

    /// <summary>Thư mục fixture <c>tests/Fixtures/PreviewCache</c> (đi ngược lên từ thư mục output của test).</summary>
    public static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "PreviewCache", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate the preview-cache fixture from the test output directory.", name);
    }
}
