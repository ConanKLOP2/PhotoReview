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
/// WP-35: a combobox of the Win32 Settings dialog must have ONE look. The system combobox repaints its closed "selected item" area
/// itself (GetDC, highlight blue + focus rectangle) when it gains/loses the focus or its selection changes, and that paint bypasses
/// WM_PAINT; the dark hand painter only ran on WM_PAINT, so right after a Tab the combobox showed the blue system look until
/// something forced a repaint. Measured on the REAL window surface (BitBlt from the window DC, never PrintWindow) at three moments
/// per state: right after the action, after the queue was drained, after a forced RedrawWindow; all three must be identical, the
/// combobox must not be the page background / the system highlight colour, and its text must still be there.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class SettingsComboFocusPaintTests(ITestOutputHelper output)
{
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;
    private const uint VkTabKey = 0x09;

    private static readonly string[] States = ["keyboard-focus", "tab-key", "mouse-focus", "hover", "disabled", "idle", "selection-change", "arrow-key", "dropped"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var dpi in new[] { 96u, 144u })
        {
            foreach (var language in new[] { "en", "vi" })
            {
                yield return [dpi, language];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Combobox_LooksTheSameRightAfterTheActionAndAfterAForcedRedraw(uint dpi, string language)
    {
        var dump = Environment.GetEnvironmentVariable("PHOTOREVIEW_COMBO_DUMP");
        using var language_ = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
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
                    foreach (var item in dlg.PageItems(page))
                    {
                        var combo = H(dlg, item.Id);
                        if (item.Class != DialogControlClass.ComboBox || combo == 0 || !User32.IsWindowVisible(combo))
                        {
                            continue;
                        }

                        dlg.EnsureVisible(item.Id);
                        Drain(dlg);
                        var viewport = WindowRect(H(dlg, SettingsDialog.ViewportId(page)));
                        var rect = WindowRect(combo);
                        if (rect.Top < viewport.Top || rect.Bottom > viewport.Bottom)
                        {
                            continue;
                        }

                        foreach (var state in States)
                        {
                            measured++;
                            MeasureState(dlg, item.Id, combo, state, $"{language}-{dpi}-p{page}-c{item.Id}-{state}", dump, failures);
                        }
                    }
                }

                d.Close();
            });
            foreach (var failure in failures)
            {
                output.WriteLine(failure);
            }

            Assert.True(measured >= 12, $"expected to measure the comboboxes of the pages, measured {measured}");
            Assert.True(failures.Count == 0, $"{language} {dpi}: {string.Join("; ", failures)}");
            return 0;
        });
    }

    private static void MeasureState(SettingsDialog dlg, int id, nint combo, string state, string tag, string? dump, List<string> failures)
    {
        // Neutral start: focus on the navigation list, cues hidden, enabled.
        User32.SendMessage(dlg.Hwnd, 0x0127, (nint)(1 | ((0x1 | 0x2) << 16)), 0); // UIS_SET hide focus + accel
        _ = SetFocusNative(H(dlg, SettingsDialog.NavId));
        User32.EnableWindow(combo, true);
        Drain(dlg);
        ForceRedraw(dlg);

        switch (state)
        {
            case "keyboard-focus":
                User32.SendMessage(dlg.Hwnd, 0x0127, (nint)(2 | ((0x1 | 0x2) << 16)), 0); // UIS_CLEAR: cues shown (a Tab did this)
                _ = SetFocusNative(combo);
                break;
            case "tab-key":
                User32.SendMessage(dlg.Hwnd, 0x0127, (nint)(1 | ((0x1 | 0x2) << 16)), 0);
                var previous = GetNextDlgTabItem(dlg.Hwnd, combo, true);
                if (previous == 0 || previous == combo)
                {
                    return;
                }

                _ = SetFocusNative(previous);
                Drain(dlg);
                _ = Key(dlg, previous, VkTabKey);
                break;
            case "mouse-focus":
                _ = SetFocusNative(combo); // cues stay hidden: the user clicked
                break;
            case "hover":
                _ = User32.SendMessage(combo, 0x0200 /* WM_MOUSEMOVE */, 0, (nint)((3 << 16) | 3));
                break;
            case "selection-change":
                _ = SetFocusNative(combo);
                User32.SendMessage(combo, 0x014E /* CB_SETCURSEL */, (ComboBoxControl.SelectedIndex(combo) + 1) % Math.Max(1, ComboBoxControl.Count(combo)), 0);
                break;
            case "arrow-key":
                _ = SetFocusNative(combo);
                User32.SendMessage(combo, 0x0100 /* WM_KEYDOWN */, 0x28 /* VK_DOWN */, 1);
                break;
            case "dropped":
                _ = SetFocusNative(combo);
                User32.SendMessage(combo, 0x014F /* CB_SHOWDROPDOWN */, 1, 0);
                break;
            case "disabled":
                User32.EnableWindow(combo, false);
                break;
        }

        var steps = new List<(string Name, byte[] Pixels)> { ("immediately", Capture(dlg.Hwnd, out var width, out var height)) };
        Drain(dlg);
        steps.Add(("drained", Capture(dlg.Hwnd, out _, out _)));
        ForceRedraw(dlg);
        var expected = Capture(dlg.Hwnd, out _, out _);
        var origin = WindowRect(dlg.Hwnd);
        var rect = WindowRect(combo);
        var box = new Rect { Left = rect.Left - origin.Left, Top = rect.Top - origin.Top, Right = rect.Right - origin.Left, Bottom = rect.Bottom - origin.Top };

        if (dump is { Length: > 0 })
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, $"{tag}-redrawn.png"), EncodePng(Crop(expected, width, new Rect { Left = rect.Left - origin.Left, Top = rect.Top - origin.Top, Right = rect.Right - origin.Left, Bottom = rect.Bottom - origin.Top }), rect.Right - rect.Left, rect.Bottom - rect.Top));
        }

        // One look: the combobox rectangle is identical in all three moments.
        foreach (var (name, pixels) in steps)
        {
            var bad = 0;
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    if (PixelAt(pixels, width, x, y) != PixelAt(expected, width, x, y))
                    {
                        bad++;
                    }
                }
            }

            if (dump is { Length: > 0 })
            {
                Directory.CreateDirectory(dump);
                File.WriteAllBytes(Path.Combine(dump, $"{tag}-{name}.png"), EncodePng(Crop(pixels, width, box), box.Right - box.Left, box.Bottom - box.Top));
            }

            if (bad > 0)
            {
                failures.Add($"{tag} {name}: {bad} combobox pixels differ from the forced redraw");
            }
        }

        // Not the page background, not the system highlight blue, and the text is there.
        var cw = box.Right - box.Left;
        var ch = box.Bottom - box.Top;
        var highlight = SystemHighlight();
        var inner = PixelAt(expected, width, box.Left + 3, box.Top + 3);
        var ink = 0;
        var highlightPixels = 0;
        var distinct = new HashSet<int>();
        for (var y = 2; y < ch - 2; y++)
        {
            for (var x = 2; x < cw - 2; x++)
            {
                var p = PixelAt(expected, width, box.Left + x, box.Top + y);
                distinct.Add(p);
                if (p == highlight)
                {
                    highlightPixels++;
                }

                if (x < cw - ch && Far(p, inner))
                {
                    ink++;
                }
            }
        }

        if (distinct.Count < 3)
        {
            failures.Add($"{tag}: combobox looks blank ({distinct.Count} distinct colours)");
        }

        if (highlightPixels > 0)
        {
            failures.Add($"{tag}: {highlightPixels} pixels use the system highlight colour (not the dark theme)");
        }

        if (ink == 0 && ComboBoxControl.SelectedIndex(combo) >= 0)
        {
            failures.Add($"{tag}: the selected text is missing");
        }

        if (state == "disabled")
        {
            User32.EnableWindow(combo, true);
        }
    }

    private static void ForceRedraw(SettingsDialog dlg)
    {
        RedrawWindow(dlg.Hwnd, null, 0, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
        Drain(dlg);
    }

    private static int SystemHighlight()
    {
        var c = GetSysColor(13 /* COLOR_HIGHLIGHT */);
        return (int)(((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF));
    }

    private static byte[] Crop(byte[] bgra, int width, Rect box)
    {
        var w = box.Right - box.Left;
        var h = box.Bottom - box.Top;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(bgra, (((box.Top + y) * width) + box.Left) * 4, result, y * w * 4, w * 4);
        }

        return result;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    private static int PixelAt(byte[] bgra, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
    }

    private static bool Far(int a, int b)
    {
        var dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
        var dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
        var db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return dr + dg + db > 90;
    }

    private static void ParkCursor(SettingsDialog dialog)
    {
        var target = WindowRect(H(dialog, SettingsDialog.HeaderId));
        _ = SetCursorPos(target.Left + ((target.Right - target.Left) / 2), target.Top + ((target.Bottom - target.Top) / 2));
    }

    private static byte[] Capture(nint hwnd, out int width, out int height)
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
    private static extern nint GetNextDlgTabItem(nint dialog, nint control, [MarshalAs(UnmanagedType.Bool)] bool previous);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

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
