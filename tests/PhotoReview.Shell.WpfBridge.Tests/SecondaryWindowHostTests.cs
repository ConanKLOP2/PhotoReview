using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-19b: hợp đồng của host (<see cref="SecondaryWindowHostFactory"/>) mà <c>WpfBridgePort</c> (test) bọc: trạng thái modeless cho
/// bộ lọc phím, Application WPF lười không tắt khi đóng cửa sổ cuối, dispose gỡ bộ lọc khỏi vòng lặp.
/// </summary>
[Trait("Category", "UI")]
[Collection("WpfBridge")]
public sealed class SecondaryWindowHostTests
{
    [Fact]
    public async Task HasModelessWindows_IsTrueWhileTheBenchmarkWindowIsOpen_AndFalseAfterItCloses()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(harness.Services));
        var pump = Assert.IsAssignableFrom<ISecondaryWindowMessagePump>(host);
        Assert.False(await harness.Ui.InvokeAsync(() => pump.HasModelessWindows));

        await harness.Ui.InvokeAsync(() => host.ShowBenchmark(harness.OwnerHwnd, null));
        var hwnd = await harness.WaitForOwnedWindowAsync("benchmark shown");
        var whileOpen = await harness.Ui.InvokeAsync(() => pump.HasModelessWindows);
        BridgeHarness.Close(hwnd);
        await harness.WaitForNoOwnedWindowAsync("benchmark closed");

        Assert.True(whileOpen);
        Assert.False(await harness.Ui.InvokeAsync(() => pump.HasModelessWindows));
        await harness.Ui.InvokeAsync(() => ((IDisposable)host).Dispose());
    }

    [Fact]
    public async Task TwoBenchmarkWindows_BothCount_AndTheCountReturnsToZero()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(harness.Services));
        var pump = (ISecondaryWindowMessagePump)host;

        await harness.Ui.InvokeAsync(() =>
        {
            host.ShowBenchmark(harness.OwnerHwnd, null);
            host.ShowBenchmark(harness.OwnerHwnd, null);
        });
        await TestSupport.Wait.UntilAsync(() => harness.OwnedVisibleWindows().Count == 2, "two benchmark windows");
        foreach (var hwnd in harness.OwnedVisibleWindows())
        {
            BridgeHarness.Close(hwnd);
        }

        await harness.WaitForNoOwnedWindowAsync("both closed");
        Assert.False(await harness.Ui.InvokeAsync(() => pump.HasModelessWindows));
        await harness.Ui.InvokeAsync(() => ((IDisposable)host).Dispose());
    }

    [Fact]
    public async Task PreFilterMessage_WithNoModelessWindow_DoesNotConsumeAnything()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(harness.Services));
        var pump = (ISecondaryWindowMessagePump)host;

        var handled = await harness.Ui.InvokeAsync(() => pump.PreFilterMessage(harness.OwnerHwnd, 0x0100, 0x09, 1, 0, 0, 0));

        Assert.False(handled);
        await harness.Ui.InvokeAsync(() => ((IDisposable)host).Dispose());
    }

    [Fact]
    public async Task Application_IsKeptAliveAcrossWindows_SoASecondWindowOpensAfterTheFirstClosed()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var port = await harness.LoadPortAsync();

        for (var i = 0; i < 2; i++)
        {
            var shown = harness.Ui.InvokeAsync(() => port.ShowDiagnostics(harness.OwnerHwnd));
            BridgeHarness.Close(await harness.WaitForOwnedWindowAsync("diagnostics shown #" + i));
            await shown;
            await harness.WaitForNoOwnedWindowAsync("diagnostics closed #" + i);
        }

        // OnLastWindowClose would have shut the Application down after the first window.
        Assert.False(await harness.Ui.InvokeAsync(() => Application.Current.ShutdownMode == ShutdownMode.OnLastWindowClose));
    }

    [Fact]
    public async Task Dispose_RemovesTheKeyFilterFromTheMessageLoop()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var before = await harness.Ui.InvokeAsync(() => harness.Ui.Loop.MessageFilterCount);

        var port = await harness.LoadPortAsync();
        var whileLoaded = await harness.Ui.InvokeAsync(() => harness.Ui.Loop.MessageFilterCount);
        await harness.Ui.InvokeAsync(() => ((IDisposable)port).Dispose());
        var afterDispose = await harness.Ui.InvokeAsync(() => harness.Ui.Loop.MessageFilterCount);

        Assert.Equal(before + 1, whileLoaded);
        Assert.Equal(before, afterDispose);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(harness.Services));

        await harness.Ui.InvokeAsync(() =>
        {
            ((IDisposable)host).Dispose();
            ((IDisposable)host).Dispose();
        });

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Ui.InvokeAsync(() => host.ShowDiagnostics(harness.OwnerHwnd)));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task ShowSettings_WithoutASettingsStore_ReturnsFalseWithoutOpeningAWindow()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var empty = new ServiceCollection().BuildServiceProvider();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(empty));

        var shown = await harness.Ui.InvokeAsync(() => host.ShowSettings(harness.OwnerHwnd, SettingsTarget.Default));

        Assert.False(shown);
        Assert.Empty(harness.OwnedVisibleWindows());
        await harness.Ui.InvokeAsync(() => ((IDisposable)host).Dispose());
    }

    [Fact]
    public async Task ShowRecovery_WithoutAJournal_OpensNothing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var empty = new ServiceCollection().BuildServiceProvider();
        var host = await harness.Ui.InvokeAsync(() => SecondaryWindowHostFactory.Create(empty));

        await harness.Ui.InvokeAsync(() => host.ShowRecovery(harness.OwnerHwnd));

        Assert.Empty(harness.OwnedVisibleWindows());
        await harness.Ui.InvokeAsync(() => ((IDisposable)host).Dispose());
    }

    [Fact]
    public async Task ModalWindowWithAnInvalidOwnerHandle_OpensOwnerless_InsteadOfFailing()
    {
        await using var harness = await BridgeHarness.StartAsync();
        var bridge = await harness.LoadPortAsync();

        var shown = harness.Ui.InvokeAsync(() => bridge.PromptCustomZoom(0x7FFFFF, 100)); // a handle that is not a window
        await TestSupport.Wait.UntilAsync(() => harness.TopLevelVisibleWindows().Count > 0, "zoom dialog shown");
        BridgeHarness.Close(harness.TopLevelVisibleWindows()[0]);

        Assert.Null(await shown);
    }
}
