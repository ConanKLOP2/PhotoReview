using System.IO;
using System.Threading;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Instance;
using PhotoReview.Core.Model;
using PhotoReview.Platform.Windows;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R18 lock/pipe scope, the branches <see cref="InstanceScopeTests"/> does not reach: the public constructor and default
/// client, shutdown behaviour (StopListening, failing claim disposal, an open racing the shutdown), nested pending opens and
/// a mutex name held by something that is not a mutex. Real named objects under a unique name prefix, all disposed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InstanceScopeGapTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _prefix = "PhotoReviewGap" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewScopeGap_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public InstanceScopeGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private sealed class FakeLog : ILog
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        public bool Enabled => true;
        public IReadOnlyList<string> Messages { get { lock (_gate) return [.. _messages]; } }
        public void Info(string message) { lock (_gate) _messages.Add("INFO: " + message); }
        public void Warn(string message) { lock (_gate) _messages.Add("WARN: " + message); }
        public void Error(string message, Exception? ex = null) { lock (_gate) _messages.Add($"ERROR: {message} {ex?.GetType().Name}"); }
    }

    private InstanceScope NewScope(InstanceMode mode, ILog? log = null, Action<IReadOnlyList<string>>? onPaths = null)
    {
        var scope = new InstanceScope(mode, onPaths ?? (_ => { }), log, _prefix, forwardTimeout: Timeout, clientFactory: null);
        _disposables.Add(scope);
        return scope;
    }

    [Fact]
    public async Task PublicConstructor_UsesTheGivenModeAndPrefixAndTheRealForwardClient()
    {
        var folder = Folder("pub");
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new InstanceScope(InstanceMode.PerFolder, received.SetResult, log: null, namePrefix: _prefix);
        _disposables.Add(scope);

        Assert.Equal(InstanceMode.PerFolder, scope.Mode);
        Assert.StartsWith(_prefix, scope.KeysFor(folder).PipeName, StringComparison.Ordinal);
        Assert.True(scope.TryAcquire(folder));

        var client = scope.CreateClient(folder);
        Assert.IsType<InstanceForwardClient>(client); // the default factory talks to the real pipe of this scope
        Assert.Equal(ForwardOutcome.Delivered, await client.SendAsync([folder], Timeout));
        Assert.Equal([folder], await received.Task.WithTimeout(Timeout, "the forwarded path"));
    }

    [Fact]
    public void Constructor_BlankPrefixOrNullHandler_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InstanceScope(InstanceMode.PerFolder, _ => { }, namePrefix: " "));
        Assert.Throws<ArgumentNullException>(() => new InstanceScope(InstanceMode.PerFolder, null!, namePrefix: _prefix));
    }

    [Fact]
    public async Task StopListening_KeepsTheLockButStopsAnsweringTheForwardPipe()
    {
        var folder = Folder("stop");
        using var other = new InstanceScope(InstanceMode.PerFolder, _ => { }, log: null, namePrefix: _prefix);
        var scope = NewScope(InstanceMode.PerFolder);
        Assert.Equal(InstanceClaimResult.Acquired, scope.Claim(folder));
        Assert.Equal(ForwardOutcome.Delivered, await other.CreateClient(folder).SendAsync([folder], Timeout));

        scope.StopListening();

        Assert.True(scope.Holds(folder)); // still the owner: nobody else may open this folder while the app shuts down
        Assert.Equal(InstanceClaimResult.AlreadyHeld, scope.Claim(folder));
        var outcome = await other.CreateClient(folder).SendAsync([folder], TimeSpan.FromMilliseconds(500));
        // The listener is gone, so no pipe instance exists: the client's bounded connect (the timeout above, not a sleep) ends
        // with nothing written -> NoInstance. Rejected/Unknown would mean a server still answered or accepted the request.
        Assert.Equal(ForwardOutcome.NoInstance, outcome);
        Assert.False(other.TryAcquire(folder)); // and the name is still taken
    }

    [Fact]
    public async Task BeforeOpenAsync_AfterDispose_JustProceedsAndHoldsNothing()
    {
        var folder = Folder("late");
        var scope = NewScope(InstanceMode.PerFolder);
        scope.Dispose();

        // A folder open racing the shutdown: the process is going away with its locks, so it neither throws nor forwards.
        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(folder, null));
        scope.OnFolderShown(folder); // and the bookkeeping calls are no-ops
        scope.AfterOpen(folder, folder);
        Assert.False(scope.Holds(folder));
        Assert.Throws<ObjectDisposedException>(() => scope.Claim(folder));
        scope.Dispose(); // idempotent
    }

    [Fact]
    public void Dispose_OneClaimFailsToDispose_StillFreesTheOthersAndLogsIt()
    {
        var failing = Folder("failing");
        var healthy = Folder("healthy");
        var log = new FakeLog();
        var scope = NewScope(InstanceMode.PerFolder, log);
        Assert.Equal(InstanceClaimResult.Acquired, scope.Claim(failing));
        Assert.Equal(InstanceClaimResult.Acquired, scope.Claim(healthy));
        var failingName = scope.KeysFor(failing).MutexName;
        scope.ClaimDisposeHook = name => { if (name == failingName) throw new InvalidOperationException("dispose failed"); };

        scope.Dispose();

        Assert.Contains(log.Messages, m => m == "WARN: Instance claim dispose failed: InvalidOperationException");
        using var next = new InstanceScope(InstanceMode.PerFolder, _ => { }, log: null, namePrefix: _prefix);
        Assert.True(next.TryAcquire(healthy)); // the claim after the failing one was still released
    }

    [Fact]
    public async Task Release_ClaimFailsToDispose_IsLoggedAndTheOtherReleasesStillRun()
    {
        var shown = Folder("shown");
        var failing = Folder("failing");
        var healthy = Folder("healthy");
        var log = new FakeLog();
        var scope = NewScope(InstanceMode.PerFolder, log);
        scope.Claim(shown);
        scope.Claim(failing);
        scope.Claim(healthy);
        var failingName = scope.KeysFor(failing).MutexName;
        scope.ClaimDisposeHook = name => { if (name == failingName) throw new InvalidOperationException("release failed"); };

        scope.OnFolderShown(shown); // releases every claim but the shown folder's
        await scope.WhenReleased.WithTimeout(Timeout, "the releases");

        Assert.Contains(log.Messages, m => m == "ERROR: Instance lock release failed InvalidOperationException");
        Assert.True(scope.Holds(shown));
        Assert.False(scope.Holds(failing));
        Assert.False(scope.Holds(healthy));
        using var next = new InstanceScope(InstanceMode.PerFolder, _ => { }, log: null, namePrefix: _prefix);
        Assert.True(next.TryAcquire(healthy));
    }

    [Fact]
    public async Task PendingOpens_AreCountedPerFolder_AndOnlyTheLastFinishReleasesTheClaim()
    {
        var shown = Folder("shown");
        var target = Folder("target");
        var scope = NewScope(InstanceMode.PerFolder);
        scope.Claim(shown);
        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(target, null));
        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(target, null)); // a second open of the same folder is in flight

        scope.AfterOpen(target, shown); // the first finishes (say, superseded): the second still needs the claim
        await scope.WhenReleased.WithTimeout(Timeout, "no release");
        Assert.True(scope.Holds(target));

        scope.AfterOpen(target, shown); // the last one finishes with another folder shown: the claim goes
        await scope.WhenReleased.WithTimeout(Timeout, "the release");
        Assert.False(scope.Holds(target));
        Assert.True(scope.Holds(shown));
    }

    [Fact]
    public void Claim_NameHeldByANonMutexObject_IsReportedAsOwnedElsewhere()
    {
        var folder = Folder("squatted");
        var log = new FakeLog();
        var scope = NewScope(InstanceMode.PerFolder, log);
        // A different kind of kernel object under the mutex's name: opening it as a mutex fails, which means "someone else holds the name".
        using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, scope.KeysFor(folder).MutexName);

        Assert.Equal(InstanceClaimResult.OwnedElsewhere, scope.Claim(folder));

        Assert.False(scope.Holds(folder));
        Assert.Contains(log.Messages, m => m == "WARN: Instance lock unavailable: WaitHandleCannotBeOpenedException");
    }
}
