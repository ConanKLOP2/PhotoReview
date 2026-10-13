using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Golden;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.PixelGolden;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-27 (thủ công, desktop ẩn): chụp bằng BitBlt từ DC cửa sổ (pixel thật, không PrintWindow) để NHÌN menu tối, hộp thoại phụ, HUD và
/// emoji. <c>PHOTOREVIEW_WP27_DIR=&lt;thư mục&gt; dotnet test --filter FullyQualifiedName~Wp27VisualCheckTests</c>; không có biến thì bỏ qua.
/// Mỗi ảnh có một dòng trong <c>capture-log.txt</c> (kích thước, tỉ lệ pixel gần trắng, số màu, số điểm khác PrintWindow).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class Wp27VisualCheckTests
{
    private const int SbTop = 6;
    private const int SbBottom = 7;
    private static readonly object LogGate = new();
    private static readonly bool[] Booleans = [false, true];

    private static string? Dir => Environment.GetEnvironmentVariable("PHOTOREVIEW_WP27_DIR");

    private static IEnumerable<(string Code, PhotoReview.Core.Localization.Localizer Loc)> Languages =>
        [("en", TestLocalization.English), ("vi", TestLocalization.Vietnamese)];

    // ---- capture -----------------------------------------------------------------------------------------------------------------

    /// <summary>BitBlt từ DC cửa sổ (pixel hiện trên màn hình của desktop). Ghi PNG + dòng log; trả số điểm khác PrintWindow.</summary>
    internal static void Capture(string dir, string name, nint hwnd)
    {
        var rect = WindowRect(hwnd);
        var width = Width(rect);
        var height = Height(rect);
        if (width <= 0 || height <= 0)
        {
            Log(dir, $"{name}: EMPTY RECT {width}x{height}");
            return;
        }

        var bits = new byte[width * height * 4];
        var printed = new byte[width * height * 4];
        var diff = 0;
        var attempts = 0;
        // A window of a hidden desktop that has not painted yet shows the SHARED screen surface (real desktop pixels!): force the
        // paint (frame + children) and retry until BitBlt and PrintWindow agree (3 tries); a lasting difference is logged as DIFF.
        while (attempts < 3)
        {
            attempts++;
            _ = W.RedrawWindow(hwnd, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400);
            var windowDc = W.GetWindowDC(hwnd);
            var memory = W.CreateCompatibleDC(windowDc);
            var bitmap = W.CreateCompatibleBitmap(windowDc, width, height);
            var old = W.SelectObject(memory, bitmap);
            try
            {
                _ = W.BitBlt(memory, 0, 0, width, height, windowDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
                ReadBits(memory, bitmap, old, width, height, bits);
                old = W.SelectObject(memory, bitmap);
                _ = W.PrintWindow(hwnd, memory, 0);
                ReadBits(memory, bitmap, old, width, height, printed);
            }
            finally
            {
                W.DeleteObject(bitmap);
                W.DeleteDC(memory);
                _ = W.ReleaseDC(hwnd, windowDc);
            }

            diff = 0;
            for (var i = 0; i < bits.Length; i += 4)
            {
                if (bits[i] != printed[i] || bits[i + 1] != printed[i + 1] || bits[i + 2] != printed[i + 2])
                {
                    diff++;
                }
            }

            if (diff == 0)
            {
                break;
            }

            SpinFor(150);
        }

        var near = 0;
        var colours = new HashSet<int>();
        for (var i = 0; i < bits.Length; i += 4)
        {
            var luma = ((0.2126 * bits[i + 2]) + (0.7152 * bits[i + 1]) + (0.0722 * bits[i])) / 255.0;
            if (luma > 0.85)
            {
                near++;
            }

            if (colours.Count < 5000)
            {
                colours.Add(bits[i] | (bits[i + 1] << 8) | (bits[i + 2] << 16));
            }
        }

        File.WriteAllBytes(Path.Combine(dir, name + ".png"), NativeDialogSnapshotTests.EncodePng(bits, width, height));
        Log(dir, string.Create(CultureInfo.InvariantCulture,
            $"{name}: {width}x{height} nearWhite={near * 100.0 / (width * height):F1}% colours={colours.Count} attempts={attempts} bitbltVsPrintWindowDiffPixels={diff}{(diff == 0 ? "" : " DIFF")}"));
    }

    private static unsafe void ReadBits(nint memory, nint bitmap, nint old, int width, int height, byte[] target)
    {
        var info = new byte[40];
        BitConverter.GetBytes(40).CopyTo(info, 0);
        BitConverter.GetBytes(width).CopyTo(info, 4);
        BitConverter.GetBytes(-height).CopyTo(info, 8);
        BitConverter.GetBytes((short)1).CopyTo(info, 12);
        BitConverter.GetBytes((short)32).CopyTo(info, 14);
        fixed (byte* p = target)
        fixed (byte* i = info)
        {
            _ = W.SelectObject(memory, old);
            _ = W.GetDIBits(memory, bitmap, 0, (uint)height, p, i, 0);
        }
    }

    private static void Log(string dir, string line)
    {
        lock (LogGate)
        {
            File.AppendAllText(Path.Combine(dir, "capture-log.txt"), line + Environment.NewLine);
        }
    }

    private static void WritePng(string dir, string name, PixelFrame frame, string note = "")
    {
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), NativeDialogSnapshotTests.EncodePng(frame.Bgra, frame.Width, frame.Height));
        Log(dir, $"{name}: {frame.Width}x{frame.Height} offscreen {note}");
    }

    // ---- dialogs -----------------------------------------------------------------------------------------------------------------

    private static void Shots(string dir, string baseName, NativeDialog dialog, params (string Suffix, Action<NativeDialog> Prepare)[] shots)
    {
        ShowOnOwner(dialog, (d, _) =>
        {
            foreach (var (suffix, prepare) in shots)
            {
                prepare(d);
                Drain(d);
                Capture(dir, baseName + suffix, d.Hwnd);
            }

            d.Close();
        });
    }

    private static void Scroll(NativeDialog d, int command)
    {
        var bar = ((SettingsDialog)d).ControlHandle(SettingsDialog.ScrollId);
        User32.SendMessage(d.Hwnd, ControlConstants.WmVScroll, command, bar);
    }

    private static void Dpi(NativeDialog d, uint dpi)
    {
        if (dpi != d.Dpi)
        {
            NativeDialogSnapshotTests.ChangeDpi(d, dpi);
        }
    }

    /// <summary>Shows the keyboard cues (clears "hide focus") and focuses the first visible, enabled tab stop below a control.</summary>
    private static void FocusFirstTabStop(NativeDialog d, int rootId)
    {
        User32.SendMessage(d.Hwnd, 0x0127 /* WM_CHANGEUISTATE */, (nint)((1 << 16) | 2) /* UISF_HIDEFOCUS, UIS_CLEAR */, 0);
        var root = ((SettingsDialog)d).ControlHandle(rootId);
        var target = FindTabStop(root);
        if (target != 0)
        {
            _ = User32.SetFocus(target);
        }
    }

    private static nint FindTabStop(nint parent)
    {
        for (var child = W.GetWindow(parent, 5 /* GW_CHILD */); child != 0; child = W.GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            var style = (uint)User32.GetWindowLongPtr(child, WindowMessages.GwlStyle);
            if ((style & 0x10000) != 0 && User32.IsWindowVisible(child) && User32.IsWindowEnabled(child))
            {
                return child;
            }

            var inner = FindTabStop(child);
            if (inner != 0)
            {
                return inner;
            }
        }

        return 0;
    }

    /// <summary>Diagnostic: lists the children of page 0's viewport and re-captures with WS_CLIPSIBLINGS cleared.</summary>
    [Fact]
    public void Settings_Diag_ChildrenOfThePage()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        using var scope = TestLocalization.Use(TestLocalization.English);
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            var dialog = new SettingsDialog(new AppSettings(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            Shots(dir, "diag-base", dialog, ("", d =>
            {
                GoTo((SettingsDialog)d, 0);
                var viewport = ((SettingsDialog)d).ControlHandle(SettingsDialog.ViewportId(0));
                var index = 0;
                for (var c = W.GetWindow(viewport, 5); c != 0; c = W.GetWindow(c, 2))
                {
                    var r = WindowRect(c);
                    Log(dir, $"diag child {index++}: class={W.ClassName(c)} style={(uint)User32.GetWindowLongPtr(c, WindowMessages.GwlStyle):X8} vis={User32.IsWindowVisible(c)} rect=({r.Left},{r.Top},{Width(r)}x{Height(r)}) text={TextOf(c)}");
                    if (index > 40)
                    {
                        break;
                    }
                }
            }), ("-noclipsib", d =>
            {
                var viewport = ((SettingsDialog)d).ControlHandle(SettingsDialog.ViewportId(0));
                for (var c = W.GetWindow(viewport, 5); c != 0; c = W.GetWindow(c, 2))
                {
                    var style = User32.GetWindowLongPtr(c, WindowMessages.GwlStyle);
                    User32.SetWindowLongPtr(c, WindowMessages.GwlStyle, style & ~0x04000000);
                }
            }));
            return 0;
        });
    }

    [Fact]
    public void Settings_EverySevenPages_TopMiddleBottom()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (code, loc) in Languages)
        {
            using var scope = TestLocalization.Use(loc);
            OnSta(() =>
            {
                using var styles = ActivateVisualStyles();
                // noclip = diagnostic variant: WS_CLIPSIBLINGS removed from the page controls again (what the page would look like
                // if the group boxes did not clip the controls above them); the default variant is the build as it is.
                foreach (var noclip in Booleans)
                {
                    foreach (var dpi in new uint[] { 96, 144 })
                    {
                        var suffixDpi = (dpi == 96 ? "" : "-150") + (noclip ? "-noclip" : "");
                        for (var page = 0; page < 7; page++)
                        {
                            var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
                            var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
                            dialog.Presenter.ShellIntegration = new FakeShell();
                            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
                            var p = page;
                            var clear = noclip;
                            Shots(dir, $"settings-p{p}-{code}{suffixDpi}", dialog,
                                ("-top", d =>
                                {
                                    Dpi(d, dpi);
                                    GoTo((SettingsDialog)d, p);
                                    if (clear)
                                    {
                                        var viewport = ((SettingsDialog)d).ControlHandle(SettingsDialog.ViewportId(p));
                                        for (var c = W.GetWindow(viewport, 5); c != 0; c = W.GetWindow(c, 2))
                                        {
                                            var style = User32.GetWindowLongPtr(c, WindowMessages.GwlStyle);
                                            User32.SetWindowLongPtr(c, WindowMessages.GwlStyle, style & ~0x04000000);
                                        }
                                    }

                                    Scroll(d, SbTop);
                                }),
                                ("-mid", d => Scroll(d, ScrollConstants.SbPageDown)),
                                ("-bottom", d => Scroll(d, SbBottom)),
                                ("-focus", d =>
                                {
                                    Scroll(d, SbTop);
                                    FocusFirstTabStop(d, SettingsDialog.ViewportId(((SettingsDialog)d).ActivePageIndex));
                                }));
                        }
                    }
                }

                return 0;
            });
        }
    }

    [Fact]
    public void Dialogs_ZoomDiagnosticsSkippedBatch()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (code, loc) in Languages)
        {
            using var scope = TestLocalization.Use(loc);
            OnSta(() =>
            {
                using var styles = ActivateVisualStyles();
                var display = DiagnosticsPresenter.Build(new ReviewMetrics().Snapshot());
                var skipped = Enumerable.Range(0, 30).Select(i => new SkippedEntry($@"C:\photos\2026\trip\IMG_{i:0000}.jpg", "The process cannot access the file.")).ToArray();
                var batch = Enumerable.Range(0, 30).Select(i => new BatchReviewItem($@"C:\photos\2026\trip\IMG_{i:0000}.jpg", 1_000_000 + (i * 12345))).ToArray();
                foreach (var dpi in new uint[] { 96, 144 })
                {
                    var s = dpi == 96 ? "" : "-150";
                    Shots(dir, $"zoom-{code}{s}", new ZoomPromptDialog(175), ("", d => Dpi(d, dpi)));
                    Shots(dir, $"zoom-error-{code}{s}", new ZoomPromptDialog(175), ("", d =>
                    {
                        Dpi(d, dpi);
                        SetText(d, ZoomPromptDialog.EditId, "9999");
                        Command(d, ZoomPromptDialog.ApplyId);
                    }));
                    Shots(dir, $"diagnostics-{code}{s}", new DiagnosticsDialog(display), ("", d => Dpi(d, dpi)));
                    Shots(dir, $"skipped-{code}{s}", new ListDialog(ListDialogContent.ForSkippedFiles(skipped)), ("", d => Dpi(d, dpi)));
                    Shots(dir, $"batch-{code}{s}", new ListDialog(ListDialogContent.ForBatchReview(batch)), ("", d => Dpi(d, dpi)));
                }

                return 0;
            });
        }
    }

    [Fact]
    public async Task Dialogs_RecoveryActionProfilesBenchmark()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (code, loc) in Languages)
        {
            using var scope = TestLocalization.Use(loc);
            await using var ui = await UiThread.StartAsync();
            await ui.InvokeAsync(() => _ = ActivateVisualStyles());
            var actions = new List<ReviewAction>
            {
                new() { Name = "Keep", Shortcut = "F6", Operation = FileOperationType.Move, Destination = "Keep", Confirm = false },
                new() { Name = "Copy to the review folder", Shortcut = "F7", Operation = FileOperationType.Copy, Destination = "Review", Confirm = true },
                new() { Name = "Trash", Shortcut = "F8", Operation = FileOperationType.Recycle, Destination = "x", Confirm = false },
            };
            await ui.InvokeAsync(() =>
            {
                foreach (var dpi in new uint[] { 96, 144 })
                {
                    var s = dpi == 96 ? "" : "-150";
                    Shots(dir, $"actionprofiles-{code}{s}", new ActionProfilesDialog(actions), ("", d =>
                    {
                        Dpi(d, dpi);
                        ListBoxControl.SelectSingle(Item(d, ActionProfilesDialog.ListId), 1);
                        Command(d, ActionProfilesDialog.ListId, ControlConstants.LbnSelChange);
                    }));
                }
            });

            var owner = await ui.InvokeAsync(() => CreateOwner());
            BenchmarkDialog? bench = null;
            await ui.InvokeAsync(() =>
            {
                bench = new BenchmarkDialog(@"C:\Photos\trip", ui.Dispatcher, (profile, _) => new Session(profile),
                    (_, _, _) => null, _ => { }, (_, _, _) => ["a.jpg"], _ => Task.FromResult(@"C:\Temp\summary.json"));
                bench.ShowModeless(owner, ui.Loop);
                var list = Item(bench, BenchmarkDialog.ProfilesId);
                ListBoxControl.SelectAll(list, false);
                for (var i = 0; i < bench.Presenter.ProfileItems.Count; i++)
                {
                    if (bench.Presenter.ProfileItems[i].Profile.Id is "fast-sequential" or "preview-light" or "original-correctness" or "fast-balanced")
                    {
                        ListBoxControl.SetSelected(list, i, true);
                    }
                }

                Drain(bench);
                Capture(dir, $"benchmark-{code}-before-run", bench.Hwnd);
            });
            await ui.RunAsync(() => bench!.Presenter.RunSelectedAsync(
                bench.Presenter.ProfileItems.Where(i => i.Profile.Id is "fast-sequential" or "preview-light" or "original-correctness" or "fast-balanced")
                    .Select(i => i.Profile).ToArray(), @"C:\Photos\trip", "64"));
            await ui.InvokeAsync(() =>
            {
                ListViewControl.SelectRow(Item(bench!, BenchmarkDialog.ResultsId), 1);
                Drain(bench!);
                Capture(dir, $"benchmark-{code}", bench!.Hwnd);
                NativeDialogSnapshotTests.ChangeDpi(bench, 144);
                Drain(bench);
                Capture(dir, $"benchmark-{code}-150", bench.Hwnd);
                bench.Close();
                DestroyOwner(owner);
            });

            // Recovery (modeless on the real shell loop; temp folder + journal of its own, no Recycle Bin).
            await using var rig = await RecoveryRig.StartAsync();
            var retry = rig.Write(@"src\retry-me.jpg", "aaaa");
            var done = rig.Write(@"dst\already.jpg", "bbbbbb");
            var entries = new List<JournalEntry>
            {
                rig.Entry("e1", retry, Path.Combine(rig.Root, "dst", "retry-me.jpg")),
                rig.Entry("e2", Path.Combine(rig.Root, "src", "already.jpg"), done),
                rig.Entry("e3", Path.Combine(rig.Root, "src", "lost.jpg"), Path.Combine(rig.Root, "dst", "lost.jpg"), size: 5),
                rig.Entry("e4", Path.Combine(rig.Root, "src", "a-very-long-file-name-that-should-be-trimmed-with-an-ellipsis-IMG_20261013_000123456789.jpg"),
                    Path.Combine(rig.Root, "dst", "x.jpg"), size: 7),
            };
            var d = await rig.OpenAsync(entries, retry: null, dismiss: e => new DismissOutcome(e, []));
            await rig.CheckDoneAsync(d);
            await rig.OnUi(() =>
            {
                Drain(d);
                Capture(dir, $"recovery-{code}", d.Hwnd);
                ListBoxControl.SelectAll(Item(d, RecoveryDialog.ListId), false);
                ListBoxControl.SetSelected(Item(d, RecoveryDialog.ListId), 0, true);
                Command(d, RecoveryDialog.ListId, ControlConstants.LbnSelChange);
                Drain(d);
                Capture(dir, $"recovery-{code}-selected", d.Hwnd);
                NativeDialogSnapshotTests.ChangeDpi(d, 144);
                Drain(d);
                Capture(dir, $"recovery-{code}-150", d.Hwnd);
            });
        }
    }

    private sealed class Session(BenchmarkProfile profile) : IBenchmarkProfileSession
    {
        public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken)
        {
            var failed = profile.Id == "original-correctness";
            double[] samples = failed ? [] : profile.Id == "preview-light" ? [9, 10, 11, 13] : [12, 15, 18, 22, 31];
            var workload = failed ? BenchmarkWorkload.Correctness : BenchmarkWorkload.Sequential;
            var status = failed ? BenchmarkResultStatus.Fail : BenchmarkResultStatus.Pass;
            return Task.FromResult(new BenchmarkReport("snap-" + profile.Id, DateTimeOffset.UnixEpoch, folder, [new BenchmarkPhaseResult(profile.Id, workload, samples, status)]));
        }

        public ValueTask DisposeAsync() => default;
    }

    // ---- menus -------------------------------------------------------------------------------------------------------------------

    private static MenuItemModel Cmd(string id, string text, string? shortcut = null, bool enabled = true, bool check = false) =>
        new(id, MenuItemKind.Command, text, shortcut, enabled, check, [], null);

    [Fact]
    public void Menus_ContextAndTools_DarkStates()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        const nint VkDown = 0x28;
        const nint VkRight = 0x27;
        foreach (var (code, loc) in Languages)
        {
            using var scope = TestLocalization.Use(loc);
            var context = ContextMenuModelBuilder.Build(new ContextMenuBuildContext(new AppSettings { HiddenContextMenuItems = [] }, true, true, false, 150.0, true, true, true));
            var zoomIndex = 0;
            foreach (var item in context)
            {
                if (item.Kind == MenuItemKind.Submenu)
                {
                    break;
                }

                if (item.Kind != MenuItemKind.Separator)
                {
                    zoomIndex++;
                }
            }

            var tools = ContextMenuModelBuilder.BuildToolsMenu(false);
            var synthetic = new List<MenuItemModel>
            {
                Cmd("a", "Normal item", "Ctrl+N"),
                Cmd("b", "Checked item", "F5", check: true),
                Cmd("c", "Disabled item", "Del", enabled: false),
                new("sep", MenuItemKind.Separator, "", null, false, false, [], null),
                Cmd("d", "A very long menu item text that must not overlap its shortcut text", "Ctrl+Shift+Alt+L"),
                new("sub", MenuItemKind.Submenu, "Submenu", null, true, false, [Cmd("s1", "Child one"), Cmd("s2", "Child two", enabled: false)], null),
                Cmd("e", "Tiếng Việt có dấu: Đường dẫn ảnh", "Ctrl+Đ"),
            };
            foreach (var dpi in new uint[] { 96, 144 })
            {
                var s = dpi == 96 ? "" : "-150";
                ShowMenu(dir, $"menu-context-{code}{s}", context, dpi,
                    ("-open", []), ("-hover3", [VkDown, VkDown, VkDown]));
                ShowMenu(dir, $"menu-context-sub-{code}{s}", context, dpi,
                    ("", Enumerable.Repeat(VkDown, zoomIndex + 1).Append(VkRight).Append(VkDown).ToArray()));
                ShowMenu(dir, $"menu-tools-{code}{s}", tools, dpi, ("-open", []), ("-hover2", [VkDown, VkDown]));
            }

            ShowMenu(dir, $"menu-synthetic-{code}", synthetic, 96, ("-open", []), ("-hover-disabled", [VkDown, VkDown, VkDown]));
            ShowMenu(dir, $"menu-synthetic-sub-{code}", synthetic, 96, ("", [VkDown, VkDown, VkDown, VkDown, VkDown, VkRight, VkDown, VkDown]));
        }
    }

    private static void ShowMenu(string dir, string name, IReadOnlyList<MenuItemModel> items, uint dpi,
        params (string Suffix, nint[] Keys)[] steps)
    {
        const nint VkEscape = 0x1B;
        OnSta(() =>
        {
            var owner = CreateOwner();
            try
            {
                var worker = Task.Run(() =>
                {
                    var deadline = Environment.TickCount64 + 15000;
                    while (Environment.TickCount64 < deadline && MenuWindows().Count == 0)
                    {
                        Thread.Yield();
                    }

                    SpinFor(600); // first paint of the menu window (an unpainted window shows the shared screen surface)
                    foreach (var (suffix, keys) in steps)
                    {
                        foreach (var key in keys)
                        {
                            _ = User32.PostMessage(owner, WindowMessages.WmKeyDown, key, 0);
                        }

                        SpinFor(400);
                        var menus = MenuWindows();
                        for (var i = 0; i < menus.Count; i++)
                        {
                            Capture(dir, name + suffix + (i == 0 ? "" : "-level" + i), menus[i]);
                        }

                        if (menus.Count == 0)
                        {
                            Log(dir, name + suffix + ": NO MENU WINDOW");
                        }
                    }

                    for (var i = 0; i < 4; i++)
                    {
                        _ = User32.PostMessage(owner, WindowMessages.WmKeyDown, VkEscape, 0);
                        SpinFor(100);
                    }
                });

                using var session = new OwnerDrawMenuSession(owner, dpi);
                var attached = session.Attach();
                var root = User32.CreatePopupMenu();
                var ids = new List<string>();
                var populate = typeof(NativePopupMenuHost).GetMethod("Populate", BindingFlags.NonPublic | BindingFlags.Static)!;
                populate.Invoke(null, [root, items, ids, null, attached ? session : null]);
                _ = User32.TrackPopupMenuEx(root, WindowMessages.TpmReturnCmd | WindowMessages.TpmRightButton, 150, 150, owner, 0);
                User32.DestroyMenu(root);
                worker.Wait(TimeSpan.FromSeconds(20));
            }
            finally
            {
                DestroyOwner(owner);
            }

            return 0;
        });
    }

    private static void SpinFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Thread.Yield();
        }
    }

    private static List<nint> MenuWindows()
    {
        var found = new List<nint>();
        W.EnumWindows((hwnd, _) =>
        {
            var name = W.ClassName(hwnd);
            if (name == "#32768" && User32.IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        found.Sort((a, b) => WindowRect(a).Left.CompareTo(WindowRect(b).Left));
        return found;
    }

    // ---- HUD + emoji (offscreen, the real ShellOverlay) --------------------------------------------------------------------------

    [Fact]
    public void Hud_And_ColourEmoji()
    {
        var dir = Dir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (code, loc) in Languages)
        {
            using var scope = TestLocalization.Use(loc);
            using var text = new DWriteTextRenderer(code == "vi" ? "vi-VN" : "en-US", 512);
            var seen = new HashSet<double>();
            foreach (var c in PixelGoldenCatalog.All.Where(c => c.Hud && c.IsFit))
            {
                if (!seen.Add(c.Dpi))
                {
                    continue;
                }

                var withHud = PixelGoldenShellRenderer.Render(c, hudText: text);
                var without = PixelGoldenShellRenderer.Render(PixelGoldenCatalog.WithoutHud(c));
                WritePng(dir, $"hud-{code}-dpi{c.Dpi * 100:0}", withHud.Frame, $"case={c.Name} chromaPixels={Chroma(withHud.Frame)} (no-HUD {Chroma(without.Frame)})");
            }

            // Emoji at a large size on a dark panel: colour glyphs have saturated pixels, monochrome ones are grey.
            using var surface = D2DRenderSurface.CreateOffscreenSurface(720, 140, 1.0);
            using var pixels = RenderTestImages.Render(surface, dc =>
            {
                dc.Clear(new ColorF(0.12f, 0.12f, 0.12f, 1f));
                using var layout = text.CreateLayout("📂 📁 ⚙ ⋮ ★ 🔍 ✔ ❤", new TextStyle("Segoe UI", 48), 700, TextTrimming.CharacterEllipsis);
                dc.DrawText(layout, new PointD(10, 10), new ColorF(0.9f, 0.9f, 0.9f, 1f));
            });
            var frame = PixelGoldenShellRenderer.ToFrame(pixels);
            WritePng(dir, $"emoji-{code}", frame, $"chromaPixels={Chroma(frame)}");
        }
    }

    private static int Chroma(PixelFrame f)
    {
        var count = 0;
        for (var i = 0; i < f.Bgra.Length; i += 4)
        {
            var b = f.Bgra[i];
            var g = f.Bgra[i + 1];
            var r = f.Bgra[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60)
            {
                count++;
            }
        }

        return count;
    }

    private static unsafe class W
    {
        public delegate bool EnumProc(nint hwnd, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint GetWindowDC(nint hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(nint hwnd, nint dc);

        [DllImport("user32.dll")]
        public static extern nint GetWindow(nint hwnd, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumProc proc, nint lParam);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
        private static extern int GetClassName(nint hwnd, char* name, int max);

        public static string ClassName(nint hwnd)
        {
            var buffer = stackalloc char[64];
            var length = GetClassName(hwnd, buffer, 64);
            return new string(buffer, 0, length);
        }

        [DllImport("gdi32.dll")]
        public static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        public static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);

        [DllImport("gdi32.dll")]
        public static extern nint SelectObject(nint hdc, nint obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(nint obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);
    }
}
