using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests.Hosting;

/// <summary>
/// Một UI thread thật của shell cho test: thread STA riêng tạo <see cref="Win32UiDispatcher"/>, cài
/// <see cref="Win32UiSynchronizationContext"/> và chạy <see cref="MessageLoop"/> tới khi <see cref="DisposeAsync"/> gửi WM_QUIT.
/// Mọi lần chờ đều có giới hạn (<see cref="Bound"/>); không có độ trễ cố định. Dùng chung cho Shell.Integration.Tests (link file).
/// </summary>
internal sealed class UiThread : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Win32UiDispatcher? _dispatcher;
    private MessageLoop? _loop;

    private UiThread(ILog? log)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ready = ready.Task;
        _thread = new Thread(() => Run(log, ready)) { IsBackground = true, Name = "PhotoReview test UI thread" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public Win32UiDispatcher Dispatcher => _dispatcher ?? throw new InvalidOperationException("UI thread not started");

    public MessageLoop Loop => _loop ?? throw new InvalidOperationException("UI thread not started");

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Kết thúc với mã thoát của vòng lặp, hoặc ngoại lệ vòng lặp ném ra.</summary>
    public Task<int> Exit => _exit.Task;

    private Task Ready { get; }

    public static async Task<UiThread> StartAsync(ILog? log = null)
    {
        var ui = new UiThread(log);
        ui._thread.Start();
        await ui.Ready.WaitAsync(Bound);
        return ui;
    }

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action, UiPriority.Normal).WaitAsync(Bound);

    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        T value = default!;
        await Dispatcher.InvokeAsync(() => value = func(), UiPriority.Normal).WaitAsync(Bound);
        return value;
    }

    /// <summary>Chạy một hàm async trên UI thread (continuation của nó quay về UI thread qua SynchronizationContext).</summary>
    public Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Post(() => _ = PumpAsync(body, done), UiPriority.Normal);
        return done.Task.WaitAsync(Bound);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_exit.Task.IsCompleted)
        {
            Dispatcher.Post(() => MessageLoop.Quit(0), UiPriority.Normal);
        }

        try
        {
            await _exit.Task.WaitAsync(Bound);
        }
        catch (Exception) when (_exit.Task.IsFaulted)
        {
            // Test kiểm ngoại lệ vòng lặp qua Exit; ở đây chỉ dọn.
        }

        Assert.True(_thread.Join(Bound), "UI thread did not exit");
    }

    private static async Task PumpAsync(Func<Task> body, TaskCompletionSource done)
    {
        try
        {
            await body();
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    private void Run(ILog? log, TaskCompletionSource ready)
    {
        Win32UiDispatcher? dispatcher = null;
        try
        {
            dispatcher = new Win32UiDispatcher(log);
            using var context = Win32UiSynchronizationContext.Install(dispatcher);
            _dispatcher = dispatcher;
            _loop = new MessageLoop(dispatcher);
            ready.TrySetResult();
            _exit.TrySetResult(_loop.Run());
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            _exit.TrySetException(ex);
        }
        finally
        {
            dispatcher?.Dispose();
        }
    }
}

/// <summary>ILog ghi lại để test kiểm việc log.</summary>
internal sealed class RecordingLog : ILog
{
    private readonly object _gate = new();
    private readonly List<(string Message, Exception? Exception)> _errors = [];

    public bool Enabled => true;

    public IReadOnlyList<(string Message, Exception? Exception)> Errors
    {
        get
        {
            lock (_gate)
            {
                return [.. _errors];
            }
        }
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
        lock (_gate)
        {
            _errors.Add((message, ex));
        }
    }
}
