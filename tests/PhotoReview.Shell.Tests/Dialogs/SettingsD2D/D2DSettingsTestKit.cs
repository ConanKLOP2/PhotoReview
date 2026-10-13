using System.IO.Compression;
using System.Runtime.InteropServices;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>Kẹp giấy giả cho test (không đụng clipboard của người dùng).</summary>
internal sealed class FakeD2DClipboard : ID2DClipboard
{
    public string? Text { get; set; }

    public string? GetText() => Text;

    public void SetText(string text) => Text = text;
}

internal static partial class D2DTestNative
{
    [LibraryImport("user32.dll", EntryPoint = "PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);
}

/// <summary>Ảnh BGRA chụp được (so pixel).</summary>
internal sealed class D2DShot(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Bgra { get; } = bgra;

    /// <summary>Số pixel khác nhau (so BGR, bỏ alpha) trong vùng chung; kích thước khác nhau = tất cả.</summary>
    public int DiffCount(D2DShot other, int maxX = int.MaxValue, int minY = 0, int maxY = int.MaxValue)
    {
        if (Width != other.Width || Height != other.Height)
        {
            return int.MaxValue;
        }

        var count = 0;
        var xEnd = Math.Min(Width, maxX);
        var yEnd = Math.Min(Height, maxY);
        for (var y = minY; y < yEnd; y++)
        {
            for (var x = 0; x < xEnd; x++)
            {
                var o = ((y * Width) + x) * 4;
                if (Bgra[o] != other.Bgra[o] || Bgra[o + 1] != other.Bgra[o + 1] || Bgra[o + 2] != other.Bgra[o + 2])
                {
                    count++;
                }
            }
        }

        return count;
    }

    public int DistinctColors()
    {
        var set = new HashSet<uint>();
        for (var o = 0; o + 3 < Bgra.Length && set.Count < 64; o += 4 * 5)
        {
            set.Add(BitConverter.ToUInt32(Bgra, o) & 0x00FFFFFF);
        }

        return set.Count;
    }
}

/// <summary>
/// Host KHÔNG cửa sổ: <see cref="D2DSettingsView"/> + bề mặt offscreen (GPU thật hoặc WARP). Dùng cho test điểm ảnh/hiệu năng/logic
/// vì đọc lại được từng pixel (<c>TryReadPixels</c>); cửa sổ thật chỉ có swap chain nên không đọc được.
/// </summary>
internal sealed class D2DOffscreenHost : IDisposable
{
    private readonly DWriteTextRenderer _text = new();
    private readonly D2DRenderSurface _surface;

    public D2DOffscreenHost(int width, int height, double scale, bool dark, bool hardware, AppSettings? settings = null,
        D2DEditMode mode = D2DEditMode.SelfDrawn, FakeD2DClipboard? clipboard = null)
    {
        Clipboard = clipboard ?? new FakeD2DClipboard();
        _surface = D2DRenderSurface.CreateOffscreenSurface(width, height, 1.0, hardware);
        View = new D2DSettingsView(_text, Clipboard, dark ? D2DSettingsPalette.Dark : D2DSettingsPalette.Classic, mode, () => Tr.SettingsNavDisplay);
        Page = new D2DDisplayPage(View);
        var presenter = new SettingsPresenter(settings ?? new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 });
        State = presenter.LoadFields();
        Page.Load(State);
        View.Layout(width, height, scale);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public D2DSettingsView View { get; }

    public D2DDisplayPage Page { get; }

    public SettingsFormState State { get; }

    public FakeD2DClipboard Clipboard { get; }

    public bool IsWarp => _surface.IsWarp;

    public string Adapter => _surface.AdapterDescription;

    public D2DRenderSurface Surface => _surface;

    /// <summary>Vẽ một khung vào bề mặt (giữ lịch sử) và trả ảnh.</summary>
    public D2DShot Render(bool read = true)
    {
        var ctx = _surface.BeginDrawContext();
        View.Render(ctx);
        D2DShot? shot = null;
        if (read && ctx.TryReadPixels(out var pixels))
        {
            using (pixels)
            {
                var data = new byte[Width * Height * 4];
                for (var y = 0; y < Height; y++)
                {
                    pixels.GetRow(y).Slice(0, Width * 4).CopyTo(data.AsSpan(y * Width * 4));
                }

                shot = new D2DShot(Width, Height, data);
            }
        }

        _surface.EndDrawAndPresent();
        return shot ?? new D2DShot(Width, Height, []);
    }

    public void Dispose()
    {
        _surface.Dispose();
        _text.Dispose();
    }
}

internal static unsafe class D2DTestKit
{
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkPrior = 0x21;
    public const int VkNext = 0x22;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;

    /// <summary>Mở cửa sổ D2D thật trên thread STA, nạp trang Display, chạy <paramref name="body"/>, luôn đóng cửa sổ.</summary>
    public static T WithWindow<T>(D2DSettingsWindowOptions options, Func<D2DSettingsWindow, D2DDisplayPage, T> body, AppSettings? settings = null)
        => OnSta(() =>
        {
            var presenter = new SettingsPresenter(settings ?? new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 });
            var state = presenter.LoadFields();
            using var window = new D2DSettingsWindow(options, () => Tr.SettingsNavDisplay, v => new D2DDisplayPage(v));
            var page = (D2DDisplayPage)window.Page;
            page.Load(state);
            window.View.Layout(window.View.ClientWidth, window.View.ClientHeight, window.Scale);
            window.PushValuesToHosted();
            window.SyncHosted();
            try
            {
                return body(window, page);
            }
            finally
            {
                window.Dispose();
            }
        });

    public static void Pump()
    {
        Msg message;
        while (User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmRemove))
        {
            if (message.Message == WindowMessages.WmQuit)
            {
                return;
            }

            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }
    }

    public static nint MakeLParam(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    public static void Click(nint hwnd, int x, int y)
    {
        var lp = MakeLParam(x, y);
        User32.SendMessage(hwnd, WindowMessages.WmLButtonDown, 1, lp);
        User32.SendMessage(hwnd, WindowMessages.WmLButtonUp, 0, lp);
    }

    public static void Key(nint hwnd, int vk) => User32.SendMessage(hwnd, WindowMessages.WmKeyDown, vk, 1);

    public static void Wheel(nint hwnd, int notches) => User32.SendMessage(hwnd, WindowMessages.WmMouseWheel, unchecked((nint)((long)(notches * 120) << 16)), 0);

    /// <summary>Chụp bằng PrintWindow(PW_RENDERFULLCONTENT) - đọc được cả nội dung swap chain nếu DWM hỗ trợ trên desktop này.</summary>
    public static D2DShot? PrintWindowShot(nint hwnd, int width, int height)
    {
        var window = User32.GetDC(0);
        var dc = D2DGdi.CreateCompatibleDC(window);
        var bitmap = D2DGdi.CreateCompatibleBitmap(window, width, height);
        var old = Gdi32.SelectObject(dc, bitmap);
        try
        {
            if (!D2DTestNative.PrintWindow(hwnd, dc, 2))
            {
                return null;
            }

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
                Gdi32.SelectObject(dc, old);
                _ = D2DGdi.GetDIBits(dc, bitmap, 0, (uint)height, p, i, 0);
            }

            return new D2DShot(width, height, pixels);
        }
        finally
        {
            Gdi32.DeleteObject(bitmap);
            D2DGdi.DeleteDC(dc);
            _ = User32.ReleaseDC(0, window);
        }
    }

    /// <summary>PNG BGRA -> file (ảnh lỗi).</summary>
    public static byte[] EncodePng(D2DShot shot)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBe(header, 0, shot.Width);
        WriteBe(header, 4, shot.Height);
        header[8] = 8;
        header[9] = 2;
        Chunk(stream, "IHDR", header);
        var raw = new byte[(shot.Width * 3 + 1) * shot.Height];
        for (var y = 0; y < shot.Height; y++)
        {
            var row = y * (shot.Width * 3 + 1);
            for (var x = 0; x < shot.Width; x++)
            {
                var s = ((y * shot.Width) + x) * 4;
                raw[row + 1 + (x * 3)] = shot.Bgra[s + 2];
                raw[row + 2 + (x * 3)] = shot.Bgra[s + 1];
                raw[row + 3 + (x * 3)] = shot.Bgra[s];
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            z.Write(raw);
        }

        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
        return stream.ToArray();
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

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crcInput = new byte[t.Length + data.Length];
        t.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, t.Length);
        var crc = Crc32(crcInput);
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c);
    }
}

/// <summary>Các hàm GDI tối thiểu cho chụp ảnh (chỉ test).</summary>
internal static partial class D2DGdi
{
    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")]
    public static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    public static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);
}
