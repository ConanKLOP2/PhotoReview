using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;

namespace PhotoReview.Platform.Windows;

/// <summary>Result of trying to take the instance lock of a folder (or, in SingleWindow mode, of the app).</summary>
public enum InstanceClaimResult
{
    /// <summary>This scope already holds the lock.</summary>
    AlreadyHeld,
    /// <summary>The lock was free and is now held by this scope (its forward pipe is listening).</summary>
    Acquired,
    /// <summary>Another process (or another scope) holds the lock.</summary>
    OwnedElsewhere,
}

/// <summary>
/// Q-R18: owns this process's single-instance locks and forward pipes.
/// <para>
/// A lock is a named mutex HANDLE (opened without ownership, so any thread may take or release it; the name exists while
/// a handle is open, which is all the check needs). Each held lock has its own <see cref="InstanceForwardServer"/>; both
/// are released together, server first, so a new owner can create the pipe as soon as the name is free.
/// </para>
/// <para>
/// SingleWindow: every folder maps to the one app-wide key, so <see cref="BeforeOpenAsync"/> always proceeds.
/// PerFolder: opening a folder first claims its lock (while still holding the old one); once the window shows the new
/// folder, locks of folders it no longer shows and has no pending open for are released. If another process owns the
/// folder, the request is forwarded to it and this window keeps its folder.
/// </para>
/// </summary>
public sealed class InstanceScope : IFolderOwnership, IDisposable
{
    private readonly InstanceMode _mode;
    private readonly string _prefix;
    private readonly Action<IReadOnlyList<string>> _onForwardedPaths;
    private readonly ILog _log;
    private readonly Func<string, IInstanceForwardClient> _clientFactory;
    private readonly TimeSpan _forwardTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, HeldLock> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingOpens = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="onForwardedPaths">Called on a pipe thread with the validated paths of a forwarded launch.</param>
    /// <param name="namePrefix">Mutex/pipe name prefix; tests use a unique one.</param>
    /// <param name="forwardTimeout">How long a folder switch waits for the owning instance (default <see cref="SecondInstanceHandoff.DefaultTimeout"/>).</param>
    /// <param name="allowServerForeground">Let the owning instance take the foreground when a switch is forwarded to it.</param>
    public InstanceScope(
        InstanceMode mode, Action<IReadOnlyList<string>> onForwardedPaths, ILog? log = null,
        string namePrefix = InstanceKeys.DefaultPrefix, TimeSpan? forwardTimeout = null, bool allowServerForeground = false)
        : this(mode, onForwardedPaths, log, namePrefix, forwardTimeout, null, allowServerForeground)
    {
    }

    internal InstanceScope(
        InstanceMode mode, Action<IReadOnlyList<string>> onForwardedPaths, ILog? log, string namePrefix,
        TimeSpan? forwardTimeout, Func<string, IInstanceForwardClient>? clientFactory, bool allowServerForeground = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);
        _mode = mode;
        _prefix = namePrefix;
        _onForwardedPaths = onForwardedPaths ?? throw new ArgumentNullException(nameof(onForwardedPaths));
        _log = log ?? NullLog.Instance;
        _forwardTimeout = forwardTimeout ?? SecondInstanceHandoff.DefaultTimeout;
        _clientFactory = clientFactory ?? (pipe => new InstanceForwardClient(pipe, _log, allowServerForeground));
    }

    public InstanceMode Mode => _mode;

    /// <summary>Mutex/pipe names this scope uses for <paramref name="folder"/> (the app-wide pair in SingleWindow mode).</summary>
    public InstanceKeys KeysFor(string? folder) => InstanceKeys.For(_mode, folder, _prefix);

    /// <summary>Startup: takes the lock for the launch folder (or the app lock). False = another instance owns it; forward to it.</summary>
    public bool TryAcquire(string? folder) => Claim(folder) != InstanceClaimResult.OwnedElsewhere;

    /// <summary>A client for the pipe of the instance that owns <paramref name="folder"/> (second-launch hand-off).</summary>
    public IInstanceForwardClient CreateClient(string? folder) => _clientFactory(KeysFor(folder).PipeName);

    public InstanceClaimResult Claim(string? folder) => ClaimCore(KeysFor(folder), markPending: false);

    /// <summary>True while this scope holds the lock that covers <paramref name="folder"/>.</summary>
    public bool Holds(string? folder)
    {
        var key = KeysFor(folder).MutexName;
        lock (_gate) return _claims.ContainsKey(key);
    }

    public async Task<FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var keys = KeysFor(folder);
        if (ClaimCore(keys, markPending: true) != InstanceClaimResult.OwnedElsewhere) return FolderOpenDecision.Proceed;

        var target = Path.GetFullPath(initialPath ?? folder);
        var forwarded = await SecondInstanceHandoff.TryForwardAsync(
            _clientFactory(keys.PipeName), [target], _forwardTimeout, _log, cancellationToken).ConfigureAwait(false);
        if (forwarded) return FolderOpenDecision.ForwardedToOtherInstance;

        // The owner did not take the request. Its lock follows ITS folder, so it may just have moved away (or exited):
        // one more claim turns that race into a normal open here instead of a false "already open" message.
        if (ClaimCore(keys, markPending: true) != InstanceClaimResult.OwnedElsewhere) return FolderOpenDecision.Proceed;
        _log.Warn("Folder switch refused: another instance owns the folder and did not answer");
        return FolderOpenDecision.OwnedByOtherInstance;
    }

    public void OnFolderShown(string folder) => ReleaseExcept(KeysFor(folder).MutexName, finishedOpen: null);

    public void AfterOpen(string folder, string? shownFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ReleaseExcept(KeysFor(shownFolder).MutexName, finishedOpen: KeysFor(folder).MutexName);
    }

    /// <summary>Stops every forward listener but keeps the locks (shutdown: no new requests while the app flushes).</summary>
    public void StopListening()
    {
        List<HeldLock> claims;
        lock (_gate) claims = [.. _claims.Values];
        foreach (var claim in claims) claim.StopListening();
    }

    public void Dispose()
    {
        List<HeldLock> claims;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            claims = [.. _claims.Values];
            _claims.Clear();
            _pendingOpens.Clear();
        }
        foreach (var claim in claims) claim.Dispose();
    }

    private InstanceClaimResult ClaimCore(InstanceKeys keys, bool markPending)
    {
        lock (_gate)
        {
            // A folder open racing the app shutdown just proceeds; the process is going away with its locks.
            if (_disposed && markPending) return InstanceClaimResult.AlreadyHeld;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_claims.ContainsKey(keys.MutexName))
            {
                var claim = HeldLock.TryCreate(keys, _onForwardedPaths, _log);
                if (claim is null) return InstanceClaimResult.OwnedElsewhere;
                _claims.Add(keys.MutexName, claim);
                if (markPending) _pendingOpens[keys.MutexName] = _pendingOpens.GetValueOrDefault(keys.MutexName) + 1;
                return InstanceClaimResult.Acquired;
            }
            if (markPending) _pendingOpens[keys.MutexName] = _pendingOpens.GetValueOrDefault(keys.MutexName) + 1;
            return InstanceClaimResult.AlreadyHeld;
        }
    }

    private void ReleaseExcept(string keepKey, string? finishedOpen)
    {
        var released = new List<HeldLock>();
        lock (_gate)
        {
            if (_disposed) return;
            if (finishedOpen is not null && _pendingOpens.TryGetValue(finishedOpen, out var count))
            {
                if (count <= 1) _pendingOpens.Remove(finishedOpen);
                else _pendingOpens[finishedOpen] = count - 1;
            }
            foreach (var (key, claim) in _claims)
            {
                if (key == keepKey || _pendingOpens.ContainsKey(key)) continue;
                released.Add(claim);
            }
            foreach (var claim in released) _claims.Remove(claim.MutexName);
            if (released.Count == 0) return;
            // Off the caller's (UI) thread: stopping a listener waits for its loop, which needs a pool thread and must not
            // stall a folder switch while preload workers keep the pool busy. Server first, then the mutex name.
            _releases = Task.WhenAll(_releases, Task.Run(() =>
            {
                foreach (var claim in released)
                {
                    try { claim.Dispose(); }
                    catch (Exception ex) { _log.Error("Instance lock release failed", ex); }
                }
            }));
        }
    }

    private Task _releases = Task.CompletedTask;

    /// <summary>Completes when every release started so far has finished (tests; shutdown does not need it).</summary>
    internal Task WhenReleased
    {
        get { lock (_gate) return _releases; }
    }

    private sealed class HeldLock : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly InstanceForwardServer _server;

        private HeldLock(string mutexName, Mutex mutex, InstanceForwardServer server)
        {
            MutexName = mutexName;
            _mutex = mutex;
            _server = server;
        }

        public string MutexName { get; }

        public static HeldLock? TryCreate(InstanceKeys keys, Action<IReadOnlyList<string>> onPaths, ILog log)
        {
            Mutex mutex;
            bool created;
            try
            {
                mutex = new Mutex(initiallyOwned: false, keys.MutexName, out created);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                // A same-named object we may not open (another user, or not a mutex): someone else holds the name.
                log.Warn($"Instance lock unavailable: {ex.GetType().Name}");
                return null;
            }
            if (!created)
            {
                mutex.Dispose();
                return null;
            }
            var server = new InstanceForwardServer(keys.PipeName, onPaths, log);
            server.Start();
            return new HeldLock(keys.MutexName, mutex, server);
        }

        public void StopListening() => _server.Dispose();

        public void Dispose()
        {
            _server.Dispose(); // idempotent; before the name is freed so a new owner can create the pipe
            _mutex.Dispose();
        }
    }
}
