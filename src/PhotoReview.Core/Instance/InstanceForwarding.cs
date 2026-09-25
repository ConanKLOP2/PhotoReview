namespace PhotoReview.Core.Instance;

/// <summary>Result of handing the command-line paths of a second launch to the instance that already owns the folder.</summary>
public enum ForwardOutcome
{
    /// <summary>The running instance accepted the request; the caller should exit.</summary>
    Delivered,
    /// <summary>An instance answered but refused the request (invalid or malformed paths).</summary>
    Rejected,
    /// <summary>Nobody answered in time (stale mutex, or the owner is stuck): fall back to the old behaviour.</summary>
    NoInstance,
}

/// <summary>Client half of the Q-R10 forwarding channel (implemented over a named pipe in Platform.Windows).</summary>
public interface IInstanceForwardClient
{
    Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Server half: listens until disposed and reports validated path lists of second launches.</summary>
public interface IInstanceForwardServer : IDisposable
{
    void Start();
}
