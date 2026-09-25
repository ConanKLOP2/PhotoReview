using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// User-visible decoder / IO error text (ADR 0006, keys <c>err.decoder.*</c> / <c>err.io.*</c>): the exception keeps its
/// type (fallback and stale-file handling key off the type) and its English <see cref="Exception.Message"/> (logs), and
/// <see cref="UserFacingError.Describe"/> renders the sentence in the current UI language. The Localizer is process-wide,
/// so classes that switch it share one non-parallel collection.
/// </summary>
[Collection("UiLanguage")]
[Trait("Category", "HotPath")]
public sealed class DecoderErrorLocalizationTests : IDisposable
{
    private readonly TempRoot _root = new("decoder-errors");
    private readonly TurboJpegDecoder _turbo = new();

    public void Dispose() => _root.Dispose();

    private static string InEnglish(Exception ex)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        return UserFacingError.Describe(ex);
    }

    private static string InVietnamese(Exception ex)
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        return UserFacingError.Describe(ex);
    }

    [Fact(DisplayName = "A file without the JPEG signature: NotSupportedException, localized text names the file, log message stays English")]
    public void NotJpeg_IsLocalizedAndKeepsItsType()
    {
        var path = FixtureGenerator.GenerateTextFile(_root.Combine("text.jpg"));

        var decode = Assert.Throws<NotSupportedException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));
        var info = Assert.Throws<NotSupportedException>(() => _turbo.ReadInfo(path));

        foreach (var ex in new[] { decode, info })
        {
            Assert.Equal($"The file is not a valid JPEG: {path}", InEnglish(ex));
            Assert.Equal($"Tệp không phải là JPEG hợp lệ: {path}", InVietnamese(ex));
            Assert.StartsWith("File is not a valid JPEG", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "A JPEG decode from a memory buffer names the source in the current language")]
    public void NotJpegInMemory_NamesTheBufferInTheCurrentLanguage()
    {
        var request = new DecodeRequest(null!, TargetWidth: 0, Bytes: new byte[] { 1, 2, 3, 4 });

        var ex = Assert.Throws<NotSupportedException>(() => _turbo.Decode(request));

        Assert.Equal("The file is not a valid JPEG: memory buffer", InEnglish(ex));
        Assert.Equal("Tệp không phải là JPEG hợp lệ: vùng đệm bộ nhớ", InVietnamese(ex));
    }

    [Fact(DisplayName = "A missing image file: FileNotFoundException stays (stale-file handling), text is localized")]
    public void MissingFile_IsLocalizedAndKeepsItsType()
    {
        var path = _root.Combine("gone.jpg");

        var decode = Assert.Throws<FileNotFoundException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));
        var info = Assert.Throws<FileNotFoundException>(() => _turbo.ReadInfo(path));

        foreach (var ex in new[] { decode, info })
        {
            Assert.Equal($"Image file not found: {path}", InEnglish(ex));
            Assert.Equal($"Không tìm thấy tệp ảnh: {path}", InVietnamese(ex));
        }
    }

    [Fact(DisplayName = "A decode request with neither path nor bytes is localized")]
    public void NoSource_IsLocalized()
    {
        var ex = Assert.Throws<ArgumentException>(() => _turbo.Decode(new DecodeRequest(" ", TargetWidth: 0)));

        Assert.Equal("A decode request needs either a file path or image data.", InEnglish(ex));
        Assert.Equal("Yêu cầu giải mã cần có đường dẫn tệp hoặc dữ liệu ảnh.", InVietnamese(ex));
    }

    [Fact(DisplayName = "ICC JPEG: TurboJpeg refuses with NotSupportedException (fallback trigger), the localized text explains why, and the fallback still decodes it")]
    public void IccJpeg_IsLocalizedAndFallbackStillTriggers()
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(_root.Combine("icc.jpg"), 64, 48);
        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(File.ReadAllBytes(path)));

        var ex = Assert.Throws<NotSupportedException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.Equal("The JPEG has an embedded ICC color profile, so another decoder is used to keep colors accurate.", InEnglish(ex));
        Assert.Equal("JPEG có hồ sơ màu ICC nhúng, nên dùng bộ giải mã khác để giữ màu chính xác.", InVietnamese(ex));

        // The fallback decision reads the exception type only: it must work while the UI language is Vietnamese.
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        var decoder = new FallbackImageDecoder(_turbo, DecoderBackend.TurboJpeg, new WpfBitmapImageDecoder(), DecoderBackend.Wpf);
        var decoded = decoder.Decode(new DecodeRequest(path, TargetWidth: 32));
        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(32, decoded.PixelWidth);
    }

    [Fact(DisplayName = "A JPEG whose header is garbage: InvalidDataException, localized sentence wraps the native detail")]
    public void CorruptHeader_WrapsNativeDetail()
    {
        var bytes = new byte[64];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0; // SOI + APP0 marker, then zeros
        var path = _root.File("header.jpg", bytes);

        var ex = Assert.Throws<InvalidDataException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));

        var english = InEnglish(ex);
        var vietnamese = InVietnamese(ex);
        Assert.StartsWith("The JPEG header could not be read: ", english, StringComparison.Ordinal);
        Assert.StartsWith("Không đọc được phần đầu của JPEG: ", vietnamese, StringComparison.Ordinal);
        var detail = english["The JPEG header could not be read: ".Length..];
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.EndsWith(detail, vietnamese, StringComparison.Ordinal); // the OS/native text is passed through unchanged
        Assert.StartsWith("TurboJPEG failed to decompress header: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A truncated JPEG: InvalidDataException from strict decoding, localized sentence wraps the native detail")]
    public void TruncatedJpeg_WrapsNativeDetail()
    {
        var full = FixtureGenerator.GenerateGradientJpeg(_root.Combine("full.jpg"), 128, 96);
        var bytes = File.ReadAllBytes(full);
        var path = _root.File("truncated.jpg", bytes[..(bytes.Length * 6 / 10)]);

        var ex = Assert.Throws<InvalidDataException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));

        var english = InEnglish(ex);
        Assert.StartsWith("The image could not be decoded: ", english, StringComparison.Ordinal);
        Assert.StartsWith("Không giải mã được ảnh: ", InVietnamese(ex), StringComparison.Ordinal);
        Assert.StartsWith("TurboJPEG decompression failed: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ReadInfo on a JPEG with a garbage header: InvalidDataException, localized sentence wraps the native detail")]
    public void ReadInfo_CorruptHeader_WrapsNativeDetail()
    {
        var bytes = new byte[64];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        var path = _root.File("info.jpg", bytes);

        var ex = Assert.Throws<InvalidDataException>(() => _turbo.ReadInfo(path));

        Assert.StartsWith("The image information could not be read: ", InEnglish(ex), StringComparison.Ordinal);
        Assert.StartsWith("Không đọc được thông tin ảnh: ", InVietnamese(ex), StringComparison.Ordinal);
        Assert.StartsWith("TurboJPEG failed to read image info: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A JPEG that declares 65500 x 65500 pixels: InvalidDataException (no allocation), localized text shows the size")]
    public void HugeDeclaredDimensions_AreRejectedWithLocalizedText()
    {
        // SOI, SOF0 (8-bit, 65500 x 65500, one component), SOS: enough for the header parse, no entropy data.
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0xFF, 0xDC, 0xFF, 0xDC, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            0xFF, 0xD9,
        ];
        var path = _root.File("huge.jpg", jpeg);

        var ex = Assert.Throws<InvalidDataException>(() => _turbo.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.StartsWith("TurboJPEG output dimensions are too large: 65500x65500", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("The decoded image would be too large: 65500x65500 (", InEnglish(ex), StringComparison.Ordinal);
        Assert.Contains("Ảnh sau khi giải mã sẽ quá lớn: 65500x65500 (", InVietnamese(ex), StringComparison.Ordinal);
        Assert.EndsWith(" byte).", InVietnamese(ex), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "WicDirect: a COM failure in the colour transform is NotSupportedException (fallback trigger) with a localized sentence that wraps the OS text")]
    public void WicIccTransformFailure_IsLocalizedAndFallbackable()
    {
        #pragma warning disable CA2201 // simulates the OS failure WIC raises; there is no other way to build the exception the guard catches
        var comFailure = new System.Runtime.InteropServices.COMException("Windows says no");
        #pragma warning restore CA2201

        var ex = Assert.Throws<NotSupportedException>(() =>
            PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder.CopyPixelsGuarded(() => throw comFailure, colorTransformActive: true));

        Assert.Equal("The embedded ICC color profile could not be converted to sRGB: Windows says no", InEnglish(ex));
        Assert.Equal("Không thể chuyển hồ sơ màu ICC nhúng sang sRGB: Windows says no", InVietnamese(ex));
        Assert.Same(comFailure, ex.InnerException);
        Assert.StartsWith("WicDirect could not transform", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ImageCacheKey.Create on a vanished file: FileNotFoundException, localized text names the file")]
    public void ImageCacheKey_VanishedFile_IsLocalized()
    {
        var info = new FileInfo(_root.Combine("vanished.jpg"));

        var ex = Assert.Throws<FileNotFoundException>(() => ImageCacheKey.Create(info, isOriginal: false, new DecodeBox(512, 0)));

        Assert.Equal($"The image file no longer exists: {info.FullName}", InEnglish(ex));
        Assert.Equal($"Tệp ảnh không còn tồn tại: {info.FullName}", InVietnamese(ex));
    }

    [Fact(DisplayName = "SourceBytesCache: a file modified while it is read throws a localized IOException")]
    public async Task SourceBytesCache_FileChangedWhileReading_IsLocalized()
    {
        var path = _root.File("big.bin", new byte[32 * 1024 * 1024]);
        var cache = new SourceBytesCache(256L * 1024 * 1024);

        IOException ex;
        using (var toucher = new FileToucher(path))
        {
            ex = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => cache.GetOrRead(path)));
        }

        Assert.Equal($"The file changed while it was being read: {path}", InEnglish(ex));
        Assert.Equal($"Tệp đã thay đổi trong lúc đang đọc: {path}", InVietnamese(ex));
        Assert.Equal($"File changed while reading: {path}", ex.Message);
    }

    [Fact(DisplayName = "Every err.* key is translated: Vietnamese differs from English")]
    public void EveryErrKey_HasARealVietnameseTranslation()
    {
        var keys = TestLocalization.English.Keys.Where(k => k.StartsWith("err.", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count >= 19, "expected the decoder/io error keys");
        foreach (var key in keys)
        {
            var en = TestLocalization.English.Get(key);
            var vi = TestLocalization.Vietnamese.Get(key);
            Assert.NotEqual(en, vi);
            Assert.NotEqual(key, vi);
        }
    }
}
