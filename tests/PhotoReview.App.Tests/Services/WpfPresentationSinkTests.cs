using System;
using System.Threading;
using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// AR04 / ADR 0005: WpfPresentationSink keeps its Dispatcher.Invoke fallback as a safety net, but
/// every time it is taken it is counted in ReviewMetrics.CrossThreadPresentCount (expected 0).
/// </summary>
public sealed class WpfPresentationSinkTests
{
    [Fact(DisplayName = "AR04: sink update from a non-dispatcher thread is marshalled and counted")]
    public void OffDispatcherThread_IsMarshalledAndCounted()
    {
        using var ui = new DispatcherThread();
        var metrics = new ReviewMetrics();
        var statusThread = -1;
        var sink = new WpfPresentationSink(
            onSetStatusText: _ => statusThread = Environment.CurrentManagedThreadId,
            dispatcher: ui.Dispatcher,
            metrics: metrics);

        sink.SetStatusText("x");
        sink.OnPresented("a.jpg");

        Assert.Equal(ui.ManagedThreadId, statusThread);
        Assert.Equal(2, metrics.Snapshot().CrossThreadPresentCount);
    }

    [Fact(DisplayName = "AR04: sink update on the dispatcher thread runs inline and is not counted")]
    public void OnDispatcherThread_RunsInlineAndIsNotCounted()
    {
        using var ui = new DispatcherThread();
        var metrics = new ReviewMetrics();
        var statusThread = -1;
        var sink = new WpfPresentationSink(
            onSetStatusText: _ => statusThread = Environment.CurrentManagedThreadId,
            dispatcher: ui.Dispatcher,
            metrics: metrics);

        ui.Dispatcher.Invoke(() => sink.SetStatusText("x"));

        Assert.Equal(ui.ManagedThreadId, statusThread);
        Assert.Equal(0, metrics.Snapshot().CrossThreadPresentCount);
    }

    /// <summary>A dedicated STA thread running a Dispatcher loop, shut down on dispose.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
            ManagedThreadId = _thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }
        public int ManagedThreadId { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
