using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Benchmark.Cli;

namespace PhotoReview.Integration.Tests;

public sealed class RawSurveyTests
{
    [Fact]
    public async Task SurveyFile_SyntheticJpeg_DiscoversDimensionsAndWicDecode()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"survey-test-{Guid.NewGuid():N}.jpg");
        try
        {
            CreateTestJpeg(tempFile, 320, 240);

            var result = await RawSurvey.SurveyFileAsync(tempFile);

            Assert.Equal("JPEG", result.Format);
            Assert.True(result.FileSizeBytes > 0);
            Assert.NotEmpty(result.EmbeddedJpegs);

            var mainJpeg = result.EmbeddedJpegs[0];
            Assert.Equal(0, mainJpeg.Offset);
            Assert.Equal(320, mainJpeg.Width);
            Assert.Equal(240, mainJpeg.Height);

            Assert.True(result.WicReadInfoSuccess);
            Assert.Equal(320, result.WicInfoWidth);
            Assert.Equal(240, result.WicInfoHeight);

            Assert.True(result.WicDecodeSuccess);
            Assert.Equal(320, result.WicDecodedWidth);
            Assert.Equal(240, result.WicDecodedHeight);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task RunAsync_ValidDirectory_OutputsMarkdownReport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"survey-run-{Guid.NewGuid():N}");
        var mdPath = Path.Combine(tempDir, "SURVEY-TEST.md");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Only RAW files are surveyed: the folder holds a synthetic DNG whose embedded preview is 400x300.
            var testJpeg = Path.Combine(tempDir, "sample.dng");
            File.WriteAllBytes(testJpeg, PhotoReview.Imaging.Tests.Raw.SyntheticRawBuilder.BuildTiff(
                littleEndian: true, PhotoReview.Imaging.Tests.Raw.SyntheticRawBuilder.CreateMinimalJpeg(400, 300)));

            var exitCode = await RawSurvey.RunAsync(["--raw-survey", tempDir, "--markdown", mdPath]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(mdPath));

            var mdContent = await File.ReadAllTextAsync(mdPath);
            Assert.Contains("Camera RAW Survey", mdContent);
            Assert.Contains("sample.dng", mdContent);
            Assert.Contains("400×300", mdContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void CreateTestJpeg(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 128;     // B
            pixels[i + 1] = 128; // G
            pixels[i + 2] = 200; // R
            pixels[i + 3] = 255; // A
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
