using System;
using System.Threading;
using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.Core.Diagnostics;
using PhotoReview.TestSupport.Windows;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// AR04 / ADR 0005: WpfPresentationSink keeps its Dispatcher.Invoke fallback as a safety net, but
/// every time it is taken it is counted in ReviewMetrics.CrossThreadPresentCount (expected 0).
/// </summary>
[Trait("Category", "UI")]
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
        private Win32DialogGuard? _guard;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                // Installed once for this thread's whole lifetime (it runs a real Dispatcher loop);
                // disposed on this same thread right after Run() returns so the unhook happens on the
                // thread that owns the hook.
                _guard = Win32DialogGuard.InstallOnCurrentThread();
                ready.Set();
                Dispatcher.Run();
                _guard.Dispose();
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
            // A real dialog forced this thread's work to misbehave; report that ahead of whatever the
            // test body itself asserted, same precedence as StaTestHost.
            var exception = _guard?.CreateException();
            if (exception is not null) throw exception;
        }
    }
}
