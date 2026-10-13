using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using PhotoReview.App.Input;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.TestSupport;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-23: số đo vẽ HUD (Category=Manual - báo số, không assert thời gian) và ảnh chụp HUD để mắt thường kiểm tra. Chạy:
/// <c>dotnet test tests/PhotoReview.Shell.Tests -c Release --filter "FullyQualifiedName~ShellOverlayPerfTests" --logger "console;verbosity=detailed"</c>.
/// Đặt <c>PHOTOREVIEW_OVERLAY_PNG</c> = đường dẫn .png để ghi khung HUD (tiếng Việt, 1280x720, DPI 1,0) ra tệp.
/// "submit" = Arrange + Render + EndDrawAndPresent (CPU); "done" = thêm chờ GPU xong.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class ShellOverlayPerfTests(ITestOutputHelper output)
{
    private static OverlayModel FullModel() => new()
    {
        HasImages = true,
        StatusPanelVisible = true,
        FileInfoVisible = true,
        StatusText = "IMG_0042.JPG  (42/1280)  6000 x 4000  14,2 MB",
        FontSize = 12,
        ExifFontSize = 11,
        ExifVisible = true,
        ExifText = "Canon EOS R5  |  85 mm  f/1.8  1/200 s  ISO 100  |  Taken: 2026-10-01 14:23",
        FolderInfoVisible = true,
        FolderInfoText = "◀ PageUp: Đà Lạt     📁 Trip to Da Lat     PageDown: Hội An ▶",
        ZoomIndicatorVisible = true,
        ZoomIndicatorText = "100%",
        CapturePairVisible = true,
        CapturePairText = "JPEG+RAW (đang xem JPEG)",
        CapturePairToolTip = "Nhấn F8 để chuyển giữa ảnh JPEG và RAW.",
        HasSkippedEntries = true,
        SkippedWarningText = "Bỏ qua 3 tệp không đọc được",
    };

    private static ShellOverlay Build(DWriteTextRenderer text, FakeTooltips tooltips) => new(new ShellOverlayOptions
    {
        Text = text,
        Clock = new FakeFrameClock(),
        Settings = static () => new AppSettings(),
        Commands = new FakeOverlayCommands(),
        RequestRender = static () => { },
        IsWindowActive = static () => true,
        Tooltips = tooltips,
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HudFrame_FullOverlay_1920x1080_Dpi1_5(bool hardware)
    {
        using IDisposable vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(1920, 1080, 1.5, useHardware: hardware);
        string device = surface.IsWarp ? "WARP" : "GPU";
        output.WriteLine($"== device {device} (requested hardware={hardware})");
        if (hardware && surface.IsWarp)
        {
            output.WriteLine("no hardware D3D11 device: skipped");
            return;
        }

        using var text = new DWriteTextRenderer("vi-VN", 512);
        using ShellOverlay overlay = Build(text, new FakeTooltips());
        overlay.Update(FullModel());
        var client = new SizeD(1920 / 1.5, 1080 / 1.5);

        var firstFrame = Stopwatch.GetTimestamp();
        RenderOnce(surface, overlay, client);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{device} first HUD frame (cold layouts): {Stopwatch.GetElapsedTime(firstFrame).TotalMilliseconds:F3} ms"));

        var submit = new List<double>();
        var done = new List<double>();
        var arrangeOnly = new List<double>();
        for (int frame = 0; frame < 300; frame++)
        {
            long a = Stopwatch.GetTimestamp();
            overlay.Arrange(client, 1.5);
            double arrange = Stopwatch.GetElapsedTime(a).TotalMilliseconds;
            long start = Stopwatch.GetTimestamp();
            RenderOnce(surface, overlay, client);
            double submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            surface.WaitForGpu();
            double completed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame >= 30)
            {
                arrangeOnly.Add(arrange);
                submit.Add(submitted);
                done.Add(completed);
            }
        }

        Summary(device + " HUD Arrange only (steady)", arrangeOnly);
        Summary(device + " HUD Arrange+Render+Present submit (CPU)", submit);
        Summary(device + " HUD Arrange+Render+Present done (GPU)", done);

        // Chữ đổi mỗi khung (zoom % khi cuộn bánh xe): layout miss mỗi khung.
        var changing = new List<double>();
        for (int frame = 0; frame < 200; frame++)
        {
            overlay.Update(FullModel() with { ZoomIndicatorText = (50 + frame).ToString(CultureInfo.InvariantCulture) + "%" });
            long start = Stopwatch.GetTimestamp();
            RenderOnce(surface, overlay, client);
            surface.WaitForGpu();
            if (frame >= 20)
            {
                changing.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        Summary(device + " HUD frame, zoom text changes each frame, done", changing);

        // Giữa lúc fade (opacity < 1 -> PushOpacity vẽ qua lớp): đo riêng.
        var fading = new List<double>();
        for (int frame = 0; frame < 200; frame++)
        {
            overlay.Toolbar.Opacity = 0.2f + (0.6f * (frame % 10) / 10f);
            overlay.InfoLayer.Opacity = 0.2f + (0.6f * (frame % 10) / 10f);
            long start = Stopwatch.GetTimestamp();
            RenderOnce(surface, overlay, client);
            surface.WaitForGpu();
            if (frame >= 20)
            {
                fading.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        Summary(device + " HUD frame while fading, done", fading);
    }

    [Fact]
    public void WriteHudSnapshot_WhenAPathIsGiven()
    {
        string? path = Environment.GetEnvironmentVariable("PHOTOREVIEW_OVERLAY_PNG");
        if (string.IsNullOrEmpty(path))
        {
            output.WriteLine("PHOTOREVIEW_OVERLAY_PNG not set: skipped");
            return;
        }

        using IDisposable vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(1280, 720, 1.0, useHardware: false);
        using var text = new DWriteTextRenderer("vi-VN", 512);
        using ShellOverlay overlay = Build(text, new FakeTooltips());
        overlay.Update(FullModel());
        IDrawContext dc = surface.BeginDraw();
        dc.Clear(new ColorF(0.25f, 0.30f, 0.35f, 1f));
        overlay.Render(dc, new SizeD(1280, 720), 1.0);
        Assert.True(dc.TryReadPixels(out PixelBuffer pixels));
        using (pixels)
        {
            WritePng(path, pixels);
        }

        surface.EndDrawAndPresent();
        output.WriteLine("wrote " + path);
    }

    private static void RenderOnce(D2DRenderSurface surface, ShellOverlay overlay, SizeD client)
    {
        IDrawContext dc = surface.BeginDraw();
        dc.Clear(new ColorF(0.06f, 0.06f, 0.06f, 1f));
        overlay.Render(dc, client, 1.5);
        surface.EndDrawAndPresent();
    }

    private void Summary(string name, List<double> values)
    {
        values.Sort();
        double P(double q) => values[Math.Min(values.Count - 1, (int)Math.Ceiling(q * values.Count) - 1)];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: n={values.Count} P50={P(0.5):F3} P95={P(0.95):F3} max={values[^1]:F3} ms"));
    }

    /// <summary>PNG RGBA 8 bit tối thiểu từ Pbgra32 (đọc bằng mắt thường; không phụ thuộc thư viện ảnh).</summary>
    private static void WritePng(string path, PixelBuffer pixels)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < pixels.Height; y++)
        {
            raw.WriteByte(0);
            ReadOnlySpan<byte> row = pixels.GetRow(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                raw.WriteByte(row[(x * 4) + 2]);
                raw.WriteByte(row[(x * 4) + 1]);
                raw.WriteByte(row[x * 4]);
                raw.WriteByte(row[(x * 4) + 3]);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        }

        using FileStream file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        WriteBigEndian(header, 0, pixels.Width);
        WriteBigEndian(header, 4, pixels.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> tail = stackalloc byte[4];
        WriteBigEndian(tail, 0, (int)crc);
        stream.Write(tail);
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type.Concat(data))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
