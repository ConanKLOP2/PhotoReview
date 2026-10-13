using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GÄ1: há»™p thoáº¡i Win32 modal cháº¡y trong vÃ²ng láº·p lá»“ng cá»§a shell (<see cref="MessageLoop.RunNested"/>): viá»‡c dispatcher má»©c
/// cao, bá»™ lá»c phÃ­m (cá»­a sá»• WPF modeless) vÃ  WM_QUIT (bÃ i há»c #486) hoáº¡t Ä‘á»™ng Ä‘Ãºng khi há»™p thoáº¡i Ä‘ang má»Ÿ, trÃªn UI thread tháº­t.
/// </summary>
[Trait("Category", "UI")]
public sealed class NativeDialogLoopTests
{
    private sealed class CountingFilter : IMessageFilter
    {
        private int _seen;

        public int Seen => Volatile.Read(ref _seen);

        public Action? OnSeen { get; set; }

        public bool PreFilterMessage(in WindowMessage message)
        {
            if (message.Msg == WindowMessages.WmApp + 0x20)
            {
                Interlocked.Increment(ref _seen);
                OnSeen?.Invoke();
            }

            return false;
        }
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReviewMetrics());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DispatcherWorkAndMessageFilters_KeepRunningInsideTheModalLoop()
    {
        await using var ui = await UiThread.StartAsync();
        var filter = new CountingFilter();
        await ui.InvokeAsync(() => ui.Loop.AddMessageFilter(filter));
        var ranPosted = false;
        var passesBefore = 0L;
        var passesInside = 0L;
        NativeDialog? open = null;
        // The filter is the loop's own hook (this is how a modeless WPF window keeps its keyboard): when it sees the user message
        // it queues Normal-priority dispatcher work that closes the dialog.
        filter.OnSeen = () => ui.Dispatcher.Post(() =>
        {
            ranPosted = true;
            passesInside = ui.Loop.PassCount;
            open!.Close();
        }, UiPriority.Normal);

        using var provider = Services();
        // A window of this thread that is not the dialog: its messages are not claimed by IsDialogMessage and go to the filters.
        var other = await ui.InvokeAsync(() => NativeDialogTestKit.CreateOwner());
        var port = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(provider, null, d =>
        {
            open = d;
            passesBefore = ui.Loop.PassCount;
            User32.PostMessage(other, WindowMessages.WmApp + 0x20, 0, 0);
        }, ui.Loop));

        var result = await ui.InvokeAsync(() => port.PromptCustomZoom(0, 100));

        Assert.Null(result);
        Assert.Equal(1, filter.Seen);
        Assert.True(ranPosted, "Normal-priority dispatcher work posted while the dialog is open must run");
        Assert.True(passesInside > passesBefore, "the nested loop must keep taking passes of the shell loop");
        await ui.InvokeAsync(() =>
        {
            ui.Loop.RemoveMessageFilter(filter);
            NativeDialogTestKit.DestroyOwner(other);
        });
    }

    [Fact]
    public async Task WmQuit_InsideTheModalLoop_ClosesTheDialogAndEndsTheOuterLoop_WithTheSameExitCode()
    {
        var ui = await UiThread.StartAsync();
        using var provider = Services();
        var port = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(provider, null, _ => MessageLoop.Quit(5), ui.Loop));

        var result = await ui.InvokeAsync(() => port.PromptCustomZoom(0, 100));

        Assert.Null(result);
        Assert.Equal(5, await ui.Exit.WaitAsync(UiThread.Bound)); // the outer Run saw the reposted WM_QUIT
        await ui.DisposeAsync();
    }

    [Fact]
    public async Task ClosingThePort_FromAQueuedCallback_EndsTheDialogOpenedByTheLoop()
    {
        await using var ui = await UiThread.StartAsync();
        using var provider = Services();
        Win32SecondaryWindowPort? port = null;
        port = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(provider, null, _ =>
            ui.Dispatcher.Post(() => port!.Dispose(), UiPriority.Normal), ui.Loop));

        var shown = await ui.InvokeAsync(() => port.PromptCustomZoom(0, 100));

        Assert.Null(shown);
        Assert.Equal(0, port.OpenCount);
    }
}
