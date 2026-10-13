using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1: định tuyến theo từng cửa sổ (<see cref="SecondaryWindowRouter"/>: cổng Win32 -> cầu WPF -> cổng tạm) và cổng Win32
/// (<see cref="Win32SecondaryWindowPort"/>) mở hộp thoại thật qua DI tối thiểu.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SecondaryWindowRoutingTests
{
    private sealed class Spy : ISecondaryWindowPort, IDisposable
    {
        private readonly SecondaryWindowKind[] _supported;

        public Spy(params SecondaryWindowKind[] supported)
        {
            _supported = supported;
        }

        public List<string> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public bool Supports(SecondaryWindowKind kind) => _supported.Contains(kind);

        public bool ShowSettings(nint ownerHwnd, SettingsTarget target)
        {
            Calls.Add("settings");
            return true;
        }

        public void ShowRecovery(nint ownerHwnd) => Calls.Add("recovery");

        public void ShowDiagnostics(nint ownerHwnd) => Calls.Add("diagnostics");

        public void ShowBenchmark(nint ownerHwnd, string? folder) => Calls.Add("benchmark");

        public IReadOnlyList<PhotoReview.Core.Settings.ReviewAction>? ShowActionProfiles(nint ownerHwnd,
            IReadOnlyList<PhotoReview.Core.Settings.ReviewAction> actions)
        {
            Calls.Add("actionProfiles");
            return actions;
        }

        public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries) => Calls.Add("skipped");

        public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
        {
            Calls.Add("batch");
            return true;
        }

        public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
        {
            Calls.Add("zoom");
            return 150;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class NoLauncher : IShellLauncher
    {
        public List<string> Opened { get; } = [];

        public bool TryOpenFile(string path, string verb)
        {
            Opened.Add(path);
            return true;
        }

        public bool TryRun(string fileName, string arguments) => false;
    }

    private static readonly SecondaryWindowKind[] Win32Kinds =
    [
        SecondaryWindowKind.Diagnostics, SecondaryWindowKind.SkippedFiles, SecondaryWindowKind.BatchReview, SecondaryWindowKind.ZoomPrompt,
        SecondaryWindowKind.Benchmark, SecondaryWindowKind.ActionProfiles, SecondaryWindowKind.Recovery,
    ];

    private static (SecondaryWindowRouter Router, Spy Native, Spy? Bridge, Func<bool> BridgeLoaded, List<string> Notices, NoLauncher Launcher) Build(
        bool bridgePresent, bool bridgeThrows = false, bool nativeSettings = false)
    {
        var native = new Spy(nativeSettings ? [.. Win32Kinds, SecondaryWindowKind.Settings] : Win32Kinds);
        var bridge = bridgePresent ? new Spy(Enum.GetValues<SecondaryWindowKind>()) : null;
        var loaded = false;
        var lazy = new Lazy<ISecondaryWindowPort?>(() =>
        {
            loaded = true;
            return bridgeThrows ? throw new FileNotFoundException("PresentationFramework") : bridge;
        });
        var notices = new List<string>();
        var launcher = new NoLauncher();
        var fallback = new SecondaryWindowFallback(new AppPaths(Path.GetTempPath()), launcher, notices.Add);
        return (new SecondaryWindowRouter(native, lazy, fallback), native, bridge, () => loaded, notices, launcher);
    }

    [Fact]
    public void WindowsTheWin32PortSupports_NeverTouchTheBridge()
    {
        var (router, native, bridge, loaded, _, _) = Build(bridgePresent: true);

        router.ShowDiagnostics(0);
        router.ShowSkippedFiles(0, []);
        Assert.True(router.ShowBatchReview(0, []));
        Assert.Equal(150, router.PromptCustomZoom(0, 100));
        router.ShowBenchmark(0, null);
        Assert.NotNull(router.ShowActionProfiles(0, []));
        router.ShowRecovery(0); // WP-35 GĐ4: Recovery is a Win32 window now

        Assert.Equal(["diagnostics", "skipped", "batch", "zoom", "benchmark", "actionProfiles", "recovery"], native.Calls);
        Assert.Empty(bridge!.Calls);
        Assert.False(loaded(), "the WPF bridge must not even be loaded for windows the Win32 port supports");
    }

    [Fact]
    public void WindowsTheWin32PortLacks_GoToTheWpfBridge()
    {
        var (router, native, bridge, loaded, notices, _) = Build(bridgePresent: true);

        Assert.True(router.ShowSettings(0, SettingsTarget.Default));

        Assert.Equal(["settings"], bridge!.Calls); // WP-35 GĐ4: only Settings is left on the bridge
        Assert.Empty(native.Calls);
        Assert.True(loaded());
        Assert.Empty(notices);
    }

    [Fact]
    public void Settings_GoesToTheWin32Port_WhenItSupportsIt_AndNeverLoadsTheBridge()
    {
        // WP-35 GĐ3a: once the Win32 port takes Settings (flag or every page present) the WPF bridge is not even loaded for it.
        var (router, native, bridge, loaded, notices, _) = Build(bridgePresent: true, nativeSettings: true);

        Assert.True(router.ShowSettings(0, SettingsTarget.ExternalEditor));

        Assert.Equal(["settings"], native.Calls);
        Assert.Empty(bridge!.Calls);
        Assert.False(loaded());
        Assert.Empty(notices);
    }

    [Fact]
    public void WithoutTheBridge_LackingWindowsFallBackToTheTemporaryPort_WhileWin32WindowsStillWork()
    {
        var (router, native, _, loaded, notices, launcher) = Build(bridgePresent: false);

        Assert.False(router.ShowSettings(0, SettingsTarget.Default)); // opens config.json, nothing saved
        router.ShowRecovery(0); // WP-35 GĐ4: a real window now, not a notice (also in the AOT build)
        router.ShowBenchmark(0, null); // WP-35 GĐ2: likewise
        router.ShowDiagnostics(0);
        Assert.True(router.ShowBatchReview(0, []));

        Assert.True(loaded());
        Assert.Single(launcher.Opened);
        Assert.Equal(["recovery", "benchmark", "diagnostics", "batch"], native.Calls);
        Assert.Equal([Tr.StatusSecondaryUnavailableSettings], notices);
    }

    [Fact]
    public void ABridgeThatFailsToLoad_IsTreatedAsAbsent_NotAsACrash()
    {
        var (router, native, _, _, notices, _) = Build(bridgePresent: true, bridgeThrows: true);

        Assert.False(router.ShowSettings(0, SettingsTarget.Default)); // Settings is the one window still on the bridge
        router.ShowDiagnostics(0);

        Assert.Equal([Tr.StatusSecondaryUnavailableSettings], notices);
        Assert.Equal(["diagnostics"], native.Calls);
    }

    [Fact]
    public void Dispose_ClosesTheWin32Port_AndTheBridgeOnlyWhenItWasLoaded()
    {
        var untouched = Build(bridgePresent: true);
        untouched.Router.Dispose();
        Assert.True(untouched.Native.Disposed);
        Assert.False(untouched.BridgeLoaded());
        Assert.False(untouched.Bridge!.Disposed);

        var used = Build(bridgePresent: true);
        Assert.True(used.Router.ShowSettings(0, SettingsTarget.Default));
        used.Router.Dispose();
        Assert.True(used.Native.Disposed);
        Assert.True(used.Bridge!.Disposed);
    }

    [Fact]
    public void Win32Port_Supports_ExactlyTheNativeWindows()
    {
        var port = new Win32SecondaryWindowPort(new ServiceCollection().BuildServiceProvider());

        foreach (var kind in Enum.GetValues<SecondaryWindowKind>())
        {
            // Settings is a Win32 window of its own once every page of the catalog exists (WP-35 GĐ3: the last page, Mouse).
            var expected = Win32Kinds.Contains(kind) || (kind == SecondaryWindowKind.Settings && SettingsPageCatalog.IsComplete);
            Assert.Equal(expected, port.Supports(kind));
        }

        Assert.False(port.ShowSettings(0, SettingsTarget.Default));
    }

    private static ServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReviewMetrics());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Win32Port_ZoomPrompt_ReturnsTheValue_OrNullWhenCancelled()
    {
        var (ok, cancelled) = OnSta(() =>
        {
            using var provider = Services();
            var okPort = new Win32SecondaryWindowPort(provider, null, d =>
            {
                SetText(d, ZoomPromptDialog.EditId, "333");
                Command(d, ZoomPromptDialog.ApplyId);
            });
            var cancelPort = new Win32SecondaryWindowPort(provider, null, d => Command(d, ZoomPromptDialog.CancelId));
            var owner = CreateOwner();
            try
            {
                return (okPort.PromptCustomZoom(owner, 100), cancelPort.PromptCustomZoom(owner, 100));
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.Equal(333, ok);
        Assert.Null(cancelled);
    }

    [Fact]
    public void Win32Port_BatchReview_IsTrueOnlyForConfirm_AndSkippedAndDiagnosticsOpenOwnedModalWindows()
    {
        var results = OnSta(() =>
        {
            using var provider = Services();
            var owner = CreateOwner();
            try
            {
                var seenOwner = new List<nint>();
                var seenEnabled = new List<bool>();
                var confirm = new Win32SecondaryWindowPort(provider, null, d =>
                {
                    seenOwner.Add(User32.GetWindow(d.Hwnd, 4 /* GW_OWNER */));
                    seenEnabled.Add(User32.IsWindowEnabled(owner));
                    Command(d, ListDialog.ConfirmId);
                });
                var cancel = new Win32SecondaryWindowPort(provider, null, d => Command(d, ListDialog.CancelId));
                var yes = confirm.ShowBatchReview(owner, [new BatchReviewItem(@"C:\a.jpg", 5)]);
                var no = cancel.ShowBatchReview(owner, [new BatchReviewItem(@"C:\a.jpg", 5)]);

                var closeAll = new Win32SecondaryWindowPort(provider, null, d => d.Close());
                closeAll.ShowSkippedFiles(owner, [new SkippedEntry(@"C:\a.jpg", "x")]);
                closeAll.ShowDiagnostics(owner);
                return (yes, no, seenOwner[0] == owner, seenEnabled[0], closeAll.OpenCount);
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.True(results.yes);
        Assert.False(results.no);
        Assert.True(results.Item3, "the dialog must be owned by the shell window");
        Assert.False(results.Item4, "the shell window must be disabled while the dialog runs");
        Assert.Equal(0, results.OpenCount);
    }

    [Fact]
    public void Win32Port_Dispose_ClosesAnOpenDialog_AndLaterCommandsDoNothing()
    {
        var (zoom, afterDispose, ownerEnabled) = OnSta(() =>
        {
            using var provider = Services();
            Win32SecondaryWindowPort? port = null;
            port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                Assert.Equal(1, port!.OpenCount);
                port.Dispose(); // as ShellStartup does when the shell closes with a dialog open
                Assert.True(d.IsClosed);
            });
            var owner = CreateOwner();
            try
            {
                var value = port.PromptCustomZoom(owner, 100);
                var later = port.PromptCustomZoom(owner, 100); // disposed: no window, no throw
                port.ShowDiagnostics(owner);
                return (value, later, User32.IsWindowEnabled(owner));
            }
            finally
            {
                DestroyOwner(owner);
            }
        });

        Assert.Null(zoom);
        Assert.Null(afterDispose);
        Assert.True(ownerEnabled);
    }

    [Fact]
    public void Win32Port_RejectsAnotherThread_BecauseWindowsHaveThreadAffinity()
    {
        var error = OnSta(() =>
        {
            using var provider = Services();
            var port = new Win32SecondaryWindowPort(provider);
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    port.PromptCustomZoom(0, 100);
                }
                catch (InvalidOperationException ex)
                {
                    caught = ex;
                }
            });
            thread.Start();
            thread.Join();
            return caught;
        });

        Assert.NotNull(error);
    }

    [Fact]
    public void Win32Port_Diagnostics_CopyGoesThroughTheClipboardService()
    {
        var clipboard = new FakeClipboard();
        OnSta(() =>
        {
            using var provider = Services(s => s.AddSingleton<IClipboardService>(clipboard));
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                Command(d, DiagnosticsDialog.CopyId);
                d.Close();
            });
            port.ShowDiagnostics(0);
            return 0;
        });

        var text = Assert.Single(clipboard.Texts);
        Assert.Contains(Tr.DiagnosticsLabelCacheHits, text, StringComparison.Ordinal);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public bool TrySetText(string text)
        {
            Texts.Add(text);
            return true;
        }
    }
}
