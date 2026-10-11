using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Integration.Tests.Hosting;

/// <summary>
/// WP-19b: điểm móc <see cref="IMessageFilter"/> / <see cref="IRawMessageFilter"/> của <see cref="MessageLoop"/> (cửa sổ phụ WPF
/// modeless nhận phím qua <c>ComponentDispatcher</c> cần MSG đầy đủ: time/pt). HWND thật, message được POST vào cửa sổ shell.
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class MessageLoopFilterTests
{
    private const uint ProbeMessage = WindowMessages.WmApp + 71;

    private sealed class Recorder : IRawMessageFilter
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Msg Raw;
        public int RawCalls;
        public int PlainCalls;
        public bool HandleProbe;

        public Task Seen => _seen.Task;

        public bool PreFilterMessage(in WindowMessage message)
        {
            if (message.Msg == ProbeMessage)
            {
                PlainCalls++;
            }

            return false;
        }

        public bool PreFilterRawMessage(in Msg message)
        {
            if (message.Message != ProbeMessage)
            {
                return false;
            }

            Raw = message;
            RawCalls++;
            _seen.TrySetResult();
            return HandleProbe;
        }
    }

    private sealed class PlainRecorder : IMessageFilter
    {
        public int Calls;
        public WindowMessage Last;

        public bool PreFilterMessage(in WindowMessage message)
        {
            if (message.Msg == ProbeMessage)
            {
                Calls++;
                Last = message;
            }

            return false;
        }
    }

    private sealed class CountingHandler : IWindowMessageHandler
    {
        public int Calls;

        public bool TryHandle(in WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg == ProbeMessage)
            {
                Calls++;
            }

            return false;
        }
    }

    private static ShellWindow CreateWindow(UiThread ui)
    {
        var clock = new TimerFrameClock();
        ui.Loop.AddFrameClock(clock);
        return new ShellWindow(new ShellWindowOptions(), clock);
    }

    [Fact]
    public async Task RawFilter_ReceivesTheFullMsg_AndIsPreferredOverThePlainCallback()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var filter = new Recorder();
        await ui.InvokeAsync(() => ui.Loop.AddMessageFilter(filter));

        Assert.True(User32.PostMessage(window.Hwnd, ProbeMessage, 11, 22));
        await filter.Seen.WaitAsync(UiThread.Bound);

        Assert.Equal(window.Hwnd, filter.Raw.Hwnd);
        Assert.Equal((nint)11, filter.Raw.WParam);
        Assert.Equal((nint)22, filter.Raw.LParam);
        Assert.NotEqual(0u, filter.Raw.Time); // time của MSG (ComponentDispatcher cần), WindowMessage không mang
        Assert.Equal(1, filter.RawCalls);
        Assert.Equal(0, filter.PlainCalls);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task PlainFilter_StillReceivesWindowMessage()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var filter = new PlainRecorder();
        await ui.InvokeAsync(() => ui.Loop.AddMessageFilter(filter));

        Assert.True(User32.PostMessage(window.Hwnd, ProbeMessage, 5, 6));
        await TestSupport.Wait.UntilAsync(() => Volatile.Read(ref filter.Calls) == 1, "plain filter called");

        Assert.Equal(new WindowMessage(window.Hwnd, ProbeMessage, 5, 6), filter.Last);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task RawFilter_ReturningTrue_StopsTranslateAndDispatch()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var handler = new CountingHandler();
        await ui.InvokeAsync(() => window.AddMessageHandler(handler));
        var swallow = new Recorder { HandleProbe = true };
        await ui.InvokeAsync(() => ui.Loop.AddMessageFilter(swallow));

        Assert.True(User32.PostMessage(window.Hwnd, ProbeMessage, 0, 0));
        await swallow.Seen.WaitAsync(UiThread.Bound);
        await ui.InvokeAsync(() => { }); // một lượt dispatcher nữa: message đã được bơm xong

        Assert.Equal(0, handler.Calls);
        await ui.InvokeAsync(() => ui.Loop.RemoveMessageFilter(swallow));
        Assert.True(User32.PostMessage(window.Hwnd, ProbeMessage, 0, 0));
        await TestSupport.Wait.UntilAsync(() => Volatile.Read(ref handler.Calls) == 1, "message dispatched once the filter is removed");
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task RemoveMessageFilter_StopsCalls_AndCountTracksRegistrations()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var filter = new Recorder();

        var before = await ui.InvokeAsync(() => ui.Loop.MessageFilterCount);
        await ui.InvokeAsync(() => ui.Loop.AddMessageFilter(filter));
        var added = await ui.InvokeAsync(() => ui.Loop.MessageFilterCount);
        await ui.InvokeAsync(() => ui.Loop.RemoveMessageFilter(filter));
        var removed = await ui.InvokeAsync(() => ui.Loop.MessageFilterCount);

        Assert.Equal(before + 1, added);
        Assert.Equal(before, removed);
        await ui.InvokeAsync(window.Dispose);
    }
}
