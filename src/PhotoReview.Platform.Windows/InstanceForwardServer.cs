using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;

namespace PhotoReview.Platform.Windows;

/// <summary>Names and security of the Q-R10 forwarding pipe.</summary>
public static class InstanceForwardPipe
{
    /// <summary>
    /// Pipe name for the same folder key as <see cref="InstanceLock"/>, additionally scoped by user SID and logon session
    /// (the mutex is session-local, so a pipe of another session must never be mistaken for the owner).
    /// </summary>
    public static string NameFor(string? folder) => InstanceKeys.For(InstanceMode.PerFolder, folder).PipeName;

    /// <summary>Q-R18: the pipe paired with <paramref name="mutexName"/>, scoped by user SID and logon session.</summary>
    internal static string NameForMutex(string mutexName, string prefix)
    {
        var key = $"{mutexName}|{InstanceKeys.CurrentUserSid}|{InstanceKeys.CurrentSessionId}";
        return prefix + ".Forward." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>DACL with a single entry: the current user. No other user, session or service can connect or inject a path.</summary>
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

/// <summary>
/// Owner side of Q-R10. Accepts one connection at a time, reads a size-capped, time-boxed request, validates it and
/// answers <c>OK</c> or <c>ERR</c>. Malformed input is dropped and never affects the listener loop.
/// </summary>
public sealed class InstanceForwardServer : IInstanceForwardServer
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onPaths;
    private readonly ILog _log;
    private readonly Func<string, bool> _pathExists;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _disposed;

    public InstanceForwardServer(
        string pipeName, Action<IReadOnlyList<string>> onPaths, ILog? log = null,
        Func<string, bool>? pathExists = null, TimeSpan? readTimeout = null)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _onPaths = onPaths ?? throw new ArgumentNullException(nameof(onPaths));
        _log = log ?? NullLog.Instance;
        _pathExists = pathExists ?? (p => File.Exists(p) || Directory.Exists(p));
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, InstanceForwardPipe.CreateSecurity());
                await using (pipe.ConfigureAwait(false))
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await HandleAsync(pipe, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Pipe creation can fail (name squatted, resource exhaustion) and a client can vanish mid-request.
                _log.Warn($"Instance forward listener error: {ex.GetType().Name}");
                try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_readTimeout);
        var buffer = new byte[ForwardedPathProtocol.MaxMessageBytes + 1];
        var length = 0;
        try
        {
            while (length <= ForwardedPathProtocol.MaxMessageBytes && !ForwardedPathProtocol.IsComplete(buffer.AsSpan(0, length)))
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(length, buffer.Length - length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("Instance forward request timed out");
            return;
        }

        var accepted = ForwardedPathProtocol.TryDecode(buffer.AsSpan(0, length), _pathExists, out var paths);
        if (accepted)
        {
            try { _onPaths(paths); }
            catch (Exception ex) { _log.Error("Instance forward handler failed", ex); accepted = false; }
        }
        else
        {
            _log.Warn($"Instance forward request rejected ({length} byte(s))");
        }

        try
        {
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(accepted ? "OK\n" : "ERR\n"), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The requester went away before reading the answer; the request itself was already handled.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop faults are already logged */ }
        _cts.Dispose();
    }
}
