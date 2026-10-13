using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;
using PhotoReview.TestSupport;
using Xunit.Abstractions;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// NOWPF-SHELL-WIRE-WPF-BRIDGE: Ä‘á»“ thá»‹ DI THáº¬T cá»§a shell (<see cref="ShellCompositionRoot"/>, dispatcher + vÃ²ng láº·p tháº­t, cá»­a sá»• chá»§
/// tháº­t) Ä‘Äƒng kÃ½ cáº§u cá»­a sá»• phá»¥: <see cref="IDialogService"/> vÃ  <see cref="IZoomPromptService"/> má»Ÿ cá»­a sá»• WPF tháº­t, owner = HWND
/// shell; cáº§u chá»‰ náº¡p khi dÃ¹ng; dá»n Ä‘Ãºng thá»© tá»±. KhÃ¡c <see cref="SecondaryWindowBridgeTests"/> (cá»•ng tráº§n): á»Ÿ Ä‘Ã¢y Ä‘i qua Ä‘Äƒng kÃ½ DI.
/// </summary>
[Trait("Category", "UI")]
[Collection("WpfBridge")]
public sealed class ShellWiringTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SkippedEntry> Skipped = [new SkippedEntry(@"C:\x\a.jpg", "locked")];

    /// <summary>DI tháº­t cá»§a shell trÃªn UI thread cá»§a harness; cá»•ng cá»­a sá»• phá»¥ náº¡p qua Ä‘Äƒng kÃ½ máº·c Ä‘á»‹nh (khÃ´ng override).</summary>
    private sealed class Wired : IAsyncDisposable
    {
        private readonly BridgeHarness _harness;
        private readonly TempRoot _root;

        private Wired(BridgeHarness harness, ServiceProvider provider, TempRoot root)
        {
            _harness = harness;
            Provider = provider;
            _root = root;
        }

        public ServiceProvider Provider { get; }

        public IDialogService Dialogs => Provider.GetRequiredService<IDialogService>();

        public IZoomPromptService Zoom => Provider.GetRequiredService<IZoomPromptService>();

        public SecondaryWindowPortHost Ports => Provider.GetRequiredService<SecondaryWindowPortHost>();

        public SettingsStore Store => Provider.GetRequiredService<SettingsStore>();

        public string DataRoot => Path.Combine(_root.Path, "data");

        public static async Task<Wired> StartAsync(BridgeHarness harness, Action<IServiceCollection>? configure = null)
        {
            var root = new TempRoot("shellwire");
            var provider = await harness.Ui.InvokeAsync(() => ShellCompositionRoot.Build(
                new ShellHostServices(harness.Ui.Dispatcher, () => harness.OwnerHwnd, harness.Ui.Loop), new ShellPresentationHub(),
                services =>
                {
                    services.AddSingleton<IAppPaths>(new AppPaths(root.Path, Path.Combine(root.Path, "data"), isolateConfig: true));
                    // Cá»•ng váº¯ng (mÃ´ phá»ng AOT/Ä‘á»™t biáº¿n) khÃ´ng Ä‘Æ°á»£c má»Ÿ trÃ¬nh soáº¡n tháº£o tháº­t cá»§a mÃ¡y: launcher giáº£.
                    services.AddSingleton<IShellLauncher>(new NoLauncher());
                    configure?.Invoke(services);
                }));
            return new Wired(harness, provider, root);
        }

        public async ValueTask DisposeAsync()
        {
            await _harness.Ui.InvokeAsync(Provider.Dispose);
            _root.Dispose();
        }
    }

    private static string Snapshot(string dir)
        => !Directory.Exists(dir)
            ? "(none)"
            : string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => f + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks));

    private static Window VisibleWindow() => Application.Current.Windows.OfType<Window>().Single(w => w.IsVisible);

    private static void Invoke(Window window, string method)
        => window.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);

    [Fact]
    public async Task Registration_ResolvesTheRealPort_LazilyAndSharedByDialogsAndZoom_WithoutLoadingTheBridge()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);

        var (dialogs, zoom, ports) = await harness.Ui.InvokeAsync(() => (wired.Dialogs, wired.Zoom, wired.Ports));

        Assert.IsType<Win32DialogService>(dialogs);
        Assert.IsType<Win32ZoomPromptService>(zoom);
        Assert.False(ports.Port.IsValueCreated); // chÆ°a má»Ÿ cá»­a sá»• phá»¥: WpfBridgeLoader (vÃ  WPF) chÆ°a náº¡p
        Assert.Equal(0, harness.Ui.Loop.MessageFilterCount); // bá»™ lá»c phÃ­m chá»‰ gáº¯n khi cáº§u náº¡p
        Assert.Same(ports.Port, wired.Ports.Port);
    }

    [Fact]
    public async Task Settings_ThroughTheDialogService_IsOwnedByTheShell_AndSavingRaisesStoreChanged()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);
        var changed = 0;
        await harness.Ui.InvokeAsync(() => wired.Store.Changed += (_, _) => Interlocked.Increment(ref changed));

        var shown = harness.Ui.InvokeAsync(() => wired.Dialogs.ShowSettings());
        var hwnd = await harness.WaitForOwnedWindowAsync("settings shown via IDialogService");
        Assert.False(harness.IsOwnerEnabled());
        Assert.Equal(1, harness.Ui.Loop.MessageFilterCount); // cáº§u Ä‘Ã£ náº¡p vÃ  gáº¯n bá»™ lá»c vÃ o vÃ²ng láº·p tháº­t cá»§a shell
        await harness.Ui.InvokeAsync(() => Invoke(VisibleWindow(), "Save_Click"));
        var accepted = await shown;

        await harness.WaitForNoOwnedWindowAsync("settings closed");
        Assert.True(accepted);
        Assert.True(Volatile.Read(ref changed) >= 1);
        Assert.NotEqual(0, hwnd);
        Assert.True(File.Exists(wired.Provider.GetRequiredService<IAppPaths>().ConfigFile));
    }

    [Fact]
    public async Task Settings_ClosedWithoutSaving_ReturnsFalse_AndDoesNotRaiseChanged()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);
        var changed = 0;
        await harness.Ui.InvokeAsync(() => wired.Store.Changed += (_, _) => Interlocked.Increment(ref changed));

        var shown = harness.Ui.InvokeAsync(() => wired.Dialogs.ShowSettings(SettingsTarget.Default));
        BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("settings shown"));

        Assert.False(await shown);
        Assert.Equal(0, Volatile.Read(ref changed));
    }

    [Fact]
    public async Task Recovery_ThroughTheDialogService_Opens_AndChangesNoData()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);
        var before = Snapshot(wired.DataRoot);

        var shown = harness.Ui.InvokeAsync(() => wired.Dialogs.ShowRecovery());
        BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("recovery shown"));
        await shown;

        await harness.WaitForNoOwnedWindowAsync("recovery closed");
        Assert.Equal(before, Snapshot(wired.DataRoot));
    }

    [Theory]
    [InlineData("Diagnostics")]
    [InlineData("SkippedFiles")]
    [InlineData("BatchReview")]
    public async Task OtherModalWindows_ThroughTheDialogService_OpenOwnedAndClose(string which)
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);

        var shown = harness.Ui.InvokeAsync(() => which switch
        {
            "Diagnostics" => Run(() => wired.Dialogs.ShowDiagnostics()),
            "SkippedFiles" => Run(() => wired.Dialogs.ShowSkippedFiles(Skipped)),
            _ => wired.Dialogs.ShowBatchReview([new BatchReviewItem(@"C:\x\a.jpg", 10)]).ToString(),
        });
        var hwnd = await harness.WaitForOwnedWindowAsync(which + " shown");
        Assert.False(harness.IsOwnerEnabled());
        BridgeHarness.Close(hwnd);

        var result = await shown;
        await harness.WaitForNoOwnedWindowAsync(which + " closed");
        Assert.True(harness.IsOwnerEnabled());
        Assert.Equal(which == "BatchReview" ? "False" : "done", result);
    }

    private static string Run(Action action)
    {
        action();
        return "done";
    }

    [Fact]
    public async Task ZoomPrompt_ThroughTheZoomService_CancelIsNull_AndOkReturnsTheValidatedPercent()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);

        var cancelled = harness.Ui.InvokeAsync(() => wired.Zoom.PromptCustomZoom(100));
        BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("zoom prompt shown"));
        Assert.Null(await cancelled);
        await harness.WaitForNoOwnedWindowAsync("zoom prompt closed");

        var accepted = harness.Ui.InvokeAsync(() => wired.Zoom.PromptCustomZoom(100));
        var dialog = await harness.WaitForOwnedWindowAsync("zoom prompt shown again");
        await harness.Ui.InvokeAsync(() =>
        {
            // WP-35 GÄ1: the prompt is a native Win32 dialog (edit id 101, Apply = IDOK), not a WPF window.
            Assert.True(PhotoReview.Shell.Interop.User32.SetWindowText(PhotoReview.Shell.Interop.User32.GetDlgItem(dialog, 101), "175"));
            PhotoReview.Shell.Interop.User32.PostMessage(dialog, 0x0111 /* WM_COMMAND */, 1 /* IDOK */, 0);
        });

        Assert.Equal(175, await accepted);
        Assert.False(wired.Ports.Port.Value is null);
    }

    [Fact]
    public async Task Benchmark_ThroughTheDialogService_IsModeless_AndTabReachesWpfThroughTheShellLoopFilter()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);

        await harness.Ui.InvokeAsync(() => wired.Dialogs.ShowBenchmark(null)); // tráº£ vá» ngay
        var hwnd = await harness.WaitForOwnedWindowAsync("benchmark shown");
        Assert.True(harness.IsOwnerEnabled());
        Assert.Equal(1, harness.Ui.Loop.MessageFilterCount);
        var seen = new List<int>();
        ThreadMessageEventHandler observe = (ref MSG m, ref bool _) => seen.Add(m.message);
        await harness.Ui.InvokeAsync(() => ComponentDispatcher.ThreadPreprocessMessage += observe);

        BridgeHarness.PostKey(hwnd, 0x09 /* VK_TAB */);
        await Wait.UntilAsync(() => harness.Ui.InvokeAsync(() => seen.Contains(0x0100)).GetAwaiter().GetResult(),
            "WM_KEYDOWN (Tab) reached ComponentDispatcher through the shell loop");
        await harness.Ui.InvokeAsync(() => ComponentDispatcher.ThreadPreprocessMessage -= observe);

        BridgeHarness.Close(hwnd);
        await harness.WaitForNoOwnedWindowAsync("benchmark closed");
        output.WriteLine("messages seen: " + seen.Count);
    }

    [Fact]
    public async Task DisposingThePortHost_ClosesModelessAndModalWindows_AndUnhooksTheLoopFilter()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);
        await harness.Ui.InvokeAsync(() => wired.Dialogs.ShowBenchmark(null));
        await harness.WaitForOwnedWindowAsync("benchmark shown");
        var modal = harness.Ui.InvokeAsync(() => wired.Dialogs.ShowSettings()); // vÃ²ng lá»“ng ShowDialog cháº¡y trÃªn UI thread
        await Wait.UntilAsync(() => harness.OwnedVisibleWindows().Count == 2, "benchmark + settings shown");

        // NhÆ° ShellStartup khi shell thoÃ¡t giá»¯a chá»«ng: Dispose cá»•ng trÃªn UI thread (vÃ²ng lá»“ng cá»§a há»™p modal Ä‘ang cháº¡y).
        await harness.Ui.InvokeAsync(() => wired.Ports.Dispose());

        Assert.False(await modal); // há»™p modal tráº£ vá», khÃ´ng treo
        await harness.WaitForNoOwnedWindowAsync("all secondary windows closed");
        Assert.Equal(0, harness.Ui.Loop.MessageFilterCount);
        Assert.True(harness.IsOwnerEnabled());
    }

    [Fact]
    public async Task OpeningFromAnotherThread_IsRejected_BecauseWpfHasThreadAffinity()
    {
        await using var harness = await BridgeHarness.StartAsync();
        await using var wired = await Wired.StartAsync(harness);
        // Recovery (chÆ°a cÃ³ há»™p thoáº¡i Win32) náº¡p cáº§u WPF trÃªn UI thread (host gáº¯n thread nÃ y); Diagnostics khÃ´ng cÃ²n náº¡p cáº§u.
        var shown = harness.Ui.InvokeAsync(() => wired.Dialogs.ShowRecovery());
        BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("recovery shown"));
        await shown;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => wired.Dialogs.ShowRecovery()));

        Assert.Contains("UI thread", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortRegisteredByTheTest_WinsOverTheRealBridge_AndAnAbsentBridgeFallsBack()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var fake = new CountingPort();
        await using (var wired = await Wired.StartAsync(harness, s => s.AddSingleton<ISecondaryWindowPort>(fake)))
        {
            await harness.Ui.InvokeAsync(() => wired.Dialogs.ShowRecovery());
            Assert.Equal(1, fake.Recoveries);
            Assert.Empty(harness.OwnedVisibleWindows());
        }

        await using var absent = await Wired.StartAsync(harness, s => s.AddSingleton<SecondaryWindowPortLoader>((_, _) => null));
        await harness.Ui.InvokeAsync(() => absent.Dialogs.ShowRecovery()); // cá»•ng táº¡m: khÃ´ng cá»­a sá»•, khÃ´ng nÃ©m
        Assert.Empty(harness.OwnedVisibleWindows());

        // WP-35 GÄ1: cáº§u váº¯ng (mÃ´ phá»ng AOT) nhÆ°ng há»™p thoáº¡i zoom lÃ  Win32 gá»‘c nÃªn váº«n má»Ÿ Ä‘Æ°á»£c; Ä‘Ã³ng = huá»· = null.
        var zoom = harness.Ui.InvokeAsync(() => absent.Zoom.PromptCustomZoom(100));
        BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("native zoom prompt without the bridge"));
        Assert.Null(await zoom);
        await harness.WaitForNoOwnedWindowAsync("native zoom prompt closed");
    }

    private sealed class NoLauncher : IShellLauncher
    {
        public bool TryOpenFile(string path, string verb) => true;

        public bool TryRun(string fileName, string arguments) => true;
    }

    private sealed class CountingPort : ISecondaryWindowPort
    {
        public int Recoveries { get; private set; }

        public bool ShowSettings(nint ownerHwnd, SettingsTarget target) => false;

        public void ShowRecovery(nint ownerHwnd) => Recoveries++;

        public void ShowDiagnostics(nint ownerHwnd)
        {
        }

        public void ShowBenchmark(nint ownerHwnd, string? folder)
        {
        }

        public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
        {
        }

        public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items) => false;

        public int? PromptCustomZoom(nint ownerHwnd, int currentPercent) => null;
    }
}
