using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// Hồi quy PIXEL của việc vẽ lại khi cuộn trang Settings Win32 (lỗi: control chồng lên nhau / còn hình cũ sau khi cuộn).
/// <para>
/// Với từng trang x DPI (100 %, 150 %) x chrome (tối = <see cref="DarkDialogChrome"/>; "cổ điển" = không áp chủ đề điều khiển, cùng đường
/// vẽ WM_CTLCOLOR) chạy một kịch bản cuộn nhiều bước (lăn chuột, kéo thumb, trang/dòng, nhảy cuối/đầu). Sau MỖI bước:
/// <list type="bullet">
/// <item>ảnh THỰC = vùng viewport + thanh cuộn chụp từ màn hình sau khi chỉ xử lý các lần vẽ đang chờ (đúng cái người dùng thấy, KHÔNG ép vẽ lại);</item>
/// <item>ảnh CHUẨN = hộp thoại tham chiếu thứ hai, đặt thẳng đúng offset đó (SB_THUMBPOSITION) rồi ép vẽ lại toàn bộ
/// (<c>RedrawWindow</c> INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW) - mọi control vẽ mới từ vị trí hiện tại.</item>
/// </list>
/// Hai ảnh phải bằng nhau từng pixel (dung sai 0: cùng nguồn vẽ, không răng cưa khác nhau). Ảnh lỗi (actual/expected/diff) ghi vào
/// <c>settings-scroll-paint/</c> cạnh test (hoặc <c>PHOTOREVIEW_SCROLL_PAINT_DIR</c>). Cần chạy trên desktop ẩn
/// (<c>tools/run-tests-hidden.ps1</c>) để không bị cửa sổ khác che khi chụp màn hình.
/// </para>
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class SettingsScrollPaintTests
{
    private const uint Rdw = 0x0001 | 0x0004 | 0x0080 | 0x0100; // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
    private const uint RdwUpdateNow = 0x0100 | 0x0080; // UPDATENOW | ALLCHILDREN: chỉ vẽ phần đang chờ, không đánh dấu thêm

    public static TheoryData<int, uint, bool> Cases()
    {
        var data = new TheoryData<int, uint, bool>();
        for (var page = 0; page < SettingsPresenter.Pages.Count; page++)
        {
            foreach (var dpi in new uint[] { 96, 144 })
            {
                foreach (var dark in new[] { true, false })
                {
                    data.Add(page, dpi, dark);
                }
            }
        }

        return data;
    }

    private enum Op
    {
        Initial,
        WheelDown3,
        WheelDown5,
        ThumbHalf,
        End,
        WheelUp1,
        Home,
        PageDown,
        PageDown2,
        ThumbDrag,
        ThumbNear,
        LineDown,
        LineUp,
        ThumbDragBack,
        HomeAgain,
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ScrollingRepaints_ExactlyLikeAFullRedraw(int pageIndex, uint dpi, bool dark)
    {
        var pageKey = SettingsPresenter.Pages[pageIndex].Key;
        var tag = $"{pageKey}-{dpi}dpi-{(dark ? "dark" : "classic")}";
        using var scope = TestLocalization.Use(TestLocalization.English);
        var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
        var ops = Enum.GetValues<Op>();
        var actual = new List<Shot>();
        var offsets = new List<int>();
        INativeDialogChrome? chrome() => dark ? null : new RecordingChrome();

        // Pass 1: người dùng thật - mỗi bước chỉ xử lý lần vẽ đang chờ.
        Run(settings, chrome(), d =>
        {
            Prepare(d, pageIndex, dpi);
            foreach (var op in ops)
            {
                Apply(d, pageIndex, op);
                Flush(d, force: false);
                offsets.Add(d.ScrollOffset(pageIndex));
                actual.Add(Capture(d, pageIndex));
            }
        });

        // Pass 2: tham chiếu - nhảy thẳng tới offset rồi ép vẽ lại hết.
        var expected = new List<Shot>();
        var reachedOffsets = new List<int>();
        Run(settings, chrome(), d =>
        {
            Prepare(d, pageIndex, dpi);
            for (var i = 0; i < ops.Length; i++)
            {
                JumpTo(d, offsets[i]);
                Flush(d, force: true);
                reachedOffsets.Add(d.ScrollOffset(pageIndex));
                expected.Add(Capture(d, pageIndex));
            }
        });

        var failures = new List<string>();
        var directory = OutputDirectory();
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory, tag + "-*.png"))
        {
            File.Delete(stale);
        }

        Assert.Equal(ops.Length, actual.Count);
        Assert.Equal(ops.Length, expected.Count);
        // Chống đậu giả: ảnh chuẩn của một trang có nội dung phải có nhiều hơn một màu.
        Assert.True(expected.Any(s => s.DistinctSample() > 4), $"{tag}: reference capture is blank (capture is broken)");
        for (var i = 0; i < ops.Length; i++)
        {
            var label = $"{i:00}-{ops[i]}";
            if (reachedOffsets[i] != offsets[i])
            {
                failures.Add($"{label}: reference reached offset {reachedOffsets[i]}, user path {offsets[i]} (layout differs)");
                continue;
            }

            var (count, box) = Compare(actual[i], expected[i]);
            if (count == 0)
            {
                continue;
            }

            failures.Add($"{label} (offset {offsets[i]}): {count} pixels differ, bounding box {box}");
            File.WriteAllBytes(Path.Combine(directory, $"{tag}-{label}-actual.png"), actual[i].ToPng());
            File.WriteAllBytes(Path.Combine(directory, $"{tag}-{label}-expected.png"), expected[i].ToPng());
            File.WriteAllBytes(Path.Combine(directory, $"{tag}-{label}-diff.png"), Diff(actual[i], expected[i]).ToPng());
        }

        Assert.True(failures.Count == 0,
            $"{tag}: stale pixels after scrolling in {failures.Count}/{ops.Length} steps (images in {directory}):\n" + string.Join('\n', failures));
    }

    /// <summary>Hộp thoại thật trên thread STA có visual styles (như exe thật); <paramref name="body"/> chạy ngay trước vòng lặp modal.</summary>
    private static void Run(AppSettings settings, INativeDialogChrome? chrome, Action<SettingsDialog> body)
        => OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()), chrome);
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                var settingsDialog = (SettingsDialog)d;
                try
                {
                    body(settingsDialog);
                }
                finally
                {
                    d.Close();
                }
            });
            return 0;
        });

    private static string OutputDirectory()
        => Environment.GetEnvironmentVariable("PHOTOREVIEW_SCROLL_PAINT_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "settings-scroll-paint");

    // ---- kịch bản ---------------------------------------------------------------------------------------------------------

    private static void Prepare(SettingsDialog d, int pageIndex, uint dpi)
    {
        GoTo(d, pageIndex);
        if (dpi != 96)
        {
            ChangeDpi(d, dpi);
        }

        Flush(d, force: false);
    }

    private static int MaxScroll(SettingsDialog d, int page) => Math.Max(0, d.ContentHeight(page) - d.ViewportHeight);

    private static void Apply(SettingsDialog d, int page, Op op)
    {
        var max = MaxScroll(d, page);
        switch (op)
        {
            case Op.Initial:
                break;
            case Op.WheelDown3:
                Wheel(d, page, -3);
                break;
            case Op.WheelDown5:
                Wheel(d, page, -5);
                break;
            case Op.ThumbHalf:
                Thumb(d, max / 2, release: true);
                break;
            case Op.End:
                Bar(d, ScrollConstants.SbBottom);
                break;
            case Op.WheelUp1:
                Wheel(d, page, 1);
                break;
            case Op.Home:
                Bar(d, ScrollConstants.SbTop);
                break;
            case Op.PageDown:
                Bar(d, ScrollConstants.SbPageDown);
                break;
            case Op.PageDown2:
                Bar(d, ScrollConstants.SbPageDown);
                break;
            case Op.ThumbDrag:
                // Kéo thumb qua nhiều vị trí trung gian rồi thả (mỗi vị trí là một WM_VSCROLL SB_THUMBTRACK).
                for (var step = 1; step <= 4; step++)
                {
                    Thumb(d, max * (step * 2 + 1) / 11, release: false);
                    Flush(d, force: false);
                }

                Thumb(d, max * 9 / 11, release: true);
                break;
            case Op.ThumbNear:
                Thumb(d, max * 9 / 11 + 7, release: true);
                break;
            case Op.LineDown:
                Bar(d, ScrollConstants.SbLineDown);
                break;
            case Op.LineUp:
                Bar(d, ScrollConstants.SbLineUp);
                break;
            case Op.ThumbDragBack:
                Thumb(d, max / 5, release: true);
                break;
            case Op.HomeAgain:
                Bar(d, ScrollConstants.SbTop);
                break;
        }
    }

    private static void Wheel(SettingsDialog d, int page, int notches)
    {
        var delta = notches * 120;
        var viewport = H(d, SettingsDialog.ViewportId(page));
        User32.SendMessage(viewport, WindowMessages.WmMouseWheel, unchecked((nint)((long)delta << 16)), 0);
    }

    private static void Bar(SettingsDialog d, int code)
        => User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, code, d.ControlHandle(SettingsDialog.ScrollId));

    private static void JumpTo(SettingsDialog d, int offset) => Thumb(d, offset, release: true);

    /// <summary>Đặt thumb tại <paramref name="position"/> như khi người dùng kéo; <paramref name="release"/> = thả chuột (SB_THUMBPOSITION).</summary>
    private static void Thumb(SettingsDialog d, int position, bool release)
    {
        var bar = d.ControlHandle(SettingsDialog.ScrollId);
        var info = new ScrollInfo { Size = (uint)sizeof(ScrollInfo), Mask = ScrollConstants.SifPos, Pos = position };
        User32.SetScrollInfo(bar, ScrollConstants.SbCtl, &info, true);
        User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, ScrollConstants.SbThumbTrack, bar);
        if (release)
        {
            User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, ScrollConstants.SbThumbPosition, bar);
        }
    }

    /// <summary>Xử lý hết thông điệp chờ + lần vẽ đang chờ. <paramref name="force"/> = vẽ lại toàn bộ (ảnh chuẩn).</summary>
    private static void Flush(SettingsDialog d, bool force)
    {
        Drain(d);
        _ = Gdi.RedrawWindow(d.Hwnd, 0, 0, force ? Rdw : RdwUpdateNow);
        Drain(d);
    }

    // ---- chụp + so sánh -----------------------------------------------------------------------------------------------------

    private sealed class Shot(int width, int height, byte[] bgra)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public byte[] Bgra { get; } = bgra;

        public int DistinctSample()
        {
            var set = new HashSet<uint>();
            for (var o = 0; o + 3 < Bgra.Length && set.Count < 16; o += 4 * 7)
            {
                set.Add(BitConverter.ToUInt32(Bgra, o) & 0x00FFFFFF);
            }

            return set.Count;
        }

        public byte[] ToPng() => EncodePng(Bgra, Width, Height);
    }

    /// <summary>
    /// Vùng client của viewport trang, đọc lại từ DC của chính cửa sổ viewport (bit đang hiện, KHÔNG qua WM_PRINT nên giữ nguyên hình cũ
    /// nếu có). WS_CLIPCHILDREN được gỡ tạm khi chụp để DC gồm cả vùng các control con; desktop ẩn không có DC màn hình nên không đọc từ đó.
    /// </summary>
    private static Shot Capture(SettingsDialog d, int page)
    {
        var viewport = H(d, SettingsDialog.ViewportId(page));
        var client = ClientRect(viewport);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        var style = User32.GetWindowLongPtr(viewport, WindowMessages.GwlStyle);
        Gdi.SetWindowLongPtr(viewport, WindowMessages.GwlStyle, style & ~(nint)ScrollConstants.WsClipChildren);
        var source = User32.GetDC(viewport);
        var memory = Gdi.CreateCompatibleDC(source);
        var bitmap = Gdi.CreateCompatibleBitmap(source, width, height);
        var old = Gdi32.SelectObject(memory, bitmap);
        try
        {
            Assert.True(Gdi.BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x00CC0020), "BitBlt from the viewport DC failed");
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
                Gdi32.SelectObject(memory, old);
                _ = Gdi.GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
            }

            return new Shot(width, height, pixels);
        }
        finally
        {
            Gdi32.DeleteObject(bitmap);
            Gdi.DeleteDC(memory);
            _ = User32.ReleaseDC(viewport, source);
            Gdi.SetWindowLongPtr(viewport, WindowMessages.GwlStyle, style);
        }
    }

    private static (int Count, string Box) Compare(Shot actual, Shot expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            return (int.MaxValue, $"size {actual.Width}x{actual.Height} vs {expected.Width}x{expected.Height}");
        }

        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var o = ((y * actual.Width) + x) * 4;
                if (actual.Bgra[o] == expected.Bgra[o] && actual.Bgra[o + 1] == expected.Bgra[o + 1] && actual.Bgra[o + 2] == expected.Bgra[o + 2])
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return (count, count == 0 ? "-" : $"({minX},{minY})-({maxX},{maxY}) of {actual.Width}x{actual.Height}");
    }

    private static Shot Diff(Shot actual, Shot expected)
    {
        var width = Math.Min(actual.Width, expected.Width);
        var height = Math.Min(actual.Height, expected.Height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = ((y * actual.Width) + x) * 4;
                var e = ((y * expected.Width) + x) * 4;
                var o = ((y * width) + x) * 4;
                var same = actual.Bgra[a] == expected.Bgra[e] && actual.Bgra[a + 1] == expected.Bgra[e + 1] && actual.Bgra[a + 2] == expected.Bgra[e + 2];
                if (same)
                {
                    pixels[o] = (byte)(expected.Bgra[e] / 3);
                    pixels[o + 1] = (byte)(expected.Bgra[e + 1] / 3);
                    pixels[o + 2] = (byte)(expected.Bgra[e + 2] / 3);
                }
                else
                {
                    pixels[o + 2] = 255; // đỏ
                }

                pixels[o + 3] = 255;
            }
        }

        return new Shot(width, height, pixels);
    }

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                raw.WriteByte(bgra[o + 2]);
                raw.WriteByte(bgra[o + 1]);
                raw.WriteByte(bgra[o]);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, width);
        BigEndian(header, 4, height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        stream.Write(body);
        var crc = new byte[4];
        BigEndian(crc, 0, unchecked((int)Crc32(body)));
        stream.Write(crc);
    }

    private static void BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static class Gdi
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

        [DllImport("gdi32.dll")]
        public static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        public static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);
    }
}
