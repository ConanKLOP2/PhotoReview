using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace PhotoReview.App;

/// <summary>
/// Persists the Win32 window placement so monitor, restored bounds and maximized
/// state survive application restarts without DIP/pixel conversion errors.
/// </summary>
internal static class WindowPlacementService
{
    private const int ShowNormal = 1;
    private const int ShowMinimized = 2;
    private const int ShowMaximized = 3;

    internal static string PlacementPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoReview", "window-placement.json");

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(PlacementPath)) return;
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(PlacementPath), JsonOptions);
            if (placement is null || !IsVisible(placement.NormalPosition)) return;

            placement.Length = Marshal.SizeOf<WindowPlacement>();
            if (placement.ShowCommand == ShowMinimized) placement.ShowCommand = ShowNormal;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetWindowPlacement(handle, placement);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restore window placement", ex);
        }
    }

    public static void Save(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(handle, placement)) return;

            // Never reopen minimized. Closing from the taskbar should restore normally.
            if (placement.ShowCommand == ShowMinimized) placement.ShowCommand = ShowNormal;
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            var temp = PlacementPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(placement, JsonOptions));
            File.Move(temp, PlacementPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save window placement", ex);
        }
    }

    private static bool IsVisible(Rectangle bounds)
    {
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        var nativeBounds = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return Forms.Screen.AllScreens.Any(screen =>
        {
            var intersection = System.Drawing.Rectangle.Intersect(nativeBounds, screen.WorkingArea);
            return intersection.Width >= 80 && intersection.Height >= 80;
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, [In, Out] WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, [In] WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand = ShowMaximized;
        public Point MinPosition;
        public Point MaxPosition;
        public Rectangle NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
