using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 hotfix: (a) the first Tab from the navigation list into a page must not blank the page (the static labels/notes and the text of
/// the number boxes were seen to vanish for ~400 ms), (b) the text of a tall single-line edit box (number / key box, ~44 px at 150 %) is
/// vertically centred, not stuck to the top edge. Both are measured on the REAL window surface (BitBlt from the window DC, never PrintWindow
/// which repaints everything) and, like <see cref="SettingsScrollRepaintFixTests"/>, a "blank equals blank" comparison is not enough: the
/// test also asserts that the visible controls are not just the page background.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class SettingsTabAndEditCenterTests(ITestOutputHelper output)
{
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;
    private const uint VkTabKey = 0x09;
    private const uint EsMultiline = 0x0004;

    public static IEnumerable<object[]> TabCases()
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
    [MemberData(nameof(TabCases))]
    public void FirstTabFromTheNavigationList_DoesNotBlankThePage(int page, uint dpi)
    {
        var dump = Environment.GetEnvironmentVariable("PHOTOREVIEW_TAB_DUMP");
        using var language_ = TestLocalization.Use(TestLocalization.English);
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            var failures = new List<string>();
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
                RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
                Drain(dlg);

                // The dialog starts with the keyboard cues hidden (opened by mouse); the first Tab is what reveals them.
                // WM_CHANGEUISTATE(UIS_SET, UISF_HIDEFOCUS | UISF_HIDEACCEL) puts it in that state deterministically.
                User32.SendMessage(dlg.Hwnd, 0x0127, (nint)(1 | ((0x1 | 0x2) << 16)), 0);
                Drain(dlg);
                RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
                Drain(dlg);
                Assert.Equal(0x3, (int)User32.SendMessage(dlg.Hwnd, 0x0129 /* WM_QUERYUISTATE */, 0, 0) & 0x3);

                // Focus the navigation list (as if the user came from the list), then press Tab the way the real loop does.
                _ = SetFocusNative(H(dlg, SettingsDialog.NavId));
                Drain(dlg);
                var before = Capture(dlg.Hwnd, out var width, out var height);
                Assert.True(Key(dlg, H(dlg, SettingsDialog.NavId), VkTabKey), "the dialog manager must take the Tab key");
                Assert.Equal(0, (int)User32.SendMessage(dlg.Hwnd, 0x0129, 0, 0) & 0x1); // the Tab revealed the focus cues (UISF_HIDEFOCUS cleared)
                var steps = new List<(string Name, byte[] Pixels)> { ("immediately", Capture(dlg.Hwnd, out int _, out int _)) };
                Drain(dlg);
                steps.Add(("drained", Capture(dlg.Hwnd, out int _, out int _)));
                PumpFor(dlg, 600);
                steps.Add(("after-600ms", Capture(dlg.Hwnd, out int _, out int _)));
                RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
                Drain(dlg);
                var expected = Capture(dlg.Hwnd, out int _, out int _);

                // Compared region: the page viewport; against the forced redraw the whole page is compared (no exclusion), against the state before the Tab
                // the control that took focus (its focus cue legitimately changes) is left out, inflated.
                // Buttons below the page (default-button highlight follows the focus) are not part of this check.
                var origin = WindowRect(dlg.Hwnd);
                var viewport = WindowRect(H(dlg, SettingsDialog.ViewportId(page)));
                var roi = new Rect { Left = viewport.Left - origin.Left, Top = viewport.Top - origin.Top, Right = viewport.Right - origin.Left, Bottom = viewport.Bottom - origin.Top };
                var focusRect = WindowRect(User32.GetFocus());
                var allowed = new List<Rect>
                {
                    new() { Left = focusRect.Left - origin.Left - 8, Top = focusRect.Top - origin.Top - 8, Right = focusRect.Right - origin.Left + 8, Bottom = focusRect.Bottom - origin.Top + 8 },
                };

                var isEdit = ClassOf(User32.GetFocus()).Equals("Edit", StringComparison.OrdinalIgnoreCase);
                foreach (var (name, pixels) in steps)
                {
                    // No exclusion for combobox/button focus: the focused control must match the forced redraw too (WP-35 combo focus paint). Only an edit box
                    // keeps it: its caret is an overlay that a repaint legitimately hides/shows.
                    var vsRedraw = Diff(pixels, expected, width, height, roi, isEdit ? allowed : [], out var box1);
                    var vsBefore = Diff(pixels, before, width, height, roi, allowed, out var box2);
                    if (dump is { Length: > 0 })
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllBytes(Path.Combine(dump, $"p{page}-{dpi}-{name}.png"), EncodePng(pixels, width, height));
                    }

                    if (vsRedraw > 0)
                    {
                        failures.Add($"{name}: {vsRedraw} pixels differ from the forced redraw, box {box1}, focus control {ClassOf(User32.GetFocus())} {focusRect.Left - origin.Left},{focusRect.Top - origin.Top}-{focusRect.Right - origin.Left},{focusRect.Bottom - origin.Top}");
                    }

                    if (vsBefore > 0)
                    {
                        failures.Add($"{name}: {vsBefore} pixels in the page changed outside the focused control, box {box2}");
                    }

                    foreach (var blank in BlankControls(dlg, pixels, width, height))
                    {
                        failures.Add($"{name}: control {blank} is blank (only page background)");
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

    [Theory]
    [InlineData(96u, "en")]
    [InlineData(144u, "en")]
    [InlineData(96u, "vi")]
    [InlineData(144u, "vi")]
    public void TextOfSingleLineEditBoxes_IsVerticallyCentred(uint dpi, string language)
    {
        var dump = Environment.GetEnvironmentVariable("PHOTOREVIEW_EDIT_DUMP");
        using var language_ = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var settings = new AppSettings();
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            var failures = new List<string>();
            var measured = 0;
            ShowOnOwner(dialog, (d, _) =>
            {
                var dlg = (SettingsDialog)d;
                dlg.EnsureAllPagesCreated();
                if (dpi != 96)
                {
                    ChangeDpi(dlg, dpi);
                }

                for (var page = 0; page < 7; page++)
                {
                    GoTo(dlg, page);
                    ParkCursor(dlg);
                    Drain(dlg);
                    var edits = new List<(int Id, nint Hwnd)>();
                    foreach (var item in dlg.PageItems(page))
                    {
                        var hwnd = H(dlg, item.Id);
                        if (item.Class != DialogControlClass.Edit || hwnd == 0 || !User32.IsWindowVisible(hwnd))
                        {
                            continue;
                        }

                        if (((uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle) & EsMultiline) != 0)
                        {
                            continue;
                        }

                        // Cap letters and digits only: no descender, so "ink" is symmetric around the cap height.
                        User32.SetWindowText(hwnd, "HE08");
                        edits.Add((item.Id, hwnd));
                    }

                    Drain(dlg);
                    RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
                    Drain(dlg);
                    var pixels = Capture(dlg.Hwnd, out var width, out var height);
                    var origin = WindowRect(dlg.Hwnd);
                    var viewport = WindowRect(H(dlg, SettingsDialog.ViewportId(page)));
                    foreach (var (id, hwnd) in edits)
                    {
                        // The inner area of the box = window rectangle minus the border (the client area may be shrunk on purpose).
                        Rect client = default;
                        User32.GetClientRect(hwnd, &client);
                        var window = WindowRect(hwnd);
                        var edge = ((window.Right - window.Left) - (client.Right - client.Left)) / 2;
                        var left = window.Left - origin.Left + edge;
                        var top = window.Top - origin.Top + edge;
                        var cw = window.Right - window.Left - (2 * edge);
                        var ch = window.Bottom - window.Top - (2 * edge);
                        if (window.Top < viewport.Top || window.Bottom > viewport.Bottom)
                        {
                            continue; // not fully inside the visible part of the page
                        }

                        measured++;
                        var back = PixelAt(pixels, width, left + 1, top + (ch / 2));
                        var firstInk = -1;
                        var lastInk = -1;
                        for (var y = 0; y < ch; y++)
                        {
                            for (var x = 0; x < cw; x++)
                            {
                                if (Far(PixelAt(pixels, width, left + x, top + y), back))
                                {
                                    if (firstInk < 0)
                                    {
                                        firstInk = y;
                                    }

                                    lastInk = y;
                                    break;
                                }
                            }
                        }

                        if (dump is { Length: > 0 } && page == 2)
                        {
                            Directory.CreateDirectory(dump);
                            File.WriteAllBytes(Path.Combine(dump, $"{language}-{dpi}-page{page}-after.png"), EncodePng(pixels, width, height));
                        }

                        if (firstInk < 0)
                        {
                            failures.Add($"page {page} edit {id}: no text pixels at all (client {cw}x{ch})");
                            continue;
                        }

                        var gapTop = firstInk;
                        var gapBottom = ch - 1 - lastInk;
                        output.WriteLine($"{language} {dpi} page {page} edit {id}: client {cw}x{ch}, ink rows {firstInk}..{lastInk}, gap top {gapTop}, bottom {gapBottom}");
                        if (Math.Abs(gapTop - gapBottom) > 1 || gapTop < 1 || gapBottom < 1)
                        {
                            failures.Add($"page {page} edit {id}: gap top {gapTop} / bottom {gapBottom} (client height {ch})");
                        }
                    }
                }

                d.Close();
            });
            Assert.True(measured >= 8, $"expected to measure the edit boxes of the pages, measured {measured}");
            Assert.True(failures.Count == 0, $"{language} {dpi}: {string.Join("; ", failures)}");
            return 0;
        });
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    private static string ClassOf(nint hwnd)
    {
        char* name = stackalloc char[64];
        var length = User32.GetClassName(hwnd, name, 64);
        return new string(name, 0, Math.Max(0, length));
    }

    internal static int PixelAt(byte[] bgra, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
    }

    internal static bool Far(int a, int b)
    {
        var dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
        var dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
        var db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return dr + dg + db > 90;
    }

    private static int Diff(byte[] actual, byte[] expected, int width, int height, Rect roi, List<Rect> allowed, out string box)
    {
        var bad = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var p = 0; p < width * height; p++)
        {
            var i = p * 4;
            if (actual[i] == expected[i] && actual[i + 1] == expected[i + 1] && actual[i + 2] == expected[i + 2])
            {
                continue;
            }

            var x = p % width;
            var y = p / width;
            if (x < roi.Left || x >= roi.Right || y < roi.Top || y >= roi.Bottom
                || allowed.Exists(r => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom))
            {
                continue;
            }

            bad++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        box = bad == 0 ? "-" : $"({minX},{minY})-({maxX},{maxY})";
        return bad;
    }

    /// <summary>Visible, non-empty controls of the active page (fully inside the viewport) that show only the page background.</summary>
    private static List<string> BlankControls(SettingsDialog dialog, byte[] pixels, int width, int height)
    {
        var result = new List<string>();
        var origin = WindowRect(dialog.Hwnd);
        var viewportRect = WindowRect(H(dialog, SettingsDialog.ViewportId(dialog.ActivePageIndex)));
        var background = PixelAt(pixels, width, viewportRect.Left - origin.Left + 2, viewportRect.Top - origin.Top + 2);
        foreach (var item in dialog.PageItems(dialog.ActivePageIndex))
        {
            var control = H(dialog, item.Id);
            if (control == 0 || !User32.IsWindowVisible(control) || IsGroupBox(control))
            {
                continue;
            }

            var rect = WindowRect(control);
            if (rect.Top < viewportRect.Top || rect.Bottom > viewportRect.Bottom || rect.Right - rect.Left < 6 || rect.Bottom - rect.Top < 6)
            {
                continue;
            }

            if (item.Class is DialogControlClass.Static or DialogControlClass.Button && TextOf(control).Length == 0)
            {
                continue;
            }

            var uniform = true;
            var first = PixelAt(pixels, width, rect.Left - origin.Left, rect.Top - origin.Top);
            for (var y = rect.Top; y < rect.Bottom && uniform; y++)
            {
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    if (PixelAt(pixels, width, x - origin.Left, y - origin.Top) != first)
                    {
                        uniform = false;
                        break;
                    }
                }
            }

            if (uniform && first == background)
            {
                result.Add($"{item.Id} ({item.Class})");
            }
        }

        return result;
    }

    private static void ParkCursor(SettingsDialog dialog)
    {
        var target = WindowRect(H(dialog, SettingsDialog.HeaderId));
        _ = SetCursorPos(target.Left + ((target.Right - target.Left) / 2), target.Top + ((target.Bottom - target.Top) / 2));
    }

    /// <summary>Message pump that waits event-driven (no sleep) for <paramref name="milliseconds"/> so queued timers/paints run.</summary>
    private static void PumpFor(SettingsDialog dialog, int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (true)
        {
            Drain(dialog);
            var left = end - Environment.TickCount64;
            if (left <= 0)
            {
                return;
            }

            _ = MsgWaitForMultipleObjects(0, 0, false, (uint)left, 0x04FF /* QS_ALLINPUT */);
        }
    }

    internal static byte[] Capture(nint hwnd, out int width, out int height)
    {
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "SetFocus")]
    private static extern nint SetFocusNative(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, nint handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint mask);

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
