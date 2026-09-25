using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Instance;

/// <summary>
/// Explorer starts N processes for an N-file selection; they all forward within a short burst. Requests that arrive while a
/// window is open are merged and the running instance opens the FIRST forwarded path exactly once when the window closes.
/// A request with no path (plain "bring to front") still triggers one activation.
/// </summary>
public sealed class ForwardedOpenCoalescer : IDisposable
{
    private readonly Action<string?> _open;
    private readonly TimeSpan _window;
    private readonly ILog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _pending;
    private bool _disposed;
    private string? _first;

    public ForwardedOpenCoalescer(Action<string?> open, TimeSpan window, ILog? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _window = window;
        _log = log ?? NullLog.Instance;
        _delay = delay ?? Task.Delay;
    }

    public void Submit(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var first = paths.Count > 0 ? paths[0] : null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending)
            {
                _first ??= first;
                return;
            }
            _pending = true;
            _first = first;
        }
        _ = FlushAfterWindowAsync(_cts.Token);
    }

    private async Task FlushAfterWindowAsync(CancellationToken token)
    {
        try
        {
            await _delay(_window, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Error("Forwarded-open window failed", ex);
        }
        string? first;
        lock (_gate)
        {
            first = _first;
            _first = null;
            _pending = false;
            if (_disposed) return;
        }
        try { _open(first); }
        catch (Exception ex) { _log.Error("Forwarded open failed", ex); }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _cts.Cancel();
    }
}
