using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>
/// Bo do (thu cong) cua hop thoai Settings Win32 HIEN TAI, trang Display, de so voi spike ve Direct2D: thoi gian mo trang,
/// thoi gian tung buoc cuon, so cua so/GDI/USER/bo nho, loi ve khi cuon (so pixel lech so voi ve lai toan bo).
/// Chay (desktop an): <c>tools/run-tests-hidden.ps1 -DesktopName SpikeD2DBaseline tests/PhotoReview.Shell.Tests/PhotoReview.Shell.Tests.csproj
/// -c Release --filter FullyQualifiedName~SettingsLegacyBaselineTests --blame-hang-timeout 5m --blame-hang-dump-type none</c>
/// voi bien moi truong <c>PHOTOREVIEW_BASELINE_OUT</c> = duong dan file van ban ket qua.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed unsafe class SettingsLegacyBaselineTests(ITestOutputHelper output)
{
    private const uint Rdw = 0x0001 | 0x0004 | 0x0080 | 0x0100; // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
    private const uint RdwUpdateNow = 0x0100 | 0x0080; // UPDATENOW | ALLCHILDREN
    private const int WarmRuns = 15;
    private const int ScrollIterations = 20;
    private const string PageKey = "Display";

    private static readonly uint[] Dpis = [96, 144];

    private readonly List<string> _lines = [];

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

    [Fact]
    public void Report_LegacySettingsDisplayPage()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        SettingsPresenter.RememberPage("General");
        var pageIndex = IndexOfPage();
        var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };

        Emit("# SettingsLegacyBaselineTests - Win32 Settings, trang " + PageKey + " (index " + pageIndex + "), chrome toi (DarkDialogChrome mac dinh)");
        Emit("when: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + "  os: " + Environment.OSVersion.Version + "  cores: " + Environment.ProcessorCount + "  runtime: " + Environment.Version
            + "  jit-test-host (khong phai exe R2R/AOT: so JIT la can tren)");
        Emit("tag trang = SettingsPresenter.Pages[" + pageIndex + "].Key = " + SettingsPresenter.Pages[pageIndex].Key);

        // Pha 1: mo trang. Lan dau cua tien trinh (cold) o 96 dpi truoc, sau do warm.
        var load = CpuLoad.Start();
        foreach (var dpi in Dpis)
        {
            var runs = new List<OpenResult>();
            for (var i = 0; i < 1 + WarmRuns; i++)
            {
                runs.Add(OpenOnce(settings, pageIndex, dpi));
            }

            ReportOpen(dpi, runs, dpi == Dpis[0]);
        }

        Emit(load.Stop("phase1-open"));
        Flush();

        // Pha 3: bo nho / doi tuong.
        load = CpuLoad.Start();
        foreach (var dpi in Dpis)
        {
            ReportObjects(settings, pageIndex, dpi);
        }

        Emit(load.Stop("phase3-objects"));
        Flush();

        // Pha 2: cuon.
        load = CpuLoad.Start();
        foreach (var dpi in Dpis)
        {
            ReportScroll(settings, pageIndex, dpi);
        }

        Emit(load.Stop("phase2-scroll"));
        Flush();

        // Pha 4: loi ve khi cuon (so pixel lech so voi ve lai toan bo).
        load = CpuLoad.Start();
        foreach (var dpi in Dpis)
        {
            ReportPixels(settings, pageIndex, dpi);
        }

        Emit(load.Stop("phase4-pixels"));
        Flush();
    }

    // ---- pha 1 ------------------------------------------------------------------------------------------------------------

    private readonly record struct OpenResult(double ConstructMs, double ReadyMs, double DpiMs, double PageMs, int ChildrenBefore, int ChildrenAfter);

    private static OpenResult OpenOnce(AppSettings settings, int pageIndex, uint dpi)
    {
        return OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var clock = Stopwatch.GetTimestamp();
            SettingsPresenter.RememberPage("General");
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()), null);
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            var constructed = Ms(clock, Stopwatch.GetTimestamp());
            double ready = 0, dpiMs = 0, page = 0;
            int before = 0, after = 0;
            ShowOnOwner(dialog, (d, _) =>
            {
                var sd = (SettingsDialog)d;
                try
                {
                    ready = Ms(clock, Stopwatch.GetTimestamp());
                    if (dpi != 96)
                    {
                        var t = Stopwatch.GetTimestamp();
                        ChangeDpi(sd, dpi);
                        Drain(sd);
                        dpiMs = Ms(t, Stopwatch.GetTimestamp());
                    }

                    before = CountChildren(sd.Hwnd);
                    var t0 = Stopwatch.GetTimestamp();
                    GoTo(sd, pageIndex);
                    PaintPending(sd, force: false);
                    page = Ms(t0, Stopwatch.GetTimestamp());
                    after = CountChildren(sd.Hwnd);
                }
                finally
                {
                    d.Close();
                }
            });
            return new OpenResult(constructed, ready, dpiMs, page, before, after);
        });
    }

    private void ReportOpen(uint dpi, List<OpenResult> runs, bool firstIsProcessCold)
    {
        var warm = runs.Skip(1).ToList();
        var first = runs[0];
        Emit($"## [open] dpi {dpi}");
        Emit($"first run in {(firstIsProcessCold ? "PROCESS (cold: JIT + first use of everything)" : "this dpi (process already warm)")}: "
            + $"construct={first.ConstructMs:F1} ms, window-ready={first.ReadyMs:F1} ms (construct+create+show), "
            + (dpi != 96 ? $"ChangeDpi={first.DpiMs:F1} ms, " : string.Empty)
            + $"GoTo({PageKey})->painted={first.PageMs:F1} ms, child windows of dialog {first.ChildrenBefore}->{first.ChildrenAfter}");
        Emit($"warm runs n={warm.Count} (each a NEW dialog): median / P95(nearest-rank) / max, ms");
        Emit("  construct SettingsDialog      : " + Stats(warm.Select(r => r.ConstructMs)));
        Emit("  window ready (incl. ctor)     : " + Stats(warm.Select(r => r.ReadyMs)));
        Emit("  create+show only (ready-ctor) : " + Stats(warm.Select(r => r.ReadyMs - r.ConstructMs)));
        if (dpi != 96)
        {
            Emit("  ChangeDpi to " + dpi + " (General page only): " + Stats(warm.Select(r => r.DpiMs)));
        }

        Emit($"  GoTo({PageKey}) -> painted (lazy create + layout + Drain + RedrawWindow UPDATENOW): " + Stats(warm.Select(r => r.PageMs)));
        Emit("  all warm GoTo ms: " + string.Join(", ", warm.Select(r => r.PageMs.ToString("F1", CultureInfo.InvariantCulture))));
    }

    // ---- pha 3 ------------------------------------------------------------------------------------------------------------

    private void ReportObjects(AppSettings settings, int pageIndex, uint dpi)
    {
        var rows = OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var result = new List<string>();
            var baseline = Snapshot();
            SettingsPresenter.RememberPage("General");
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()), null);
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                var sd = (SettingsDialog)d;
                try
                {
                    if (dpi != 96)
                    {
                        ChangeDpi(sd, dpi);
                        Drain(sd);
                    }

                    PaintPending(sd, force: false);
                    var viewport = H(sd, SettingsDialog.ViewportId(pageIndex));
                    var beforePage = Snapshot();
                    var beforeViewport = CountChildren(viewport);
                    var beforeDialog = CountChildren(sd.Hwnd);
                    GoTo(sd, pageIndex);
                    PaintPending(sd, force: false);
                    var afterPage = Snapshot();
                    var afterViewport = CountChildren(viewport);
                    var afterDialog = CountChildren(sd.Hwnd);
                    result.Add($"process before any dialog   : {baseline}");
                    result.Add($"dialog shown (General page) : {beforePage}   child windows: viewport[{PageKey}]={beforeViewport}, whole dialog={beforeDialog}");
                    result.Add($"after GoTo({PageKey}) painted   : {afterPage}   child windows: viewport[{PageKey}]={afterViewport}, whole dialog={afterDialog}");
                    result.Add($"delta opening the page      : GDI {(long)afterPage.Gdi - beforePage.Gdi:+0;-0;0}, USER {(long)afterPage.User - beforePage.User:+0;-0;0}, "
                        + $"private {(afterPage.PrivateBytes - beforePage.PrivateBytes) / 1024.0:+0.0;-0.0;0.0} KB, working set {(afterPage.WorkingSet - beforePage.WorkingSet) / 1024.0:+0.0;-0.0;0.0} KB, "
                        + $"managed heap {(afterPage.Managed - beforePage.Managed) / 1024.0:+0.0;-0.0;0.0} KB, child windows {afterViewport - beforeViewport:+0;-0;0} (viewport) / {afterDialog - beforeDialog:+0;-0;0} (dialog)");
                    var client = ClientRect(viewport);
                    result.Add($"viewport client: {Width(client)}x{Height(client)}, content height {sd.ContentHeight(pageIndex)}");
                }
                finally
                {
                    d.Close();
                }
            });
            return result;
        });
        Emit($"## [objects] dpi {dpi} (GC.Collect before each reading; GDI/USER are process-wide counters)");
        foreach (var row in rows)
        {
            Emit("  " + row);
        }
    }

    private readonly record struct ProcSnap(uint Gdi, uint User, long PrivateBytes, long WorkingSet, long Managed)
    {
        public override string ToString()
            => $"GDI={Gdi}, USER={User}, private={PrivateBytes / 1048576.0:F1} MB, workingSet={WorkingSet / 1048576.0:F1} MB, managedHeap={Managed / 1048576.0:F1} MB";
    }

    private static ProcSnap Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcSnap(GdiObjects(), UserObjects(), process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(true));
    }

    // ---- pha 2 ------------------------------------------------------------------------------------------------------------

    private void ReportScroll(AppSettings settings, int pageIndex, uint dpi)
    {
        var ops = Enum.GetValues<Op>();
        var samples = new List<double>[ops.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = [];
        }

        var firstIteration = new double[ops.Length];
        var info = (Max: 0, Content: 0, Viewport: 0);
        var completed = 0;
        Run(settings, d =>
        {
            Prepare(d, pageIndex, dpi);
            info = (MaxScroll(d, pageIndex), d.ContentHeight(pageIndex), d.ViewportHeight);
            var guard = Stopwatch.StartNew();
            for (var iteration = 0; iteration < ScrollIterations && guard.Elapsed.TotalSeconds < 40; iteration++)
            {
                completed = iteration + 1;
                for (var i = 0; i < ops.Length; i++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    Apply(d, pageIndex, ops[i]);
                    PaintPending(d, force: false);
                    var ms = Ms(t0, Stopwatch.GetTimestamp());
                    if (iteration == 0)
                    {
                        firstIteration[i] = ms;
                    }

                    samples[i].Add(ms);
                }
            }
        });

        Emit($"## [scroll] dpi {dpi}: {ScrollIterations} consecutive runs of the {ops.Length}-step script on ONE dialog; step = send scroll message(s) + Drain + RedrawWindow(UPDATENOW) (pending paint only)");
        Emit($"   viewport height={info.Viewport}, content height={info.Content}, max scroll={info.Max}, iterations completed={completed} (guard 40 s)");
        Emit("   step                n   P50 ms   P95 ms   max ms   (run 1 ms)");
        for (var i = 0; i < ops.Length; i++)
        {
            Emit($"   {i:00}-{ops[i],-15} {samples[i].Count,3}  {Percentile(samples[i], 50),7:F3}  {Percentile(samples[i], 95),7:F3}  {samples[i].Max(),7:F3}   ({firstIteration[i]:F3})");
        }

        var all = samples.SelectMany(s => s).ToList();
        var scrolling = samples.Skip(1).SelectMany(s => s).ToList();
        Emit($"   ALL steps         {all.Count,4}  {Percentile(all, 50),7:F3}  {Percentile(all, 95),7:F3}  {all.Max(),7:F3}");
        Emit($"   scrolling steps only (excl. Initial) n={scrolling.Count}: P50 {Percentile(scrolling, 50):F3}  P95 {Percentile(scrolling, 95):F3}  max {scrolling.Max():F3}");
        var perScript = Enumerable.Range(0, Math.Max(1, completed - 1)).Select(it => samples.Sum(s => s[it])).ToList();
        Emit($"   whole 15-step script: P50 {Percentile(perScript, 50):F2} ms, P95 {Percentile(perScript, 95):F2} ms, max {perScript.Max():F2} ms");
    }

    // ---- pha 4 ------------------------------------------------------------------------------------------------------------

    private void ReportPixels(AppSettings settings, int pageIndex, uint dpi)
    {
        var ops = Enum.GetValues<Op>();
        var actual = new List<Shot>();
        var offsets = new List<int>();
        Run(settings, d =>
        {
            Prepare(d, pageIndex, dpi);
            foreach (var op in ops)
            {
                Apply(d, pageIndex, op);
                PaintPending(d, force: false);
                offsets.Add(d.ScrollOffset(pageIndex));
                actual.Add(Capture(d, pageIndex));
            }
        });

        var expected = new List<Shot>();
        var reached = new List<int>();
        Run(settings, d =>
        {
            Prepare(d, pageIndex, dpi);
            for (var i = 0; i < ops.Length; i++)
            {
                Thumb(d, offsets[i], release: true);
                PaintPending(d, force: true);
                reached.Add(d.ScrollOffset(pageIndex));
                expected.Add(Capture(d, pageIndex));
            }
        });

        Emit($"## [pixels] dpi {dpi}, dark chrome: actual (pending paint only) vs reference (offset set directly + forced full redraw), tolerance 0");
        Emit($"   reference non-blank: {expected.Any(s => s.DistinctSample() > 4)}  (capture {actual[0].Width}x{actual[0].Height})");
        var total = 0;
        var bad = 0;
        for (var i = 0; i < ops.Length; i++)
        {
            if (reached[i] != offsets[i])
            {
                Emit($"   {i:00}-{ops[i],-15} offset user={offsets[i]} reference={reached[i]}  LAYOUT DIFFERS (not comparable)");
                continue;
            }

            var (count, box) = Compare(actual[i], expected[i]);
            total += count == int.MaxValue ? 0 : count;
            bad += count > 0 ? 1 : 0;
            Emit($"   {i:00}-{ops[i],-15} offset {offsets[i],5}  differing pixels: {count}{(count > 0 ? "  bbox " + box : string.Empty)}");
        }

        Emit($"   steps with stale pixels: {bad}/{ops.Length}, total differing pixels: {total}");
    }

    // ---- ha tang ----------------------------------------------------------------------------------------------------------

    private static int IndexOfPage()
    {
        for (var i = 0; i < SettingsPresenter.Pages.Count; i++)
        {
            if (SettingsPresenter.Pages[i].Key == PageKey)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No page with key " + PageKey);
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private static string Stats(IEnumerable<double> values)
    {
        var list = values.ToList();
        return $"median {Percentile(list, 50):F1} / P95 {Percentile(list, 95):F1} / max {list.Max():F1}  (min {list.Min():F1})";
    }

    /// <summary>P50 = trung vi; cac phan vi khac = nearest-rank.</summary>
    private static double Percentile(List<double> values, int percent)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (percent == 50)
        {
            var n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
        }

        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private void Emit(string line)
    {
        _lines.Add(line);
        output.WriteLine(line);
    }

    private void Flush()
    {
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_BASELINE_OUT") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllLines(path, _lines);
        }
    }

    private static void Run(AppSettings settings, Action<SettingsDialog> body)
        => OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            SettingsPresenter.RememberPage("General");
            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()), null);
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            ShowOnOwner(dialog, (d, _) =>
            {
                try
                {
                    body((SettingsDialog)d);
                }
                finally
                {
                    d.Close();
                }
            });
            return 0;
        });

    private static void Prepare(SettingsDialog d, int pageIndex, uint dpi)
    {
        GoTo(d, pageIndex);
        if (dpi != 96)
        {
            ChangeDpi(d, dpi);
        }

        PaintPending(d, force: false);
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
            case Op.PageDown2:
                Bar(d, ScrollConstants.SbPageDown);
                break;
            case Op.ThumbDrag:
                for (var step = 1; step <= 4; step++)
                {
                    Thumb(d, max * (step * 2 + 1) / 11, release: false);
                    PaintPending(d, force: false);
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

    /// <summary>Drain + RedrawWindow + Drain. <paramref name="force"/> = ve lai toan bo; false = chi lan ve dang cho.</summary>
    private static void PaintPending(SettingsDialog d, bool force)
    {
        Drain(d);
        _ = Native.RedrawWindow(d.Hwnd, 0, 0, force ? Rdw : RdwUpdateNow);
        Drain(d);
    }

    private static int CountChildren(nint parent)
    {
        var count = 0;
        Native.EnumChildWindows(parent, (_, _) =>
        {
            count++;
            return true;
        }, 0);
        return count;
    }

    // ---- chup + so sanh (sao chep tu SettingsScrollPaintTests tren nhanh test/settings-scroll-pixel-regression) ---------------

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
    }

    private static Shot Capture(SettingsDialog d, int page)
    {
        var viewport = H(d, SettingsDialog.ViewportId(page));
        var client = ClientRect(viewport);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        var style = User32.GetWindowLongPtr(viewport, WindowMessages.GwlStyle);
        Native.SetWindowLongPtr(viewport, WindowMessages.GwlStyle, style & ~(nint)ScrollConstants.WsClipChildren);
        var source = User32.GetDC(viewport);
        var memory = Native.CreateCompatibleDC(source);
        var bitmap = Native.CreateCompatibleBitmap(source, width, height);
        var old = Gdi32.SelectObject(memory, bitmap);
        try
        {
            Assert.True(Native.BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x00CC0020), "BitBlt from the viewport DC failed");
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
                _ = Native.GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
            }

            return new Shot(width, height, pixels);
        }
        finally
        {
            Gdi32.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            _ = User32.ReleaseDC(viewport, source);
            Native.SetWindowLongPtr(viewport, WindowMessages.GwlStyle, style);
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

    // ---- tai CPU toan may (chu thich nhieu) -----------------------------------------------------------------------------

    private sealed class CpuLoad
    {
        private long _idle, _kernel, _user;
        private TimeSpan _process;
        private long _clock;

        public static CpuLoad Start()
        {
            var load = new CpuLoad();
            Native.GetSystemTimes(out load._idle, out load._kernel, out load._user);
            using var p = Process.GetCurrentProcess();
            load._process = p.TotalProcessorTime;
            load._clock = Stopwatch.GetTimestamp();
            return load;
        }

        public string Stop(string label)
        {
            Native.GetSystemTimes(out var idle, out var kernel, out var user);
            using var p = Process.GetCurrentProcess();
            var total = (kernel - _kernel) + (user - _user);
            var busy = total - (idle - _idle);
            var machine = total <= 0 ? 0 : 100.0 * busy / total;
            var wall = Ms(_clock, Stopwatch.GetTimestamp());
            var own = (p.TotalProcessorTime - _process).TotalMilliseconds;
            var ownPercent = 100.0 * own / (wall * Environment.ProcessorCount);
            return $"[cpu] {label}: whole-machine CPU avg {machine:F0}% over {wall / 1000:F1} s (this test process alone {ownPercent:F1}% of all cores) -> noise context";
        }
    }

    private static class Native
    {
        public delegate bool EnumChildProc(nint hwnd, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

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

