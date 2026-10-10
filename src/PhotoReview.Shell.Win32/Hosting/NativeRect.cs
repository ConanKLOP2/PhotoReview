using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>Tiện ích nhỏ cho <see cref="Rect"/> của Shell.Interop (struct interop không mang logic).</summary>
internal static class NativeRect
{
    public static Rect Create(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    public static int Width(in Rect rect) => rect.Right - rect.Left;

    public static int Height(in Rect rect) => rect.Bottom - rect.Top;
}
