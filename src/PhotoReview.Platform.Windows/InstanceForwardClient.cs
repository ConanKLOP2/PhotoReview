using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;

namespace PhotoReview.Platform.Windows;

/// <summary>Second-launch side of Q-R10: hands the paths to the running owner over its per-user pipe.</summary>
public sealed class InstanceForwardClient : IInstanceForwardClient
{
    private readonly string _pipeName;
    private readonly ILog _log;
    private readonly bool _allowServerForeground;

    /// <param name="allowServerForeground">
    /// Production only: this process was just started by the user, so it may let the owner take the foreground
    /// (AllowSetForegroundWindow for the owner's PID; the owner then simply calls Window.Activate).
    /// </param>
    public InstanceForwardClient(string pipeName, ILog? log = null, bool allowServerForeground = false)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _log = log ?? NullLog.Instance;
        _allowServerForeground = allowServerForeground;
    }

    public async Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        byte[] request;
        try { request = ForwardedPathProtocol.Encode(paths.Count > ForwardedPathProtocol.MaxPaths ? [.. paths.Take(ForwardedPathProtocol.MaxPaths)] : paths); }
        catch (ArgumentException) { return ForwardOutcome.Rejected; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // CurrentUserOnly: refuse a server pipe that is not owned by this user (name squatting).
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (pipe.ConfigureAwait(false))
        {
            var written = false;
            try
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                if (_allowServerForeground) TryAllowForeground(pipe);
                await pipe.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                written = true;

                var reply = new byte[16];
                var length = 0;
                while (length < reply.Length && Array.IndexOf(reply, (byte)'\n', 0, length) < 0)
                {
                    var read = await pipe.ReadAsync(reply.AsMemory(length), deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                // The owner hung up without answering (its read timed out): same as nobody home, not a refusal.
                if (length == 0) return ForwardOutcome.NoInstance;
                return Encoding.ASCII.GetString(reply, 0, length).StartsWith("OK", StringComparison.Ordinal)
                    ? ForwardOutcome.Delivered
                    : ForwardOutcome.Rejected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return written ? ForwardOutcome.Unknown : ForwardOutcome.NoInstance;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Forward client could not reach the running instance: {ex.GetType().Name}");
                return ForwardOutcome.NoInstance;
            }
        }
    }

    private void TryAllowForeground(NamedPipeClientStream pipe)
    {
        try
        {
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) AllowSetForegroundWindow(pid);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _log.Warn("AllowSetForegroundWindow unavailable");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
