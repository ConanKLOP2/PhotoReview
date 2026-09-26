using System.Security.Cryptography;
using System.Text;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Q-R27 (decision D): one named kernel event per executing journal operation, visible to every PhotoReview process of
/// this user in this logon session (the processes that share <c>operations.jsonl</c>). <see cref="Begin"/> creates the
/// event and holds its handle; <see cref="IsLive"/> succeeds while any process still holds one. Windows closes a dead
/// process's handles, so a crashed operation's marker disappears and its Prepared leftover is reconciled as before.
/// <para>Name: <c>Local\&lt;prefix&gt;_LiveOp_&lt;SHA-256 hex of "liveop|user SID|id"&gt;</c>, the same shape as the
/// <see cref="InstanceKeys"/> mutex names: session-local, per user, fixed length and free of characters a kernel object
/// name cannot hold whatever the journal Id contains. An event (not a mutex) has no thread affinity, so the handle may be
/// created on one pool thread and released on another.</para>
/// </summary>
public sealed class WindowsLiveOperationRegistry : ILiveOperationRegistry
{
    private readonly string _prefix;
    private readonly ILog _log;

    /// <param name="prefix">Name prefix; tests pass a unique one so they never meet a running PhotoReview.</param>
    public WindowsLiveOperationRegistry(ILog? log = null, string prefix = InstanceKeys.DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
        _log = log ?? NullLog.Instance;
    }

    internal string NameFor(string operationId) =>
        "Local\\" + _prefix + "_LiveOp_"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("liveop|" + InstanceKeys.CurrentUserSid + "|" + operationId)));

    public IDisposable Begin(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        try
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, NameFor(operationId), out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            // Never fail the file action for its marker: without one, reconcile simply behaves as before Q-R27.
            _log.Error("Live-operation marker could not be created for journal operation " + operationId, ex);
            return NoMarker.Instance;
        }
    }

    public bool IsLive(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(NameFor(operationId), out var handle)) return false;
            handle.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // the object exists, it is only not ours to open: still a live operation's marker
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            _log.Error("Live-operation marker could not be checked for journal operation " + operationId, ex);
            return false;
        }
    }

    private sealed class NoMarker : IDisposable
    {
        public static readonly NoMarker Instance = new();

        public void Dispose() { }
    }
}
