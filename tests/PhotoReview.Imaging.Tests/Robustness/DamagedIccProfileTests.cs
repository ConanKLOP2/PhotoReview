using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Robustness;

[Trait("Category", "HotPath")]
public sealed class DamagedIccProfileTests
{
    [Theory(DisplayName = "A JPEG whose embedded ICC profile is damaged still decodes in WPF (colour profile ignored) instead of failing the photo")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DamagedProfile_DecodesInWpf(int variant)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-icc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = FixtureGenerator.GenerateJpegWithIcc(Path.Combine(dir, "icc.jpg"), 64, 48);
            var bytes = File.ReadAllBytes(path);
            var tag = JpegBytes.IccTag;
            var at = bytes.AsSpan().IndexOf(tag);
            Assert.True(at > 0);
            var body = at + tag.Length + 2; // past "ICC_PROFILE\0", chunk number and count
            switch (variant)
            {
                case 0: bytes.AsSpan(body, 128).Fill(0xFF); break;           // header + tag table garbage
                case 1: bytes.AsSpan(body + 36, 4).Clear(); break;           // 'acsp' signature wiped
                default: bytes.AsSpan(body, 4).Fill(0xFF); break;            // absurd profile size
            }
            File.WriteAllBytes(path, bytes);

            var image = new WpfBitmapImageDecoder().Decode(new DecodeRequest(path, new DecodeBox(32, 32)));
            Assert.Equal(32, image.PixelWidth);
            Assert.Equal(24, image.PixelHeight);
        }
        finally { Directory.Delete(dir, true); }
    }
}
