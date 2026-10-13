using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
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
[Collection("OverlayLocalization")]
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

    private static void ChangeDpi(NativeDialog dialog, uint dpi)
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

    private static void Snap(string directory, string name, NativeDialog dialog, Action<NativeDialog>? beforeCapture = null)
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
    private static void CaptureWindow(string path, nint hwnd)
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

    private static byte[] EncodePng(byte[] bgra, int width, int height)
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
