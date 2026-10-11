using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.TestSupport;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-19b: một UI thread thật của shell (dispatcher Win32 + <see cref="MessageLoop"/>), một cửa sổ chủ <see cref="ShellWindow"/> và
/// cầu cửa sổ phụ WPF thật (<see cref="SecondaryWindowPorts"/>) trên DI tối thiểu (SettingsStore trong thư mục tạm).
/// </summary>
internal sealed class BridgeHarness : IAsyncDisposable
{
    private const uint GwOwner = 4;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    private readonly TempRoot _root;

    private BridgeHarness(UiThread ui, IServiceProvider services, TempRoot root)
    {
        Ui = ui;
        Services = services;
        _root = root;
    }

    public UiThread Ui { get; }

    public IServiceProvider Services { get; }

    public SettingsStore Store => Services.GetRequiredService<SettingsStore>();

    public ShellWindow Owner { get; private set; } = null!;

    public nint OwnerHwnd { get; private set; }

    public uint UiThreadId { get; private set; }

    public ISecondaryWindowPort Port { get; private set; } = null!;

    // Một UI thread cho cả tiến trình test: Application WPF là một-cho-tiến-trình và gắn với thread tạo nó (đúng như shell thật:
    // một UI thread). Thread nền, không dispose (tiến trình test thoát là xong).
    private static readonly Lazy<Task<UiThread>> SharedUi = new(() => UiThread.StartAsync(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<BridgeHarness> StartAsync()
    {
        var ui = await SharedUi.Value;
        var root = new TempRoot("wpfbridge");
        var services = new ServiceCollection();
        var paths = new AppPaths(root.Path);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IFileSystem>(new PhysicalFileSystem());
        services.AddSingleton(sp => new SettingsStore(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IFileSystem>(), NullLog.Instance));
        services.AddSingleton(new ReviewMetrics());
        services.AddSingleton<IClock>(new SystemClock());
        services.AddSingleton(sp => new OperationJournal(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IClock>()));
        var harness = new BridgeHarness(ui, services.BuildServiceProvider(), root);
        harness.Owner = await ui.InvokeAsync(() =>
        {
            var clock = new TimerFrameClock();
            ui.Loop.AddFrameClock(clock);
            var window = new ShellWindow(new ShellWindowOptions(Title: "WP19b owner"), clock);
            window.Show(activate: false);
            return window;
        });
        harness.OwnerHwnd = harness.Owner.Hwnd;
        harness.UiThreadId = await ui.InvokeAsync(() => GetCurrentThreadId());
        return harness;
    }

    /// <summary>Tạo cổng cửa sổ phụ (như WP-20 sẽ làm) trên UI thread; bộ lọc phím gắn vào vòng lặp thật.</summary>
    public async Task<ISecondaryWindowPort> LoadPortAsync()
    {
        Port = await Ui.InvokeAsync(() => SecondaryWindowPorts.CreateLazy(Services, Ui.Loop).Value
            ?? throw new InvalidOperationException("bridge not loaded"));
        return Port;
    }

    /// <summary>Cửa sổ top-level (không phải owner) thuộc UI thread, đang hiện, có owner là cửa sổ shell.</summary>
    public List<nint> OwnedVisibleWindows()
    {
        var found = new List<nint>();
        EnumThreadWindows(UiThreadId, (hwnd, _) =>
        {
            if (hwnd != OwnerHwnd && IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == OwnerHwnd)
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>Mọi cửa sổ top-level đang hiện của UI thread, trừ cửa sổ chủ (kể cả cửa sổ không có owner).</summary>
    public List<nint> TopLevelVisibleWindows()
    {
        var found = new List<nint>();
        EnumThreadWindows(UiThreadId, (hwnd, _) =>
        {
            if (hwnd != OwnerHwnd && IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        return found;
    }

    public async Task<nint> WaitForOwnedWindowAsync(string what)
    {
        nint hwnd = 0;
        await Wait.UntilAsync(() => (hwnd = OwnedVisibleWindows().FirstOrDefault()) != 0, what, TimeSpan.FromSeconds(20));
        return hwnd;
    }

    public async Task WaitForNoOwnedWindowAsync(string what)
        => await Wait.UntilAsync(() => OwnedVisibleWindows().Count == 0, what, TimeSpan.FromSeconds(20));

    public bool IsOwnerEnabled() => IsWindowEnabled(OwnerHwnd);

    public static void Close(nint hwnd) => Assert.True(User32.PostMessage(hwnd, WindowMessages.WmClose, 0, 0));

    public static void PostKey(nint hwnd, int virtualKey)
    {
        Assert.True(User32.PostMessage(hwnd, WmKeyDown, virtualKey, 0x00000001));
        Assert.True(User32.PostMessage(hwnd, WmKeyUp, virtualKey, unchecked((nint)0xC0000001)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Ui.InvokeAsync(() =>
            {
                (Port as IDisposable)?.Dispose();
                Owner.Dispose();
            });
        }
        finally
        {
            _root.Dispose();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
}
