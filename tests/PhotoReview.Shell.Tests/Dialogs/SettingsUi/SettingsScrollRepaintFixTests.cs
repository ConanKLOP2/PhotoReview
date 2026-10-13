using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.Native;
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
/// Trùng nhau CHƯA đủ (một trang TRỐNG sau cuộn bằng một trang TRỐNG sau vẽ lại: đó là cách WS_CLIPSIBLINGS mà khung nhóm nằm TRÊN điều khiển
/// con làm trống mọi trang mà test vẫn xanh), nên mỗi bước còn khẳng định: mọi điều khiển hiện đủ trong vùng nhìn phải có điểm ảnh khác màu nền.
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

                ParkCursor(dlg);
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
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                    for (var i = 0; i < actual.Length; i += 4)
                    {
                        if (actual[i] != expected[i] || actual[i + 1] != expected[i + 1] || actual[i + 2] != expected[i + 2])
                        {
                            bad++;
                            if (firstBad < 0)
                            {
                                firstBad = i / 4;
                            }

                            var px = (i / 4) % width;
                            var py = (i / 4) / width;
                            minX = Math.Min(minX, px);
                            maxX = Math.Max(maxX, px);
                            minY = Math.Min(minY, py);
                            maxY = Math.Max(maxY, py);
                        }
                    }

                    // The page must not just be "equal to itself": every visible control has to show something other than the page background
                    // (a blank page after a redraw is also equal to its own scrolled copy - that is how WS_CLIPSIBLINGS under the group boxes slipped by).
                    foreach (var blank in BlankControls(dlg, expected, width, height))
                    {
                        failures.Add($"step {step} (code {codes[step]}): control {blank} is blank (only page background)");
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
                        failures.Add($"step {step} (code {codes[step]}): {bad} stale pixels, first at ({firstBad % width},{firstBad / width}), "
                            + $"box ({minX},{minY})-({maxX},{maxY}) over {ControlsAt(dlg, minX, minY, maxX, maxY)}");
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

    /// <summary>
    /// Ids (with class) of the visible controls of the active page whose on-screen rectangle (clipped to the page viewport) contains one
    /// single colour equal to the page background: the signature of a control painted over by a sibling above it. Group boxes are skipped
    /// (their rectangle legitimately is mostly background) and so are controls without text that have no border of their own.
    /// </summary>
    private static List<string> BlankControls(SettingsDialog dialog, byte[] pixels, int width, int height)
    {
        var result = new List<string>();
        var origin = WindowRect(dialog.Hwnd);
        var viewportRect = WindowRect(H(dialog, SettingsDialog.ViewportId(dialog.ActivePageIndex)));
        var background = DominantColor(pixels, width, height, viewportRect.Left - origin.Left, viewportRect.Top - origin.Top,
            viewportRect.Right - origin.Left, viewportRect.Bottom - origin.Top);
        foreach (var item in dialog.PageItems(dialog.ActivePageIndex))
        {
            var control = H(dialog, item.Id);
            if (control == 0 || !User32.IsWindowVisible(control) || IsGroupBox(control))
            {
                continue;
            }

            var rect = WindowRect(control);
            if (rect.Top < viewportRect.Top || rect.Bottom > viewportRect.Bottom)
            {
                continue; // partly scrolled out: a thin slice of a text line may legitimately be only background
            }

            var left = Math.Max(rect.Left, viewportRect.Left) - origin.Left;
            var top = Math.Max(rect.Top, viewportRect.Top) - origin.Top;
            var right = Math.Min(rect.Right, viewportRect.Right) - origin.Left;
            var bottom = Math.Min(rect.Bottom, viewportRect.Bottom) - origin.Top;
            if (right - left < 6 || bottom - top < 6)
            {
                continue;
            }

            var emptyText = item.Class is DialogControlClass.Static or DialogControlClass.Button && TextOf(control).Length == 0;
            if (emptyText)
            {
                continue;
            }

            if (UniformColor(pixels, width, left, top, right, bottom) == background)
            {
                result.Add($"{item.Id} ({item.Class})");
            }
        }

        return result;
    }

    /// <summary>Colour (0xRRGGBB) of the rectangle when every pixel has it, otherwise -1.</summary>
    private static int UniformColor(byte[] bgra, int width, int left, int top, int right, int bottom)
    {
        var first = -2;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = ((y * width) + x) * 4;
                var color = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                if (first == -2)
                {
                    first = color;
                }
                else if (color != first)
                {
                    return -1;
                }
            }
        }

        return first;
    }

    private static int DominantColor(byte[] bgra, int width, int height, int left, int top, int right, int bottom)
    {
        var counts = new Dictionary<int, int>();
        for (var y = Math.Max(0, top); y < Math.Min(height, bottom); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(width, right); x++)
            {
                var i = ((y * width) + x) * 4;
                var color = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        }

        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// The pointer must not sit over a page control: a control that scrolls under a still pointer makes the system synthesize a WM_MOUSEMOVE
    /// (hover highlight of the chrome painter) at an arbitrary later time, so "after scroll" and "after forced redraw" differ by a hover
    /// border that has nothing to do with stale pixels (seen on CI where a real pointer exists). It is parked over the dialog heading, a
    /// static that neither scrolls nor tracks hover. <c>PHOTOREVIEW_SCROLL_CURSOR_OVER_PAGE=1</c> parks it over the page instead (reproduces
    /// that CI condition on an interactive desktop).
    /// </summary>
    private static void ParkCursor(SettingsDialog dialog)
    {
        var overPage = Environment.GetEnvironmentVariable("PHOTOREVIEW_SCROLL_CURSOR_OVER_PAGE") == "1";
        var target = WindowRect(H(dialog, overPage ? SettingsDialog.ViewportId(dialog.ActivePageIndex) : SettingsDialog.HeaderId));
        _ = SetCursorPos(target.Left + ((target.Right - target.Left) / 2), target.Top + ((target.Bottom - target.Top) / 2));
    }

    private static string ControlsAt(SettingsDialog dialog, int left, int top, int right, int bottom)
    {
        var origin = WindowRect(dialog.Hwnd);
        var found = new List<string>();
        foreach (var item in dialog.PageItems(dialog.ActivePageIndex))
        {
            var control = H(dialog, item.Id);
            if (control == 0 || !User32.IsWindowVisible(control) || IsGroupBox(control))
            {
                continue;
            }

            var r = WindowRect(control);
            if (r.Left - origin.Left <= right && r.Right - origin.Left >= left && r.Top - origin.Top <= bottom && r.Bottom - origin.Top >= top)
            {
                found.Add($"{item.Id}({item.Class})");
            }
        }

        return found.Count == 0 ? "no page control" : string.Join(",", found);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

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
