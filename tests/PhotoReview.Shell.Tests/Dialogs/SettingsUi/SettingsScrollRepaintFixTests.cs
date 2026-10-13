using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// Settings Win32: cuộn trang không được để lại hình cũ / vẽ chồng. Cách đo: sau mỗi bước cuộn (và xử lý hết thông điệp vẽ), chụp
/// bề mặt THẬT của hộp thoại (BitBlt từ DC cửa sổ, không phải PrintWindow vì PrintWindow tự vẽ lại toàn bộ; DC màn hình của desktop
/// ẩn trả về đen nên không dùng được) rồi so với ảnh sau khi ép vẽ lại toàn bộ (<c>RedrawWindow</c> ALLCHILDREN|ERASE|UPDATENOW).
/// Hai ảnh phải trùng từng điểm ảnh. Nguyên nhân lỗi gốc: điều khiển của trang tạo bằng <c>CreateWindowEx</c> thiếu WS_CLIPSIBLINGS.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class SettingsScrollRepaintFixTests(ITestOutputHelper output)
{
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    // SB_LINEDOWN=1, SB_LINEUP=0, SB_PAGEDOWN=3, SB_PAGEUP=2, SB_BOTTOM=7, SB_TOP=6.
    private static readonly int[] StepCodes = [1, 1, 1, 3, 1, 0, 3, 3, 0, 1, 1, 3, 7, 0, 0, 2];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var page in new[] { 0, 1, 2, 3, 4, 5, 6 })
        {
            foreach (var dpi in new[] { 96u, 144u })
            {
                yield return [page, dpi];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ScrollingStepByStep_LeavesNoStalePixels(int page, uint dpi)
    {
        var dump = Environment.GetEnvironmentVariable("PHOTOREVIEW_SCROLL_DUMP");
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
            var failures = new List<string>();
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                var dlg = (SettingsDialog)d;
                GoTo(dlg, page);
                if (dpi != 96)
                {
                    ChangeDpi(dlg, dpi);
                }

                Drain(dlg);
                var surface = dlg.Hwnd;
                var bar = H(dlg, SettingsDialog.ScrollId);
                var codes = StepCodes;
                for (var step = 0; step < codes.Length; step++)
                {
                    User32.SendMessage(dlg.Hwnd, ControlConstants.WmVScroll, codes[step], bar);
                    Drain(dlg);
                    var actual = Capture(surface, out var width, out var height);
                    RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
                    Drain(dlg);
                    var expected = Capture(surface, out int _, out int _);
                    var bad = 0;
                    var firstBad = -1;
                    for (var i = 0; i < actual.Length; i += 4)
                    {
                        if (actual[i] != expected[i] || actual[i + 1] != expected[i + 1] || actual[i + 2] != expected[i + 2])
                        {
                            bad++;
                            if (firstBad < 0)
                            {
                                firstBad = i / 4;
                            }
                        }
                    }

                    if (dump is { Length: > 0 })
                    {
                        Directory.CreateDirectory(dump);
                        var stem = Path.Combine(dump, $"p{page}-{dpi}-s{step}");
                        File.WriteAllBytes(stem + "-actual.png", EncodePng(actual, width, height));
                        if (bad > 0)
                        {
                            File.WriteAllBytes(stem + "-expected.png", EncodePng(expected, width, height));
                        }
                    }

                    if (bad > 0)
                    {
                        failures.Add($"step {step} (code {codes[step]}): {bad} stale pixels, first at ({firstBad % width},{firstBad / width})");
                    }
                }

                d.Close();
            });
            foreach (var failure in failures)
            {
                output.WriteLine($"page {page} dpi {dpi}: {failure}");
            }

            Assert.True(failures.Count == 0, $"page {page} dpi {dpi}: {string.Join("; ", failures)}");
            return 0;
        });
    }

    /// <summary>Số đo (không assert chặt): mỗi bước cuộn trang Display (~80 điều khiển) gồm đặt lại vị trí + vẽ lại, tính bằng ms.</summary>
    [Fact]
    [Trait("Category", "Manual")]
    public void Report_ScrollStepCost_OnTheDisplayPage()
    {
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new SettingsDialog(new AppSettings(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            ShowOnOwner(dialog, (d, _) =>
            {
                var dlg = (SettingsDialog)d;
                GoTo(dlg, 1);
                Drain(dlg);
                var bar = H(dlg, SettingsDialog.ScrollId);
                var samples = new List<double>();
                for (var round = 0; round < 30; round++)
                {
                    var code = round % 2 == 0 ? ScrollConstants.SbPageDown : ScrollConstants.SbPageUp;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    User32.SendMessage(dlg.Hwnd, ControlConstants.WmVScroll, code, bar);
                    Drain(dlg);
                    samples.Add(clock.Elapsed.TotalMilliseconds);
                }

                samples.RemoveRange(0, 4);
                samples.Sort();
                output.WriteLine($"scroll step (move + repaint): median {samples[samples.Count / 2]:F2} ms, p90 {samples[(int)(samples.Count * 0.9)]:F2} ms, max {samples[^1]:F2} ms");
                d.Close();
            });
            return 0;
        });
    }

    private static byte[] Capture(nint hwnd, out int width, out int height)
    {
        // The window DC of the dialog (not PrintWindow, which repaints everything): what is really on the window surface.
        Rect rect = default;
        User32.GetWindowRect(hwnd, &rect);
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        var source = GetWindowDC(hwnd);
        var memory = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x00CC0020 /* SRCCOPY */);
            var info = new byte[40];
            BitConverter.GetBytes(40).CopyTo(info, 0);
            BitConverter.GetBytes(width).CopyTo(info, 4);
            BitConverter.GetBytes(-height).CopyTo(info, 8);
            BitConverter.GetBytes((short)1).CopyTo(info, 12);
            BitConverter.GetBytes((short)32).CopyTo(info, 14);
            var pixels = new byte[width * height * 4];
            fixed (byte* p = pixels)
            fixed (byte* i = info)
            {
                SelectObject(memory, old);
                _ = GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
            }

            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(hwnd, source);
        }
    }

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                raw.WriteByte(bgra[i + 2]);
                raw.WriteByte(bgra[i + 1]);
                raw.WriteByte(bgra[i]);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBe(header, 0, width);
        WriteBe(header, 4, height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteBe(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBe(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            body[i] = (byte)type[i];
        }

        data.CopyTo(body, 4);
        stream.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        var tail = new byte[4];
        WriteBe(tail, 0, (int)~crc);
        stream.Write(tail);
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, Rect* update, nint region, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);
}
