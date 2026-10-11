using System.Reflection;
using System.Windows;
using System.Windows.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Win32.Dialogs;
using Xunit.Abstractions;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-19b (C-13b): mở/đóng từng cửa sổ phụ WPF qua cầu trên UI thread thật của shell (desktop ẩn khi chạy qua
/// tools/run-tests-hidden.ps1). Cửa sổ đóng bằng <c>PostMessage(WM_CLOSE)</c> như nút X; không SendInput/foreground (N-8).
/// Một UI thread mỗi test; <c>Application</c> WPF là một cho cả tiến trình (cầu tạo lười, dùng lại).
/// </summary>
[Trait("Category", "UI")]
[Collection("WpfBridge")]
public sealed class SecondaryWindowBridgeTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SkippedEntry> Skipped = [new SkippedEntry(@"C:\x\a.jpg", "locked")];

    public static TheoryData<string> ModalWindows => new() { "Settings", "Recovery", "Diagnostics", "SkippedFiles", "BatchReview", "ZoomPrompt" };

    /// <summary>Gọi cửa sổ modal; trả kết quả dạng chuỗi (để so sánh giữa các cửa sổ).</summary>
    private static string Show(ISecondaryWindowPort port, nint owner, string which) => which switch
    {
        "Settings" => port.ShowSettings(owner, SettingsTarget.Default).ToString(),
        "Recovery" => Run(() => port.ShowRecovery(owner)),
        "Diagnostics" => Run(() => port.ShowDiagnostics(owner)),
        "SkippedFiles" => Run(() => port.ShowSkippedFiles(owner, Skipped)),
        "BatchReview" => port.ShowBatchReview(owner, [new BatchReviewItem(@"C:\x\a.jpg", 10)]).ToString(),
        "ZoomPrompt" => (port.PromptCustomZoom(owner, 150)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"),
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private static string Run(Action action)
    {
        action();
        return "done";
    }

    [Theory]
    [MemberData(nameof(ModalWindows))]
    public async Task ModalWindow_OpensOwnedByTheShellWindow_DisablesTheOwner_AndClosesWithWmClose(string which)
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();

        var shown = harness.Ui.InvokeAsync(() => Show(port, harness.OwnerHwnd, which)); // chặn UI thread tới khi cửa sổ đóng
        var hwnd = await harness.WaitForOwnedWindowAsync(which + " window shown");

        Assert.False(harness.IsOwnerEnabled()); // modal: WPF vô hiệu hoá owner (cùng thread)
        BridgeHarness.Close(hwnd);
        var result = await shown;

        await harness.WaitForNoOwnedWindowAsync(which + " window closed");
        Assert.True(harness.IsOwnerEnabled());
        Assert.Equal(which is "Settings" or "BatchReview" ? "False" : which == "ZoomPrompt" ? "null" : "done", result);
    }

    [Fact]
    public async Task Benchmark_IsModeless_OwnerStaysEnabled_AndClosesWithWmClose()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();

        await harness.Ui.InvokeAsync(() => port.ShowBenchmark(harness.OwnerHwnd, null)); // trả về ngay (Show)
        var hwnd = await harness.WaitForOwnedWindowAsync("benchmark shown");

        Assert.True(harness.IsOwnerEnabled());
        BridgeHarness.Close(hwnd);
        await harness.WaitForNoOwnedWindowAsync("benchmark closed");
        Assert.Empty(await harness.Ui.InvokeAsync(() => Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToList()));
    }

    [Fact]
    public async Task Benchmark_TabKey_MovesKeyboardFocus_ThroughTheShellMessageLoop()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();
        await harness.Ui.InvokeAsync(() => port.ShowBenchmark(harness.OwnerHwnd, null));
        var hwnd = await harness.WaitForOwnedWindowAsync("benchmark shown");
        var first = await harness.Ui.InvokeAsync(() =>
        {
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.IsVisible);
            window.Activate();
            var box = (UIElement)window.FindName("FolderText");
            return Keyboard.Focus(box) is not null ? Describe(Keyboard.FocusedElement) : "none";
        });

        // Bridge contract: every message the shell loop pumps reaches ComponentDispatcher while a modeless window is open.
        var seen = new List<int>();
        System.Windows.Interop.ThreadMessageEventHandler observe = (ref System.Windows.Interop.MSG m, ref bool _) => seen.Add(m.message);
        await harness.Ui.InvokeAsync(() => System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += observe);

        BridgeHarness.PostKey(hwnd, 0x09 /* VK_TAB */);
        await TestSupport.Wait.UntilAsync(() => harness.Ui.InvokeAsync(() => seen.Contains(0x0100)).GetAwaiter().GetResult(),
            "WM_KEYDOWN reached ComponentDispatcher.ThreadPreprocessMessage");
        await harness.Ui.InvokeAsync(() => System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage -= observe);
        string? second = null;
        await TestSupport.Wait.UntilAsync(() =>
        {
            second = harness.Ui.InvokeAsync(() => Describe(Keyboard.FocusedElement)).GetAwaiter().GetResult();
            return second != first;
        }, "keyboard focus moved by Tab");

        output.WriteLine($"focus {first} -> {second}");
        Assert.NotEqual(first, second);
        BridgeHarness.Close(hwnd);
        await harness.WaitForNoOwnedWindowAsync("benchmark closed");
    }

    [Fact]
    public async Task Settings_Save_RaisesStoreChanged_AndReturnsTrue()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();
        var changed = 0;
        harness.Store.Changed += (_, _) => Interlocked.Increment(ref changed);

        var shown = harness.Ui.InvokeAsync(() => port.ShowSettings(harness.OwnerHwnd, SettingsTarget.Default));
        await harness.WaitForOwnedWindowAsync("settings shown");
        await harness.Ui.InvokeAsync(() =>
        {
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.IsVisible);
            var save = window.GetType().GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            save.Invoke(window, [window, new RoutedEventArgs()]);
        });
        var accepted = await shown;

        await harness.WaitForNoOwnedWindowAsync("settings closed");
        Assert.True(accepted);
        Assert.True(Volatile.Read(ref changed) >= 1);
    }

    [Fact]
    public async Task Host_CalledFromAnotherThread_IsRejected()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => port.ShowDiagnostics(harness.OwnerHwnd)));

        Assert.Contains("UI thread", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_ClosesOpenModelessWindows_AndStopsKeyForwarding()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();
        await harness.Ui.InvokeAsync(() => port.ShowBenchmark(harness.OwnerHwnd, null));
        await harness.WaitForOwnedWindowAsync("benchmark shown");

        await harness.Ui.InvokeAsync(() => ((IDisposable)port).Dispose());

        await harness.WaitForNoOwnedWindowAsync("benchmark closed by Dispose");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Ui.InvokeAsync(() => port.ShowBenchmark(harness.OwnerHwnd, null)));
    }

    private static string Describe(IInputElement? element)
        => element is FrameworkElement fe ? $"{fe.GetType().Name}:{fe.Name}" : element?.GetType().Name ?? "none";
}

[CollectionDefinition("WpfBridge", DisableParallelization = true)]
public sealed class WpfBridgeSerial;
