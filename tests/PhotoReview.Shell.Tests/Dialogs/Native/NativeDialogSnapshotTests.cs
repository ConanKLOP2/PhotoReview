using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1 (thủ công): chụp từng hộp thoại Win32 gốc ra PNG bằng <c>PrintWindow</c> để NHÌN dark/bố cục (tiếng Anh + tiếng Việt, ở các
/// DPI cho trước) - đây là chỗ kiểm bằng mắt mà test tự động không làm được. Chạy:
/// <c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;thư mục&gt; dotnet test --filter FullyQualifiedName~NativeDialogSnapshotTests</c>. Không có biến môi
/// trường thì test bỏ qua (không ghi gì).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed unsafe class NativeDialogSnapshotTests
{
    private static string? OutputDirectory => Environment.GetEnvironmentVariable("PHOTOREVIEW_DIALOG_SNAPSHOTS");

    [Fact]
    public void WriteSnapshots_OfEveryDialog_InEnglishAndVietnamese()
    {
        var directory = OutputDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        foreach (var (code, localizer) in new[] { ("en", TestLocalization.English), ("vi", TestLocalization.Vietnamese) })
        {
            using var scope = TestLocalization.Use(localizer);
            OnSta(() =>
            {
                using var styles = ActivateVisualStyles();
                var display = DiagnosticsPresenter.Build(new ReviewMetrics().Snapshot());
                var skipped = Enumerable.Range(0, 12).Select(i => new SkippedEntry($@"C:\photos\2026\trip\IMG_{i:0000}.jpg", "The process cannot access the file.")).ToArray();
                var batch = Enumerable.Range(0, 12).Select(i => new BatchReviewItem($@"C:\photos\2026\trip\IMG_{i:0000}.jpg", 1_000_000 + i * 12345)).ToArray();
                Snap(directory, $"zoom-{code}", new ZoomPromptDialog(175));
                Snap(directory, $"zoom-error-{code}", new ZoomPromptDialog(175), d =>
                {
                    SetText(d, ZoomPromptDialog.EditId, "9999");
                    Command(d, ZoomPromptDialog.ApplyId);
                });
                Snap(directory, $"diagnostics-{code}", new DiagnosticsDialog(display));
                Snap(directory, $"skipped-{code}", new ListDialog(ListDialogContent.ForSkippedFiles(skipped)));
                Snap(directory, $"batch-{code}", new ListDialog(ListDialogContent.ForBatchReview(batch)));
                // 150 %: the layout after a (simulated) move to a 144-dpi monitor (WM_DPICHANGED with the system's suggested rectangle).
                Snap(directory, $"zoom-error-{code}-150", new ZoomPromptDialog(175), d =>
                {
                    SetText(d, ZoomPromptDialog.EditId, "9999");
                    Command(d, ZoomPromptDialog.ApplyId);
                    ChangeDpi(d, 144);
                });
                Snap(directory, $"batch-{code}-150", new ListDialog(ListDialogContent.ForBatchReview(batch)), d => ChangeDpi(d, 144));
                Snap(directory, $"diagnostics-{code}-150", new DiagnosticsDialog(display), d => ChangeDpi(d, 144));
                return 0;
            });
        }
    }

    internal static void ChangeDpi(NativeDialog dialog, uint dpi)
    {
        var current = WindowRect(dialog.Hwnd);
        var factor = dpi / (double)dialog.Dpi;
        var suggested = new Rect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Left + (int)(Width(current) * factor),
            Bottom = current.Top + (int)(Height(current) * factor),
        };
        User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
    }

    internal static void Snap(string directory, string name, NativeDialog dialog, Action<NativeDialog>? beforeCapture = null)
    {
        ShowOnOwner(dialog, (d, _) =>
        {
            beforeCapture?.Invoke(d);
            Drain(d); // let the first WM_PAINT/WM_ERASEBKGND of the controls run
            CaptureWindow(Path.Combine(directory, name + ".png"), d.Hwnd);
            d.Close();
        });
    }

    /// <summary>PrintWindow of one top-level or popup window to a PNG (the combo drop-down list is a window of its own).</summary>
    internal static void CaptureWindow(string path, nint hwnd)
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

            File.WriteAllBytes(path, EncodePng(pixels, width, height));
        }
        finally
        {
            Gdi32.DeleteObject(bitmap);
            DeleteDC(memory);
            _ = User32.ReleaseDC(0, screen);
        }
    }

    /// <summary>WP-35 GĐ2: the new controls (combo + its drop-down list, multi-select badge list, report listview, progress, trackbar, ...).</summary>
    [Fact]
    public void WriteSnapshots_OfTheControlGallery()
    {
        var directory = OutputDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var scope = TestLocalization.Use(TestLocalization.English);
        OnSta(() =>
        {
            using var styles = ActivateVisualStyles();
            Snap(directory, "gallery", new ControlGalleryDialog());
            Snap(directory, "gallery-150", new ControlGalleryDialog(), d => ChangeDpi(d, 144));
            Snap(directory, "gallery-sorted", new ControlGalleryDialog(), d =>
            {
                ((ControlGalleryDialog)d).SortBy(1);
                ((ControlGalleryDialog)d).SortBy(1);
            });
            ShowOnOwner(new ControlGalleryDialog(), (d, _) =>
            {
                var combo = Item(d, ControlGalleryDialog.Combo);
                ComboBoxControl.Drop(combo, true);
                Drain(d);
                var list = ComboBoxControl.ListWindow(combo);
                CaptureWindow(Path.Combine(directory, "gallery-combo-list.png"), list);
                ComboBoxControl.Drop(combo, false);
                d.Close();
            });
            return 0;
        });
    }

    internal static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
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
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        stream.Write(body);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(body)));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
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
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);
}

/// <summary>WP-35 GĐ2 (thủ công): ảnh chụp Action profiles (modal) và Benchmark (modeless, có kết quả); tách lớp vì cần <c>await</c>.</summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class NativeDialogSnapshotWindowsTests
{
    private static string? OutputDirectory => Environment.GetEnvironmentVariable("PHOTOREVIEW_DIALOG_SNAPSHOTS");

    /// <summary>WP-35 GĐ2: Action profiles (modal) and the Benchmark window (modeless) with results, en + vi, 100 % and 150 %.</summary>
    [Fact]
    public async Task WriteSnapshots_OfActionProfilesAndBenchmark()
    {
        var directory = OutputDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        foreach (var (code, localizer) in new[] { ("en", TestLocalization.English), ("vi", TestLocalization.Vietnamese) })
        {
            using var scope = TestLocalization.Use(localizer);
            await using var ui = await UiThread.StartAsync();
            // comctl32 v6 stays active for the thread's lifetime (the thread ends with the iteration; no out-of-order deactivation).
            await ui.InvokeAsync(() => _ = ActivateVisualStyles());
            var actions = new List<ReviewAction>
            {
                new() { Name = "Keep", Shortcut = "F6", Operation = FileOperationType.Move, Destination = "Keep", Confirm = false },
                new() { Name = "Copy to the review folder", Shortcut = "F7", Operation = FileOperationType.Copy, Destination = "Review", Confirm = true },
                new() { Name = "Trash", Shortcut = "F8", Operation = FileOperationType.Recycle, Destination = "x", Confirm = false },
            };
            await ui.InvokeAsync(() =>
            {
                NativeDialogSnapshotTests.Snap(directory, $"actionprofiles-{code}", new ActionProfilesDialog(actions), d => Select(d, 1));
                NativeDialogSnapshotTests.Snap(directory, $"actionprofiles-{code}-150", new ActionProfilesDialog(actions), d =>
                {
                    Select(d, 1);
                    NativeDialogSnapshotTests.ChangeDpi(d, 144);
                });
            });

            var owner = await ui.InvokeAsync(() => CreateOwner());
            BenchmarkDialog? dialog = null;
            await ui.InvokeAsync(() =>
            {
                dialog = new BenchmarkDialog(@"C:\Photos\trip", ui.Dispatcher,
                    (profile, _) => new SnapshotSession(profile),
                    (_, _, _) => null, _ => { }, (_, _, _) => ["a.jpg"], _ => Task.FromResult(@"C:\Temp\summary.json"));
                dialog.ShowModeless(owner, ui.Loop);
                SelectProfiles(dialog, "fast-sequential", "preview-light", "original-correctness", "fast-balanced");
            });
            await ui.RunAsync(() => dialog!.Presenter.RunSelectedAsync(
                dialog.Presenter.ProfileItems.Where(i => i.Profile.Id is "fast-sequential" or "preview-light" or "original-correctness" or "fast-balanced")
                    .Select(i => i.Profile).ToArray(), @"C:\Photos\trip", "64"));
            await ui.InvokeAsync(() =>
            {
                ListViewControl.SelectRow(Item(dialog!, BenchmarkDialog.ResultsId), 1);
                Drain(dialog!);
                NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, $"benchmark-{code}.png"), dialog!.Hwnd);
                dialog.SortBy(6);
                Drain(dialog);
                NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, $"benchmark-{code}-sorted.png"), dialog.Hwnd);
                NativeDialogSnapshotTests.ChangeDpi(dialog, 144);
                Drain(dialog);
                NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, $"benchmark-{code}-150.png"), dialog.Hwnd);
                dialog.Close();
                DestroyOwner(owner);
            });
        }
    }

    private static void SelectProfiles(BenchmarkDialog dialog, params string[] ids)
    {
        var list = Item(dialog, BenchmarkDialog.ProfilesId);
        ListBoxControl.SelectAll(list, false);
        for (var i = 0; i < dialog.Presenter.ProfileItems.Count; i++)
        {
            if (ids.Contains(dialog.Presenter.ProfileItems[i].Profile.Id))
            {
                ListBoxControl.SetSelected(list, i, true);
            }
        }
    }

    private static void Select(NativeDialog dialog, int index)
    {
        ListBoxControl.SelectSingle(Item(dialog, ActionProfilesDialog.ListId), index);
        Command(dialog, ActionProfilesDialog.ListId, ControlConstants.LbnSelChange);
    }

    private sealed class SnapshotSession(BenchmarkProfile profile) : IBenchmarkProfileSession
    {
        public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken)
        {
            var failed = profile.Id == "original-correctness";
            double[] samples = failed ? [] : profile.Id == "preview-light" ? [9, 10, 11, 13] : [12, 15, 18, 22, 31];
            var workload = failed ? BenchmarkWorkload.Correctness : BenchmarkWorkload.Sequential;
            var status = failed ? BenchmarkResultStatus.Fail : BenchmarkResultStatus.Pass;
            return Task.FromResult(new BenchmarkReport("snap-" + profile.Id, DateTimeOffset.UnixEpoch, folder,
                [new BenchmarkPhaseResult(profile.Id, workload, samples, status)]));
        }

        public ValueTask DisposeAsync() => default;
    }
}
