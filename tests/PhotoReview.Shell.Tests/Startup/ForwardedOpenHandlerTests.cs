using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>Port của <c>AppForwardedOpenTests</c> (Q-R10): phần UI-thread của một lần mở chuyển tiếp.</summary>
[Trait("Category", "HotPath")]
public sealed class ForwardedOpenHandlerTests
{
    private sealed class FakeTarget : IForwardedOpenTarget
    {
        public List<string> Calls { get; } = [];

        public bool IsClosingOrClosed { get; set; }

        public bool IsMinimized { get; set; }

        public bool ActivateThrows { get; set; }

        public Func<string, Task> Open { get; set; } = _ => Task.CompletedTask;

        public void Restore() => Calls.Add("restore");

        public void Activate()
        {
            Calls.Add("activate");
            if (ActivateThrows)
            {
                throw new InvalidOperationException("window gone");
            }
        }

        public Task OpenPathAsync(string path)
        {
            Calls.Add("open:" + path);
            return Open(path);
        }
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Entries { get; } = [];

        public bool Enabled => true;

        public void Info(string message) => Entries.Add("info:" + message);

        public void Warn(string message) => Entries.Add("warn:" + message);

        public void Error(string message, Exception? ex = null) => Entries.Add("error:" + message + ":" + ex?.GetType().Name);
    }

    [Fact]
    public async Task Path_ActivatesThenOpens()
    {
        var target = new FakeTarget();
        var handler = new ForwardedOpenHandler(target, new RecordingLog());

        await handler.HandleAsync(@"C:\p\a.jpg");

        Assert.Equal(["activate", @"open:C:\p\a.jpg"], target.Calls);
    }

    [Fact]
    public async Task NoPath_OnlyBringsTheWindowToTheFront()
    {
        var target = new FakeTarget();
        var handler = new ForwardedOpenHandler(target, new RecordingLog());

        await handler.HandleAsync(null);

        Assert.Equal(["activate"], target.Calls);
    }

    [Fact]
    public async Task MinimizedWindow_IsRestoredBeforeActivation()
    {
        var target = new FakeTarget { IsMinimized = true };
        var handler = new ForwardedOpenHandler(target, new RecordingLog());

        await handler.HandleAsync(@"C:\p\a.jpg");

        Assert.Equal(["restore", "activate", @"open:C:\p\a.jpg"], target.Calls);
    }

    [Fact]
    public async Task RequestRacingTheShutdown_IsIgnoredAndNeverTouchesTheWindow()
    {
        var target = new FakeTarget { IsClosingOrClosed = true };
        var handler = new ForwardedOpenHandler(target, new RecordingLog());

        await handler.HandleAsync(@"C:\p\a.jpg");

        Assert.Empty(target.Calls);
        Assert.Equal(1, handler.IgnoredCount);
    }

    [Fact]
    public async Task WindowGoneBetweenCheckAndActivate_IsLoggedNotThrown_AndNothingIsOpened()
    {
        var target = new FakeTarget { ActivateThrows = true };
        var log = new RecordingLog();
        var handler = new ForwardedOpenHandler(target, log);

        await handler.HandleAsync(@"C:\p\a.jpg");

        Assert.Equal(["activate"], target.Calls);
        Assert.Contains(log.Entries, e => e.StartsWith("warn:Forwarded launch: could not activate", StringComparison.Ordinal));
        Assert.Equal(1, handler.IgnoredCount);
    }

    [Fact]
    public async Task FailingOpen_IsLoggedAsForwardedOpenFailed()
    {
        var target = new FakeTarget { Open = _ => Task.FromException(new IOException("boom")) };
        var log = new RecordingLog();
        var handler = new ForwardedOpenHandler(target, log);

        await handler.HandleAsync(@"C:\p\a.jpg");

        Assert.Contains("error:Forwarded open failed:IOException", log.Entries);
    }
}
