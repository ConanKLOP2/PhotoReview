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
        // Only the first forwarded path is ever opened by the owner (ForwardedOpenCoalescer: Explorer starts one process per
        // selected file and they all collapse into one open of the FIRST path), so trimming to MaxPaths drops nothing that
        // would have been used and a long selection is still Delivered (audit F2 2026-09-26: by design, not a lost input).
        try { request = ForwardedPathProtocol.Encode(paths.Count > ForwardedPathProtocol.MaxPaths ? [.. paths.Take(ForwardedPathProtocol.MaxPaths)] : paths); }
        catch (ArgumentException) { return ForwardOutcome.Rejected; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // CurrentUserOnly: refuse a server pipe that is not owned by this user (name squatting).
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (pipe.ConfigureAwait(false))
        {
            var written = false;
            var reply = new byte[16];
            var length = 0;
            try
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                if (_allowServerForeground) TryAllowForeground(pipe);
                await pipe.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                written = true;

                while (length < reply.Length && Array.IndexOf(reply, (byte)'\n', 0, length) < 0)
                {
                    var read = await pipe.ReadAsync(reply.AsMemory(length), deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                // An owner that accepted the request always answers OK or ERR; hanging up silently means it read nothing
                // (read timeout, oversized input, shutting down), so fall back to opening here.
                if (length == 0) return ForwardOutcome.NoInstance;
                return ClassifyReply(reply, length);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return written ? ForwardOutcome.Unknown : ForwardOutcome.NoInstance;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Forward client could not reach the running instance: {ex.GetType().Name}");
                // R08: bytes already in `reply` (e.g. just the accept marker) mean the owner took the request and
                // died/disconnected before finishing its OK/ERR ack. Reporting NoInstance there would make this
                // process open a second window behind a live owner, so treat any received byte as Unknown instead.
                return length > 0 ? ForwardOutcome.Unknown : ForwardOutcome.NoInstance;
            }
        }
    }

    /// <summary>
    /// R08: <paramref name="reply"/> may start with <see cref="InstanceForwardPipe.AcceptMarker"/>, sent as soon as
    /// the owner accepted the request, before it ran the handler or wrote the real OK/ERR text. Skip it when present,
    /// then classify what follows; a reply that stops right after the marker (owner died before finishing the ack)
    /// is Unknown, not NoInstance, so this process does not open a second window behind a live owner.
    /// </summary>
    private static ForwardOutcome ClassifyReply(byte[] reply, int length)
    {
        var marker = InstanceForwardPipe.AcceptMarker;
        var offset = length >= marker.Length && reply.AsSpan(0, marker.Length).SequenceEqual(marker) ? marker.Length : 0;
        var text = Encoding.ASCII.GetString(reply, offset, length - offset);
        if (text.StartsWith("OK", StringComparison.Ordinal)) return ForwardOutcome.Delivered;
        if (text.StartsWith("ERR", StringComparison.Ordinal)) return ForwardOutcome.Rejected;
        return offset > 0 ? ForwardOutcome.Unknown : ForwardOutcome.Rejected;
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
