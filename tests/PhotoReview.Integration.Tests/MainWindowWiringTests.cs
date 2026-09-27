using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The real MainWindow's wiring: what the XAML declares (drag-drop, compare panes, overlay toolbar, title bar) is
/// observed through the live window and routed events, and the native window placement is round-tripped through a real
/// HWND. Replaces the former source-text / XAML-grep checks.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class MainWindowWiringTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static Task OnSta(Action body) => StaTestHost.RunAsync(() => { body(); return Task.CompletedTask; });

    [Fact(DisplayName = "MainWindow accepts drops, opens Normal, and keeps the folder in the title bar (FolderText hidden)")]
    public async Task Window_AcceptsDrops_OpensNormal_ShowsFolderInTitle()
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            try
            {
                Assert.True(window.AllowDrop);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(Visibility.Collapsed, window.FolderText.Visibility);

                window.ViewModel.UpdateTitle(@"C:\Photos\Trip");
                Assert.Equal(window.ViewModel.FolderTitle, window.Title);
                Assert.Contains("Trip", window.Title);
            }
            finally { window.Close(); }
        });
    }

    [Fact(DisplayName = "MainWindow toolbar is an overlay: no root grid rows, the toolbar is on top with the theme's translucent overlay brush")]
    public async Task Toolbar_IsAnOverlayOnTopOfTheImage()
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            try
            {
                var root = (Grid)window.Content;
                Assert.Empty(root.RowDefinitions);
                var toolbarZ = Panel.GetZIndex(window.ToolbarPanel);
                Assert.All(root.Children.OfType<UIElement>().Where(c => !ReferenceEquals(c, window.ToolbarPanel)),
                    c => Assert.True(Panel.GetZIndex(c) < toolbarZ, $"{c.GetType().Name} is not below the toolbar"));
                Assert.Same(window.FindResource("Dark.Overlay"), window.ToolbarPanel.Background);
            }
            finally { window.Close(); }
        });
    }

    [Theory(DisplayName = "Compare panes are focusable and Enter/Space on a pane selects it; other keys do not")]
    [InlineData(true, Key.Enter)]
    [InlineData(true, Key.Space)]
    [InlineData(false, Key.Enter)]
    [InlineData(false, Key.Space)]
    public async Task ComparePane_KeyboardSelectsIt(bool left, Key key)
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            using var source = new HwndSource(new HwndSourceParameters("compare-key"));
            try
            {
                var compare = window.ViewModel.Compare;
                compare.LeftPath = @"C:\a.jpg";
                compare.RightPath = @"C:\b.jpg";
                var pane = left ? window.CompareLeftBorder : window.CompareRightBorder;
                Assert.True(pane.Focusable);

                var other = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                pane.RaiseEvent(other);
                Assert.Null(compare.SelectedPath);
                Assert.False(other.Handled);

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                pane.RaiseEvent(args);
                Assert.Equal(left ? compare.LeftPath : compare.RightPath, compare.SelectedPath);
                Assert.True(args.Handled);
            }
            finally { window.Close(); }
        });
    }

    [Theory(DisplayName = "A left click on a compare pane selects that side")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComparePane_ClickSelectsSide(bool left)
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            try
            {
                var compare = window.ViewModel.Compare;
                compare.LeftPath = @"C:\a.jpg";
                compare.RightPath = @"C:\b.jpg";
                var pane = left ? window.CompareLeftBorder : window.CompareRightBorder;
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                pane.RaiseEvent(args);
                Assert.Equal(left ? compare.LeftPath : compare.RightPath, compare.SelectedPath);
                Assert.True(args.Handled);
            }
            finally { window.Close(); }
        });
    }

    [Fact(DisplayName = "Dropping files onto the window: drag-over advertises Copy for files (None otherwise) and Drop opens the dropped folder")]
    public async Task DragOverAndDrop_OpenTheDroppedFolder()
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("drop");
        WriteImage(Path.Combine(folder.Path, "one.png"));
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(null, new TestHostHooks { OnPresented = presented.Add });

                var files = new DataObject(DataFormats.FileDrop, new[] { folder.Path });
                var over = CreateDragEvent(DragDrop.PreviewDragOverEvent, files);
                window.RaiseEvent(over);
                Assert.Equal(DragDropEffects.Copy, over.Effects);
                Assert.True(over.Handled);

                var text = CreateDragEvent(DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.Text, "hello"));
                window.RaiseEvent(text);
                Assert.Equal(DragDropEffects.None, text.Effects);

                window.RaiseEvent(CreateDragEvent(DragDrop.DropEvent, files));
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Deadline),
                    $"The dropped folder never presented. StatusText={window.StatusText.Text}");
                Assert.Equal(Path.Combine(folder.Path, "one.png"), presented[0], ignoreCase: true);
            });
        }
        finally
        {
            if (window is { } opened)
                await OnSta(() => { try { opened.Close(); } catch (InvalidOperationException) { } });
        }
    }

    [Fact(DisplayName = "Closing the main window saves its native placement to the placement file")]
    public async Task Close_SavesWindowPlacement()
    {
        using var dataRoot = new DataRootFixture();
        var file = dataRoot.Root.Combine("placement.json");
        await OnSta(() => ShowOffScreen(file).Close());
        Assert.True(File.Exists(file), "Closing did not write the placement file");
        var bounds = ReadPlacement(file).NormalPosition;
        Assert.True(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top);
    }

    [Fact(DisplayName = "A saved placement whose monitor is gone is rejected: the window is left untouched")]
    public async Task Restore_RejectsPlacementOnNoMonitor()
    {
        using var temp = new TempRoot("placement-gone");
        var file = temp.Combine("placement.json");
        WritePlacement(file, new WindowPlacementService.Rectangle { Left = -20000, Top = -20000, Right = -19000, Bottom = -19400 });
        await OnSta(() =>
        {
            var window = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Assert.False(IsWindowVisible(handle));
                WindowPlacementService.Restore(window, file);
                // Applying a placement (SW_SHOWNORMAL) shows the window and Windows nudges it onto a monitor, so a
                // rejected placement is the one that leaves the window untouched: still hidden.
                Assert.False(IsWindowVisible(handle), "The unreachable placement was applied");
            }
            finally { window.Close(); }
        });
    }

    // Applying a placement makes the window visible on the real desktop for a moment, so this is a Native test.
    [Fact(DisplayName = "A saved placement on a connected monitor is restored when the main window has loaded")]
    [Trait("Category", "Native")]
    public async Task Restore_AppliesPlacementOnLoaded()
    {
        using var dataRoot = new DataRootFixture();
        var placementFile = dataRoot.Root.Combine("placement.json");
        var work = PrimaryWorkArea();
        var wanted = new WindowPlacementService.Rectangle { Left = work.Left + 60, Top = work.Top + 60, Right = work.Left + 560, Bottom = work.Top + 460 };
        WritePlacement(placementFile, wanted);
        using var temp = new TempRoot("placement-restore");
        await StaTestHost.RunAsync(async () =>
        {
            var window = ShowOffScreen(placementFile);
            try
            {
                Assert.True(await StaTestHost.WaitForAsync(() => window.IsLoaded, Deadline), "The window never loaded");
                var after = temp.Combine("after.json");
                WindowPlacementService.Save(window, after);
                Assert.Equal(wanted, ReadPlacement(after).NormalPosition);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Off-screen, not activated, not in the taskbar; placement restore/save stays enabled on the test's own file (never the user's real one).</summary>
    private static MainWindow ShowOffScreen(string placementFile)
    {
        var window = TestAppHost.CreateMainWindow(null, placementFile: placementFile);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        return window;
    }

    /// <summary>DragEventArgs has no public constructor; the internal one takes (data, keys, allowed effects, target, point).</summary>
    private static DragEventArgs CreateDragEvent(RoutedEvent routedEvent, IDataObject data)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, new Border(), new System.Windows.Point(1, 1)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true, WriteIndented = true };

    private static void WritePlacement(string file, WindowPlacementService.Rectangle bounds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var placement = new WindowPlacementService.WindowPlacement { Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>(), ShowCommand = 1, NormalPosition = bounds };
        File.WriteAllText(file, JsonSerializer.Serialize(placement, PlacementJson));
    }

    private static WindowPlacementService.WindowPlacement ReadPlacement(string file) =>
        JsonSerializer.Deserialize<WindowPlacementService.WindowPlacement>(File.ReadAllText(file), PlacementJson)!;

    private static WindowPlacementService.Rectangle PrimaryWorkArea()
    {
        var monitor = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(monitor, ref info));
        return info.Work;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public WindowPlacementService.Rectangle Monitor; public WindowPlacementService.Rectangle Work; public int Flags; }

    private static void WriteImage(string path)
    {
        var pixels = new byte[16 * 16 * 4];
        Array.Fill(pixels, (byte)0x90);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
