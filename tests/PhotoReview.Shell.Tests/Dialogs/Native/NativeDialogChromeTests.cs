using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// Giao diện tối của hộp thoại Win32 KHÔNG phụ thuộc chủ đề hệ thống (fix/native-dialog-chrome-no-ordinal). Cửa sổ THẬT, chụp bằng
/// <c>PrintWindow</c> rồi đo điểm ảnh: nền tối, tương phản chữ/nền (WCAG &gt;= 4,5:1 cho chữ thường), màu từng trạng thái, và
/// KHÔNG đổi khi chế độ ứng dụng của uxtheme bị đổi qua ordinal (mô phỏng "chế độ hỏng ordinal" của Windows 11 build 26300: nền không
/// đổi dù cờ dark bật). Chạy ở cả comctl32 v5 (testhost không manifest) và v6 (manifest nhúng = exe thật).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class NativeDialogChromeTests
{
    private const double MinimumTextContrast = 4.5;
    private const double MinimumGraphicContrast = 3.0;
    private const double MaximumBackgroundLuminance = 0.12;

    // ---- đo điểm ảnh ----------------------------------------------------------------------------------------------------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern unsafe int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);

    /// <summary>Ảnh chụp một cửa sổ (RGB 0xRRGGBB) cùng gốc toạ độ màn hình của nó.</summary>
    private sealed class Shot(int width, int height, uint[] rgb, int originX, int originY)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int OriginX { get; } = originX;

        public int OriginY { get; } = originY;

        /// <summary>Màu tại toạ độ màn hình (x, y).</summary>
        public uint At(int screenX, int screenY)
        {
            var x = screenX - OriginX;
            var y = screenY - OriginY;
            return x < 0 || y < 0 || x >= Width || y >= Height ? 0xFF00FF : rgb[(y * Width) + x];
        }
    }

    private static unsafe Shot Capture(nint hwnd)
    {
        var rect = WindowRect(hwnd);
        var width = Width(rect);
        var height = Height(rect);
        var screen = User32.GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = Gdi32.SelectObject(memory, bitmap);
        try
        {
            PrintWindow(hwnd, memory, 0);
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
                _ = GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
            }

            var rgb = new uint[width * height];
            for (var n = 0; n < rgb.Length; n++)
            {
                rgb[n] = ((uint)pixels[(n * 4) + 2] << 16) | ((uint)pixels[(n * 4) + 1] << 8) | pixels[n * 4];
            }

            return new Shot(width, height, rgb, rect.Left, rect.Top);
        }
        finally
        {
            Gdi32.DeleteObject(bitmap);
            DeleteDC(memory);
            _ = User32.ReleaseDC(0, screen);
        }
    }

    private static uint Rgb(string key)
    {
        var color = PhotoReview.Shell.Win32.Overlay.DarkPalette.Get(key);
        return ((uint)Math.Round(color.R * 255) << 16) | ((uint)Math.Round(color.G * 255) << 8) | (uint)Math.Round(color.B * 255);
    }

    private static double Luminance(uint rgb)
    {
        static double Channel(uint v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));
    }

    private static double Contrast(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Hình chữ nhật CLIENT của điều khiển theo toạ độ màn hình.</summary>
    private static unsafe Rect ClientOnScreen(nint hwnd)
    {
        var window = WindowRect(hwnd);
        var client = ClientRect(hwnd);
        var origin = new Point { X = window.Left, Y = window.Top };
        User32.ScreenToClient(hwnd, &origin); // = -(offset của client trong cửa sổ)
        var left = window.Left - origin.X;
        var top = window.Top - origin.Y;
        return new Rect { Left = left, Top = top, Right = left + Width(client), Bottom = top + Height(client) };
    }

    /// <summary>(nền = màu nhiều nhất, mực = màu tương phản nhất với nền) trong vùng client của điều khiển.</summary>
    private static unsafe (uint Background, uint Ink, double Contrast, int InkPixels) Analyse(Shot shot, nint control)
    {
        var rect = ClientOnScreen(control);
        var histogram = new Dictionary<uint, int>();
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var color = shot.At(x, y);
                histogram[color] = histogram.GetValueOrDefault(color) + 1;
            }
        }

        var background = histogram.OrderByDescending(p => p.Value).First().Key;
        uint ink = background;
        var best = 1.0;
        foreach (var color in histogram.Keys)
        {
            var contrast = Contrast(color, background);
            if (contrast > best)
            {
                best = contrast;
                ink = color;
            }
        }

        var inkPixels = histogram.Where(p => p.Key != background).Sum(p => p.Value);
        return (background, ink, best, inkPixels);
    }

    private static unsafe string ClassOf(nint hwnd)
    {
        var buffer = stackalloc char[64];
        var length = User32.GetClassName(hwnd, buffer, 64);
        return new string(buffer, 0, length);
    }

    private static List<nint> Children(nint dialog)
    {
        var result = new List<nint>();
        for (var child = User32.GetWindow(dialog, 5 /* GW_CHILD */); child != 0; child = User32.GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            result.Add(child);
        }

        return result;
    }

    private sealed record Finding(string Control, string Class, string Problem);

    /// <summary>
    /// Quy tắc "giao diện tối đọc được" cho MỌI điều khiển hiện của hộp thoại: nền client có độ sáng thấp (không trắng/xám sáng) và
    /// mực (chữ/biểu tượng) tương phản &gt;= 4,5:1 với nền của chính nó (điều khiển có chữ) hoặc &gt;= 3:1 (đồ hoạ).
    /// </summary>
    private static List<Finding> DarkAndReadableFindings(NativeDialog dialog)
    {
        var shot = Capture(dialog.Hwnd);
        var findings = new List<Finding>();
        foreach (var child in Children(dialog.Hwnd))
        {
            var name = $"id {User32.GetDlgCtrlID(child)} '{Truncate(TextOf(child))}'";
            var cls = ClassOf(child);
            if (!User32.IsWindowVisible(child) || Width(WindowRect(child)) < 8 || Height(WindowRect(child)) < 6)
            {
                continue;
            }

            var (background, ink, contrast, inkPixels) = Analyse(shot, child);
            var isGraphic = cls is "msctls_progress32" or "msctls_trackbar32";
            var isFrame = cls == "Button" && (((uint)User32.GetWindowLongPtr(child, WindowMessages.GwlStyle)) & 0xF) == 7;
            var transparent = cls == "Static" && (isFrame || string.IsNullOrEmpty(TextOf(child)));
            if (transparent)
            {
                continue;
            }

            if (Luminance(background) > MaximumBackgroundLuminance)
            {
                findings.Add(new Finding(name, cls, $"light background #{background:X6} (luminance {Luminance(background):0.00})"));
            }

            if (inkPixels > 0)
            {
                var needed = isGraphic || isFrame ? MinimumGraphicContrast : MinimumTextContrast;
                if (contrast < needed)
                {
                    findings.Add(new Finding(name, cls, $"contrast {contrast:0.00}:1 < {needed} (ink #{ink:X6} on #{background:X6})"));
                }
            }
        }

        return findings;
    }

    private static string Truncate(string text) => text.Length <= 24 ? text : text[..24];

    // ---- khung chạy ----------------------------------------------------------------------------------------------------

    private static T RunDialog<T>(NativeDialog dialog, Func<NativeDialog, T> body, bool visualStyles)
        => OnSta(() =>
        {
            using var styles = visualStyles ? ActivateVisualStyles() : null;
            T result = default!;
            ShowOnOwner(dialog, (d, _) =>
            {
                Drain(d);
                result = body(d);
                d.Close();
            });
            return result;
        });

    private static IEnumerable<(string Name, Func<NativeDialog> Make, Action<NativeDialog>? Prepare)> Dialogs()
    {
        var display = DiagnosticsPresenter.Build(new ReviewMetrics().Snapshot());
        var skipped = Enumerable.Range(0, 12).Select(i => new SkippedEntry($@"C:\photos\trip\IMG_{i:0000}.jpg", "The process cannot access the file.")).ToArray();
        var batch = Enumerable.Range(0, 12).Select(i => new BatchReviewItem($@"C:\photos\trip\IMG_{i:0000}.jpg", 1_000_000 + (i * 12345))).ToArray();
        var actions = new List<ReviewAction>
        {
            new() { Name = "Keep", Shortcut = "F6", Operation = FileOperationType.Move, Destination = "Keep", Confirm = false },
            new() { Name = "Copy to the review folder", Shortcut = "F7", Operation = FileOperationType.Copy, Destination = "Review", Confirm = true },
            new() { Name = "Trash", Shortcut = "F8", Operation = FileOperationType.Recycle, Destination = "x", Confirm = false },
        };
        yield return ("gallery", () => new ControlGalleryDialog(), null);
        yield return ("zoom", () => new ZoomPromptDialog(175), null);
        yield return ("zoom-error", () => new ZoomPromptDialog(175), d =>
        {
            SetText(d, ZoomPromptDialog.EditId, "9999");
            Command(d, ZoomPromptDialog.ApplyId);
        });
        yield return ("diagnostics", () => new DiagnosticsDialog(display), null);
        yield return ("skipped", () => new ListDialog(ListDialogContent.ForSkippedFiles(skipped)), null);
        yield return ("batch", () => new ListDialog(ListDialogContent.ForBatchReview(batch)), null);
        yield return ("actionprofiles", () => new ActionProfilesDialog(actions), d =>
        {
            ListBoxControl.SelectSingle(Item(d, ActionProfilesDialog.ListId), 1);
            Command(d, ActionProfilesDialog.ListId, ControlConstants.LbnSelChange);
        });
    }

    public static TheoryData<string, bool, bool, bool> Matrix()
    {
        var data = new TheoryData<string, bool, bool, bool>();
        foreach (var dialog in Dialogs())
        {
            foreach (var visualStyles in new[] { false, true })
            {
                data.Add(dialog.Name, visualStyles, false, true);
                data.Add(dialog.Name, visualStyles, true, false);
            }
        }

        return data;
    }

    // ---- 1. nền tối + chữ đọc được ở mọi hộp thoại/điều khiển ---------------------------------------------------------------

    /// <param name="name">Hộp thoại.</param>
    /// <param name="visualStyles">comctl32 v6 (exe thật) hay v5 (testhost).</param>
    /// <param name="highDpi">Bố cục sau WM_DPICHANGED sang 144 dpi (150 %).</param>
    /// <param name="english">Ngôn ngữ giao diện (en) hay tiếng Việt.</param>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryControl_HasADarkBackground_AndReadableText(string name, bool visualStyles, bool highDpi, bool english)
    {
        var entry = Dialogs().Single(d => d.Name == name);
        using var scope = TestLocalization.Use(english ? TestLocalization.English : TestLocalization.Vietnamese);
        var findings = RunDialog(entry.Make(), d =>
        {
            entry.Prepare?.Invoke(d);
            if (highDpi)
            {
                NativeDialogSnapshotTests.ChangeDpi(d, 144);
            }

            Drain(d);
            return DarkAndReadableFindings(d);
        }, visualStyles);

        Assert.True(findings.Count == 0,
            $"{name} (v6={visualStyles}, 150%={highDpi}): " + string.Join("; ", findings.Select(f => $"{f.Class} {f.Control}: {f.Problem}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Benchmark_HasADarkBackground_AndReadableText(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var findings = RunBenchmark(visualStyles, dialog =>
        {
            ListBoxControl.SelectSingle(Item(dialog, BenchmarkDialog.ProfilesId), 1);
            Drain(dialog);
            return DarkAndReadableFindings(dialog);
        });

        Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => $"{f.Class} {f.Control}: {f.Problem}")));
    }

    /// <summary>
    /// Cửa sổ Benchmark (modeless, UI thread thật của shell). Activation context của comctl32 v6 PHẢI được bật VÀ tắt trong CÙNG
    /// MỘT việc của dispatcher: user32 khôi phục ngăn xếp activation khi một WndProc trả về, nên nếu việc bật chạy trong
    /// WM_APP+1 (loop đang chờ) rồi việc tắt chạy ở lượt khác thì DeactivateActCtx ném SEHException (đo: 15/60 lượt khi tách hai
    /// việc; CI đỏ ngẫu nhiên ở ca visualStyles: True). Gộp một việc làm lượt bật/tắt lồng nhau đúng thứ tự như ở OnSta.
    /// </summary>
    private static T RunBenchmark<T>(bool visualStyles, Func<BenchmarkDialog, T> body)
    {
        return Task.Run(async () =>
        {
            await using var ui = await UiThread.StartAsync();
            return await ui.InvokeAsync(() =>
            {
                using var styles = visualStyles ? ActivateVisualStyles() : null;
                var owner = CreateOwner();
                try
                {
                    var dialog = new BenchmarkDialog(@"C:\Photos\trip", ui.Dispatcher,
                        (profile, _) => new IdleSession(), (_, _, _) => null, _ => { }, (_, _, _) => ["a.jpg"],
                        _ => Task.FromResult(@"C:\Temp\summary.json"));
                    dialog.ShowModeless(owner, ui.Loop);
                    try
                    {
                        Drain(dialog);
                        return body(dialog);
                    }
                    finally
                    {
                        dialog.Close();
                    }
                }
                finally
                {
                    DestroyOwner(owner);
                }
            });
        }).GetAwaiter().GetResult();
    }

    private sealed class IdleSession : IBenchmarkProfileSession
    {
        public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken)
            => Task.FromResult(new BenchmarkReport("idle", DateTimeOffset.UnixEpoch, folder, []));

        public ValueTask DisposeAsync() => default;
    }

    // ---- 2. KHÔNG phụ thuộc chế độ ứng dụng của uxtheme (mô phỏng ordinal hỏng) -------------------------------------------

    /// <summary>
    /// Các hàm uxtheme theo ordinal (không công bố) mà bản cũ của khung gọi: 135 SetPreferredAppMode, 136 FlushMenuThemes. Ở đây chỉ
    /// TEST gọi chúng để đưa tiến trình vào đủ mọi chế độ (kể cả "dark nửa vời" của Windows 11 build 26300); hộp thoại phải KHÔNG đổi.
    /// </summary>
    private static unsafe class UxthemeAppMode
    {
        private static readonly nint Module = Kernel32.LoadLibraryEx("uxtheme.dll", 0, Kernel32.LoadLibrarySearchSystem32);

        public static bool Available => Module != 0 && Kernel32.GetProcAddress(Module, 135) != 0;

        public static void Set(int mode)
        {
            var setMode = (delegate* unmanaged<int, int>)Kernel32.GetProcAddress(Module, 135);
            if (setMode is not null)
            {
                setMode(mode);
            }

            var flush = (delegate* unmanaged<void>)Kernel32.GetProcAddress(Module, 136);
            if (flush is not null)
            {
                flush();
            }
        }
    }

    /// <summary>Vùng client của mọi điều khiển (theo thứ tự) để so hai ảnh chụp chỉ ở phần giao diện của hộp thoại.</summary>
    private static List<uint> ControlPixels(NativeDialog dialog)
    {
        var shot = Capture(dialog.Hwnd);
        var pixels = new List<uint>();
        foreach (var child in Children(dialog.Hwnd))
        {
            var window = WindowRect(child);
            for (var y = window.Top; y < window.Bottom; y++)
            {
                for (var x = window.Left; x < window.Right; x++)
                {
                    pixels.Add(shot.At(x, y));
                }
            }
        }

        return pixels;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Look_DoesNotChange_WhateverThePreferredAppModeIs_EvenWhenItFlipsWhileOpen(bool visualStyles)
    {
        if (!UxthemeAppMode.Available)
        {
            return; // Windows without the ordinal: nothing to simulate (the dialog can not depend on it either)
        }

        using var scope = TestLocalization.Use(TestLocalization.English);
        try
        {
            var baseline = Snapshot(visualStyles, appMode: 0, flipTo: null);
            foreach (var mode in new[] { 1, 2, 3 })
            {
                Assert.True(baseline.SequenceEqual(Snapshot(visualStyles, mode, flipTo: null)), $"app mode {mode} changed the controls' pixels");
            }

            Assert.True(baseline.SequenceEqual(Snapshot(visualStyles, 2, flipTo: 3)), "ForceDark -> ForceLight while open changed the pixels");
            Assert.True(baseline.SequenceEqual(Snapshot(visualStyles, 3, flipTo: 2)), "ForceLight -> ForceDark while open changed the pixels");
        }
        finally
        {
            UxthemeAppMode.Set(0);
        }
    }

    private static List<uint> Snapshot(bool visualStyles, int appMode, int? flipTo)
        => OnSta(() =>
        {
            using var styles = visualStyles ? ActivateVisualStyles() : null;
            UxthemeAppMode.Set(appMode);
            List<uint> pixels = [];
            ShowOnOwner(new ControlGalleryDialog(), (d, _) =>
            {
                Drain(d);
                if (flipTo is { } mode)
                {
                    UxthemeAppMode.Set(mode);
                    BroadcastThemeChanged(d);
                    Drain(d);
                }

                pixels = ControlPixels(d);
                d.Close();
            });
            return pixels;
        });

    private static void BroadcastThemeChanged(NativeDialog dialog)
    {
        User32.SendMessage(dialog.Hwnd, ControlConstants.WmThemeChanged, 0, 0);
        foreach (var child in Children(dialog.Hwnd))
        {
            User32.SendMessage(child, ControlConstants.WmThemeChanged, 0, 0);
        }
    }

    // ---- 3. màu từng trạng thái ----------------------------------------------------------------------------------------

    private static (uint Centre, uint Edge) ButtonColors(NativeDialog dialog, int id)
    {
        var shot = Capture(dialog.Hwnd);
        var rect = WindowRect(Item(dialog, id));
        return (shot.At(rect.Left + 6, rect.Top + 6), shot.At(rect.Left + 1, ((rect.Top + rect.Bottom) / 2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushButton_FillFollowsTheState_Rest_Hover_Pressed_Disabled(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (rest, hover, pressed, disabledFill, disabledContrast) = RunDialog(new ControlGalleryDialog(), d =>
        {
            const int id = ControlGalleryDialog.TipButton;
            var button = Item(d, id);
            Drain(d);
            var restColor = ButtonColors(d, id).Centre;
            User32.SendMessage(button, ControlConstants.WmMouseMove, 0, 0x00050005); // no Drain: TrackMouseEvent posts WM_MOUSELEAVE (cursor is elsewhere)
            var hoverColor = ButtonColors(d, id).Centre;
            User32.SendMessage(button, 0x00F3 /* BM_SETSTATE */, 1, 0);
            Drain(d);
            var pressedColor = ButtonColors(d, id).Centre;
            User32.SendMessage(button, 0x00F3, 0, 0);
            User32.SendMessage(button, ControlConstants.WmMouseLeave, 0, 0);
            User32.EnableWindow(button, false);
            Drain(d);
            var shot = Capture(d.Hwnd);
            var analysed = Analyse(shot, button);
            return (restColor, hoverColor, pressedColor, ButtonColors(d, id).Centre, analysed.Contrast);
        }, visualStyles);

        Assert.Equal(Rgb("Dark.Button"), rest);
        Assert.Equal(Rgb("Dark.ButtonHover"), hover);
        Assert.Equal(Rgb("Dark.ButtonPressed"), pressed);
        Assert.Equal(Rgb("Dark.Button"), disabledFill);
        Assert.True(disabledContrast >= MinimumTextContrast, $"disabled text contrast {disabledContrast:0.00}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckBoxAndRadio_DrawTheirCheckedStateWithTheAccentColour(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (checkedBox, uncheckedBox, radioOn, radioOff) = RunDialog(new ControlGalleryDialog(), d =>
        {
            Drain(d);
            var shot = Capture(d.Hwnd);
            uint Centre(int id)
            {
                var rect = WindowRect(Item(d, id));
                return shot.At(rect.Left + 3, rect.Top + (Height(rect) / 2));
            }

            // Fill of the box (left edge + a few px in) tells checked (accent) from unchecked (input colour).
            var onBox = Centre(ControlGalleryDialog.Check);
            ButtonStateControl.SetChecked(Item(d, ControlGalleryDialog.Check), false);
            Drain(d);
            var offShot = Capture(d.Hwnd);
            var offRect = WindowRect(Item(d, ControlGalleryDialog.Check));
            var offBox = offShot.At(offRect.Left + 3, offRect.Top + (Height(offRect) / 2));

            var radioRect = WindowRect(Item(d, ControlGalleryDialog.RadioA));
            var radioCentre = (radioRect.Left + 6, radioRect.Top + (Height(radioRect) / 2));
            var radioOnColor = shot.At(radioCentre.Item1, radioCentre.Item2);
            ButtonStateControl.SetChecked(Item(d, ControlGalleryDialog.RadioA), false);
            Drain(d);
            var radioOffColor = Capture(d.Hwnd).At(radioCentre.Item1, radioCentre.Item2);
            return (onBox, offBox, radioOnColor, radioOffColor);
        }, visualStyles);

        Assert.Equal(Rgb("Dark.Accent"), checkedBox);
        Assert.Equal(Rgb("Dark.Input"), uncheckedBox);
        Assert.Equal(Rgb("Dark.Accent"), radioOn);
        Assert.Equal(Rgb("Dark.Input"), radioOff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedListViewRow_UsesTheSelectionColour_FocusedOrNot(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (unfocused, focused, otherRow, ink) = RunDialog(new ControlGalleryDialog(), d =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            ListViewControl.SelectRow(report, 2);
            User32.SetFocus(Item(d, ControlGalleryDialog.Combo));
            Drain(d);
            uint Sample(int row, int dx)
            {
                var shot = Capture(d.Hwnd);
                var client = ClientOnScreen(report);
                var rowTop = client.Top + HeaderHeight(report) + (row * RowHeight(report));
                return shot.At(client.Left + dx, rowTop + (RowHeight(report) / 2));
            }

            // dx = 6: inside the first column, left of any glyph of the rows used here (row 2 and 3 have an empty first cell).
            var unfocusedColor = Sample(2, 6);
            User32.SetFocus(report);
            Drain(d);
            var focusedColor = Sample(2, 6);
            var other = Sample(3, 6);
            var shotAll = Capture(d.Hwnd);
            var analysed = Analyse(shotAll, report);
            return (unfocusedColor, focusedColor, other, analysed.Contrast);
        }, visualStyles);

        Assert.Equal(Rgb("Dark.Selection"), unfocused);
        Assert.Equal(Rgb("Dark.Selection"), focused);
        Assert.NotEqual(Rgb("Dark.Selection"), otherRow);
        Assert.True(ink >= MinimumTextContrast);
    }

    private static int HeaderHeight(nint listView)
    {
        var header = ListViewControl.Header(listView);
        return Height(WindowRect(header));
    }

    private static unsafe int RowHeight(nint listView)
    {
        Rect rect = default;
        rect.Left = 0; // LVIR_BOUNDS
        User32.SendMessage(listView, 0x100E /* LVM_GETITEMRECT */, 0, (nint)(&rect));
        return Math.Max(1, rect.Bottom - rect.Top);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Progress_FillsExactlyTheFractionInTheAccentColour(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (accentRun, width, info) = RunDialog(new ControlGalleryDialog(), d =>
        {
            ProgressControl.SetFraction(Item(d, ControlGalleryDialog.Progress), 0.4);
            Drain(d);
            var shot = Capture(d.Hwnd);
            var client = ClientOnScreen(Item(d, ControlGalleryDialog.Progress));
            var y = (client.Top + client.Bottom) / 2;
            var run = 0;
            for (var x = client.Left; x < client.Right; x++)
            {
                if (shot.At(x, y) == Rgb("Dark.Accent"))
                {
                    run++;
                }
            }

            return (run, Width(client), $"client {client.Left},{client.Top},{client.Right},{client.Bottom} mid #{shot.At(client.Left + 3, y):X6}");
        }, visualStyles);

        Assert.True(accentRun / (double)width is >= 0.37 and <= 0.43, $"{accentRun}/{width} {info}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditAndListBox_HaveAFlatDarkBorder_NotTheSystem3DEdge(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var (editEdge, editInner, listEdge) = RunDialog(new ControlGalleryDialog(), d =>
        {
            Drain(d);
            var shot = Capture(d.Hwnd);
            var edit = WindowRect(Item(d, ControlGalleryDialog.Count));
            var list = WindowRect(Item(d, ControlGalleryDialog.BadgeList));
            var mid = (edit.Top + edit.Bottom) / 2;
            return (shot.At(edit.Left, mid), shot.At(edit.Left + 1, mid), shot.At(list.Left, (list.Top + list.Bottom) / 2));
        }, visualStyles);

        Assert.Equal(Rgb("Dark.BorderStrong"), editEdge);
        Assert.Equal(Rgb("Dark.Input"), editInner);
        Assert.Equal(Rgb("Dark.BorderStrong"), listEdge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListViewHeader_FillerRightOfTheLastColumn_IsDark(bool visualStyles)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var filler = RunDialog(new ControlGalleryDialog(), d =>
        {
            var report = Item(d, ControlGalleryDialog.Report);
            for (var i = 0; i < 10; i++)
            {
                ListViewControl.SetColumnWidth(report, i, 20); // narrow columns: the header has a large unused area on the right
            }

            Drain(d);
            var header = ListViewControl.Header(report);
            var rect = WindowRect(header);
            return Capture(d.Hwnd).At(rect.Right - 6, (rect.Top + rect.Bottom) / 2);
        }, visualStyles);

        Assert.True(Luminance(filler) <= MaximumBackgroundLuminance, $"header filler #{filler:X6}");
    }

    // ---- 4. phân loại ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Painter_IsAttachedToEveryControlClassItDraws_AndNotToStaticLabels()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var attached = RunDialog(new ControlGalleryDialog(), d => new Dictionary<int, bool>
        {
            [ControlGalleryDialog.Combo] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Combo)),
            [ControlGalleryDialog.BadgeList] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.BadgeList)),
            [ControlGalleryDialog.Report] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Report)),
            [ControlGalleryDialog.Progress] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Progress)),
            [ControlGalleryDialog.Slider] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Slider)),
            [ControlGalleryDialog.Notes] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Notes)),
            [ControlGalleryDialog.Check] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Check)),
            [ControlGalleryDialog.RadioA] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.RadioA)),
            [ControlGalleryDialog.Group] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.Group)),
            [ControlGalleryDialog.TipButton] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.TipButton)),
            [NativeDialog.IdOk] = NativeDialogChromePainter.IsAttached(Item(d, NativeDialog.IdOk)),
            [ControlGalleryDialog.NameLabel] = NativeDialogChromePainter.IsAttached(Item(d, ControlGalleryDialog.NameLabel)),
        }, visualStyles: false);

        foreach (var (id, isAttached) in attached.Where(p => p.Key != ControlGalleryDialog.NameLabel))
        {
            Assert.True(isAttached, $"control {id} has no hand-drawn chrome");
        }

        Assert.False(attached[ControlGalleryDialog.NameLabel]);
    }

    [Theory]
    [InlineData("Button", 0u, "PushButton")]
    [InlineData("Button", 1u, "PushButton")]
    [InlineData("Button", 3u, "CheckBox")]
    [InlineData("Button", 9u, "RadioButton")]
    [InlineData("Button", 7u, "GroupBox")]
    [InlineData("Button", 0x100Bu, "None")] // BS_OWNERDRAW: the owner paints it
    [InlineData("Button", 0x1003u, "PushButton")] // BS_AUTOCHECKBOX | BS_PUSHLIKE
    [InlineData("ComboBox", 3u, "ComboBox")] // CBS_DROPDOWNLIST
    [InlineData("ComboBox", 0x13u, "None")] // + CBS_OWNERDRAWFIXED
    [InlineData("ComboBox", 2u, "None")] // CBS_DROPDOWN (has an edit child)
    [InlineData("Edit", 0u, "EditBorder")]
    [InlineData("ListBox", 0u, "ListBoxBorder")]
    [InlineData("ScrollBar", 0u, "None")]
    [InlineData("Static", 0u, "None")]
    [InlineData("Static", 0x10u, "EtchedStatic")]
    [InlineData("Trackbar", 0u, "Trackbar")]
    [InlineData("Trackbar", 2u, "None")] // TBS_VERT
    [InlineData("Progress", 0u, "Progress")]
    [InlineData("Progress", 8u, "None")] // PBS_MARQUEE
    public void KindFor_FollowsTheClassAndTheStyle(string controlClass, uint style, string expected)
    {
        var cls = Enum.Parse<DialogControlClass>(controlClass);
        var result = OnSta(() =>
        {
            CommonControlsInit.Ensure();
            var owner = CreateOwner();
            var windowClass = DialogTemplate.WindowClassName(cls);
            var hwnd = User32.CreateWindowEx(0, windowClass, "x", 0x40000000u /* WS_CHILD */ | style, 0, 0, 50, 20, owner, 0,
                Kernel32.GetModuleHandle(0), 0);
            Assert.NotEqual(0, hwnd);
            try
            {
                return NativeDialogChromePainter.KindFor(hwnd, cls);
            }
            finally
            {
                User32.DestroyWindow(hwnd);
                DestroyOwner(owner);
            }
        });

        Assert.Equal(Enum.Parse<ChromePaintKind>(expected), result);
    }
}
