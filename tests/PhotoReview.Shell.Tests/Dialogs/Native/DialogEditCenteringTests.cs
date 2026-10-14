using System.Runtime.InteropServices;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Dialogs.SettingsUi;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// The single-line edit boxes of the secondary dialogs (Benchmark, Action profiles, Zoom prompt) are taller than one text line; a plain
/// EDIT draws its text at the top of the client area, so the text stuck to the top edge. <c>NativeDialog</c> attaches
/// <see cref="EditTextCentering"/> to every single-line (non ES_MULTILINE) edit. Measured on the REAL surface (BitBlt from the window DC)
/// the ink gap above/below "HE08" must be equal (+-1) and not touch the border; the caret must lie inside the text rows.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed unsafe class DialogEditCenteringTests(ITestOutputHelper output)
{
    private const uint EsMultiline = 0x0004;
    private const uint RdwAll = 0x0001 | 0x0004 | 0x0080 | 0x0100;
    private const uint EmPosFromChar = 0x00D6;
    private const uint EmSetSel = 0x00B1;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var dialog in new[] { "benchmark", "profiles", "zoom" })
        {
            foreach (var dpi in new[] { 96u, 144u })
            {
                foreach (var language in new[] { "vi", "en" })
                {
                    yield return [dialog, dpi, language];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SingleLineEditText_IsVerticallyCentred_AndCaretIsInsideTheTextRows(string which, uint dpi, string language)
    {
        using var lang = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var failures = new List<string>();
        var measured = 0;
        var dump = Environment.GetEnvironmentVariable("PHOTOREVIEW_EDITCENTER_DUMP");

        void Measure(NativeDialog d)
        {
            if (dpi != 96)
            {
                ChangeDpi(d, dpi);
            }

            Drain(d);
            var edits = new List<(int Id, nint Hwnd)>();
            for (var id = 1; id < 2000; id++)
            {
                var hwnd = User32.GetDlgItem(d.Hwnd, id);
                if (hwnd == 0 || !User32.IsWindowVisible(hwnd) || !IsEditClass(hwnd)
                    || ((uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle) & EsMultiline) != 0)
                {
                    continue;
                }

                User32.SetWindowText(hwnd, "HE08");
                edits.Add((id, hwnd));
            }

            Assert.NotEmpty(edits);
            // No focused box: neither the selection highlight (it fills the whole line box) nor a blinking caret may count as "ink".
            foreach (var (_, box) in edits)
            {
                User32.SendMessage(box, EmSetSel, 0, 0);
            }

            for (var id = 1; id < 2000; id++)
            {
                var button = User32.GetDlgItem(d.Hwnd, id);
                if (button != 0 && User32.IsWindowVisible(button) && User32.IsWindowEnabled(button) && ClassOf(button) == "Button")
                {
                    _ = SetFocus(button);
                    break;
                }
            }

            Drain(d);
            // A window DC BitBlt returns BLACK for the parts outside the screen (small CI screen at 144 DPI: the right part of the wide
            // boxes was off-screen and looked like "ink" in every row). Put the dialog at the screen origin and measure only the left
            // part of each box, where the text is.
            User32.SetWindowPos(d.Hwnd, 0, 0, 0, 0, 0,
                WindowMessages.SwpNoSize | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
            Drain(d);
            RedrawWindow(d.Hwnd, null, 0, RdwAll);
            Drain(d);
            var pixels = SettingsTabAndEditCenterTests.Capture(d.Hwnd, out var width, out _);
            var origin = WindowRect(d.Hwnd);
            foreach (var (id, hwnd) in edits)
            {
                Rect client = default;
                User32.GetClientRect(hwnd, &client);
                var window = WindowRect(hwnd);
                var edge = ((window.Right - window.Left) - (client.Right - client.Left)) / 2;
                var left = window.Left - origin.Left + edge;
                var top = window.Top - origin.Top + edge;
                var cw = window.Right - window.Left - (2 * edge);
                var ch = window.Bottom - window.Top - (2 * edge);
                measured++;
                var line0 = LineHeightOf(hwnd);
                var textWidth = Math.Min(cw, 6 * line0); // "HE08" is ~3 em wide; nothing else is measured
                // Background = the box's own colour: top-left corner of the inner area (left of the text, never ink, never selection; on screen whenever the box is).
                var back = SettingsTabAndEditCenterTests.PixelAt(pixels, width, left, top);
                int first = -1, last = -1;
                var nonBack = 0;
                for (var y = 0; y < ch; y++)
                {
                    for (var x = 0; x < textWidth; x++)
                    {
                        if (SettingsTabAndEditCenterTests.Far(SettingsTabAndEditCenterTests.PixelAt(pixels, width, left + x, top + y), back))
                        {
                            nonBack++;
                            if (first < 0)
                            {
                                first = y;
                            }

                            last = y;
                        }
                    }
                }

                var label = $"{which} {language} {dpi} edit {id}";
                if (dump is { Length: > 0 })
                {
                    Directory.CreateDirectory(dump);
                    File.WriteAllBytes(Path.Combine(dump, $"{which}-{language}-{dpi}-{id}.raw"), pixels);
                }

                if (first < 0 || nonBack < 8)
                {
                    failures.Add($"{label}: no text pixels (box is uniform), client {cw}x{ch}");
                    continue;
                }

                var gapTop = first;
                var gapBottom = ch - 1 - last;
                output.WriteLine($"{label}: window {window.Left},{window.Top}-{window.Right},{window.Bottom} dialog {origin.Left},{origin.Top}-{origin.Right},{origin.Bottom} screen {GetSystemMetrics(0)}x{GetSystemMetrics(1)}, back {back:X6}");
                output.WriteLine($"{label}: client {cw}x{ch}, ink {first}..{last}, gap top {gapTop} bottom {gapBottom}");
                if (Math.Abs(gapTop - gapBottom) > 1 || gapTop < 1 || gapBottom < 1)
                {
                    failures.Add($"{label}: gap top {gapTop} / bottom {gapBottom} (inner height {ch})");
                }

                // Caret / selection / IME: after the subclass the caret must sit on the text rows (inside the moved client area).
                _ = SetFocus(hwnd);
                User32.SendMessage(hwnd, EmSetSel, 4, 4);
                Drain(d);
                var clientTopInWindow = (window.Bottom - window.Top) - (client.Bottom - client.Top) - edge; // window y of the client top
                var pos = (int)User32.SendMessage(hwnd, EmPosFromChar, 1, 0);
                var charY = (short)((pos >> 16) & 0xFFFF);
                var caretY = GetCaretPos(out var caret) ? caret.Y : -1;
                var line = LineHeightOf(hwnd);
                foreach (var (name, y) in new[] { ("char", (int)charY), ("caret", caretY) })
                {
                    if (y < 0)
                    {
                        failures.Add($"{label}: {name} y {y} outside the client area");
                        continue;
                    }

                    // row of the caret/char box top relative to the inner area (like first/last); the box must cover the ink rows
                    var rowTop = clientTopInWindow + y - edge;
                    if (rowTop > first || rowTop + line - 1 < last)
                    {
                        failures.Add($"{label}: {name} rows {rowTop}..{rowTop + line - 1} do not cover the ink rows {first}..{last}");
                    }
                }
            }
        }

        if (which == "benchmark")
        {
            BenchmarkHost.Run(Measure);
        }
        else
        {
            OnSta(() =>
            {
                NativeDialog dialog = which == "zoom"
                    ? new ZoomPromptDialog(150)
                    : new ActionProfilesDialog([new ReviewAction { Name = "Keep", Shortcut = "F6", Destination = "Keep" }]);
                ShowOnOwner(dialog, (d, _) =>
                {
                    Measure(d);
                    d.Close();
                });
                return 0;
            });
        }

        Assert.True(measured >= (which == "profiles" ? 3 : which == "benchmark" ? 2 : 1), $"{which}: measured only {measured} boxes");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static string ClassOf(nint hwnd)
    {
        var name = stackalloc char[32];
        var length = GetClassName(hwnd, name, 32);
        return new string(name, 0, length);
    }

    private static bool IsEditClass(nint hwnd) => ClassOf(hwnd).Equals("Edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>TEXTMETRIC.tmHeight (line cell height, ascent + descent) of the font the box uses.</summary>
    private static int LineHeightOf(nint hwnd)
    {
        var font = (nint)User32.SendMessage(hwnd, DialogConstants.WmGetFont, 0, 0);
        var hdc = GetDC(hwnd);
        var old = SelectObject(hdc, font);
        var metrics = stackalloc int[15];
        GetTextMetrics(hdc, metrics);
        SelectObject(hdc, old);
        _ = ReleaseDC(hwnd, hdc);
        return metrics[0];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCaretPos(out Pt point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetrics(nint hdc, int* metrics);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, char* buffer, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint hwnd, Rect* update, nint region, uint flags);
}

/// <summary>Not in the unsafe test class: <c>await</c> is not allowed there.</summary>
internal static class BenchmarkHost
{
    public static void Run(Action<NativeDialog> body)
    {
        Task.Run(async () =>
        {
            await using var ui = await UiThread.StartAsync();
            var owner = await ui.InvokeAsync(() => CreateOwner());
            try
            {
                var dialog = await ui.InvokeAsync(() =>
                {
                    var d = new BenchmarkDialog(@"C:\bench", ui.Dispatcher, (_, _) => throw new NotSupportedException());
                    d.ShowModeless(owner, ui.Loop);
                    return d;
                });
                await ui.InvokeAsync(() =>
                {
                    body(dialog);
                    dialog.Close();
                });
            }
            finally
            {
                await ui.InvokeAsync(() => DestroyOwner(owner));
            }
        }).GetAwaiter().GetResult();
    }
}
