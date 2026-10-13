using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Integration.Tests.Menus;

/// <summary>
/// Chụp pixel của cửa sổ popup menu native (lớp "#32768") bằng PrintWindow, trên desktop của thread gọi (desktop ẩn khi chạy qua
/// tools/run-tests-hidden.ps1). Dùng để kiểm nền/chữ của menu thật mà không phụ thuộc ordinal uxtheme (WP shell-menus).
/// </summary>
internal static partial class MenuCapture
{
    internal readonly record struct Shot(int Width, int Height, uint[] Pixels)
    {
        public uint At(int x, int y) => Pixels[(y * Width) + x];
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string cls, string? title);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    private static partial int DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bmp, uint start, uint lines, uint[]? bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPels, YPels;
        public uint ClrUsed, ClrImportant;
    }

    /// <summary>Hwnd của popup menu đang mở trên desktop này (0 nếu chưa có).</summary>
    internal static nint FindMenuWindow() => FindWindow("#32768", null);

    /// <summary>Chụp popup <paramref name="hwnd"/> thành điểm ảnh 0x00RRGGBB; null nếu PrintWindow thất bại.</summary>
    internal static Shot? Capture(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            if (!PrintWindow(hwnd, mem, 2)) return null; // PW_RENDERFULLCONTENT
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var bits = new uint[w * h];
            if (GetDIBits(mem, bmp, 0, (uint)h, bits, ref info, 0) == 0) return null;
            for (var i = 0; i < bits.Length; i++) bits[i] &= 0x00FFFFFF;
            return new Shot(w, h, bits);
        }
        finally
        {
            _ = SelectObject(mem, old);
            _ = DeleteObject(bmp);
            _ = DeleteDC(mem);
            _ = ReleaseDC(0, screen);
        }
    }

    internal static double Luma(uint rgb) =>
        ((0.2126 * ((rgb >> 16) & 0xFF)) + (0.7152 * ((rgb >> 8) & 0xFF)) + (0.0722 * (rgb & 0xFF))) / 255.0;

    /// <summary>Ghi ảnh chụp ra BMP 32 bit (chẩn đoán bằng mắt khi biến PHOTOREVIEW_TEST_MENU_DUMP trỏ tới một thư mục).</summary>
    internal static void SaveBmp(Shot shot, string path)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        var size = shot.Width * shot.Height * 4;
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(54 + size);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(shot.Width);
        w.Write(-shot.Height);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(size);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);
        foreach (var p in shot.Pixels) w.Write(p);
    }}
