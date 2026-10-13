using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ4: dòng của danh sách Recovery được VẼ đúng (owner-draw qua WM_DRAWITEM thật vào DC bộ nhớ): tiêu đề nửa trên, dòng phụ
/// nửa dưới, huy hiệu bên phải viền đúng màu tone; hàng chọn dùng màu chọn.
/// </summary>
[Trait("Category", "UI")]
public sealed unsafe class RecoveryRowDrawingTests
{
    private const int Width = 420;
    private const int Height = 44;

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    /// <summary>Vẽ dòng 0 của danh sách (một mục CanRetry thật trong thư mục tạm) và trả toàn bộ điểm ảnh [y][x].</summary>
    private static uint[][] DrawFirstRow(uint state, RecoveryVerdict expectedVerdict)
    {
        var root = Path.Combine(Path.GetTempPath(), "wp35g4-recovery-draw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            File.WriteAllText(source, "bytes");
            var info = new FileInfo(source);
            var entry = new JournalEntry("draw", FileOperationType.Move, JournalState.Failed, source, Path.Combine(root, "out", "photo.jpg"),
                info.Length, info.LastWriteTimeUtc, RecoveryRig.Stamp, "disk error");
            return OnSta(() =>
            {
                uint[][] pixels = [];
                var dialog = new RecoveryDialog([entry], seams: new RecoveryDialogSeams(new FakeMessages(), AutoCheck: false));
                ShowOnOwner(dialog, (d, _) =>
                {
                    var rd = (RecoveryDialog)d;
                    var result = new RecoveryFileCheck(new PhysicalFileSystem()).Check(entry);
                    Assert.Equal(expectedVerdict, result.Verdict);
                    rd.Presenter.Rows[0].SetResult(result);
                    var list = Item(d, RecoveryDialog.ListId);
                    var screen = User32.GetDC(0);
                    var memory = CreateCompatibleDC(screen);
                    var bitmap = CreateCompatibleBitmap(screen, Width, Height);
                    var old = Gdi32.SelectObject(memory, bitmap);
                    try
                    {
                        var item = new byte[ControlConstants.DrawItemSize];
                        fixed (byte* p = item)
                        {
                            *(int*)(p + 4) = RecoveryDialog.ListId;
                            *(int*)(p + 8) = 0;
                            *(uint*)(p + 16) = state;
                            *(nint*)(p + 24) = list;
                            *(nint*)(p + 32) = memory;
                            *(Rect*)(p + 40) = new Rect { Left = 0, Top = 0, Right = Width, Bottom = Height };
                            User32.SendMessage(d.Hwnd, ControlConstants.WmDrawItem, RecoveryDialog.ListId, (nint)p);
                        }

                        pixels = new uint[Height][];
                        for (var y = 0; y < Height; y++)
                        {
                            pixels[y] = new uint[Width];
                            for (var x = 0; x < Width; x++)
                            {
                                pixels[y][x] = GetPixel(memory, x, y);
                            }
                        }
                    }
                    finally
                    {
                        Gdi32.SelectObject(memory, old);
                        Gdi32.DeleteObject(bitmap);
                        DeleteDC(memory);
                        _ = User32.ReleaseDC(0, screen);
                    }

                    d.Close();
                });
                return pixels;
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int CountNot(uint[][] pixels, int top, int bottom, int left, int right, uint background)
    {
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (pixels[y][x] != background)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int Count(uint[][] pixels, uint color)
    {
        var count = 0;
        foreach (var row in pixels)
        {
            count += row.Count(pixel => pixel == color);
        }

        return count;
    }

    [Fact]
    public void ARow_ShowsTheTitleAbove_TheSubtitleBelow_AndABadgeInTheToneColour()
    {
        var pixels = DrawFirstRow(0, RecoveryVerdict.CanRetry);
        var background = DialogColors.Input;

        var title = CountNot(pixels, 0, Height / 2, 4, 200, background);
        var subtitle = CountNot(pixels, Height / 2, Height, 4, 200, background);
        var badge = Count(pixels, RecoveryDialog.ToColorRef(RecoveryTone.Good));

        Assert.True(title > 20, $"the title (upper half) must be drawn, {title} pixels");
        Assert.True(subtitle > 20, $"the subtitle (lower half) must be drawn, {subtitle} pixels");
        Assert.True(badge > 20, $"the badge border/text must use the tone colour, {badge} pixels");
    }

    [Fact]
    public void ASelectedRow_UsesTheSelectionColour()
    {
        var pixels = DrawFirstRow(ControlConstants.OdsSelected, RecoveryVerdict.CanRetry);

        Assert.Equal(DialogColors.FromKey("Dark.Selection"), pixels[2][Width / 2 + 60]);
    }

    [Fact]
    public void TheBadgePill_IsFilledWithThePanelColour_NotTheInputColour()
    {
        var pixels = DrawFirstRow(0, RecoveryVerdict.CanRetry);

        // The pill sits at the right edge, vertically centred: inside its border the fill is the Panel colour.
        Assert.Contains(DialogColors.FromKey("Dark.Panel"), pixels[Height / 2]);
    }
}
