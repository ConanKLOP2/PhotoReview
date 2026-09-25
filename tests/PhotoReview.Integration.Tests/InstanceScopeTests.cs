using System.IO;
using PhotoReview.Core.Instance;
using PhotoReview.Core.Model;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R18: instance mode names and the lock/pipe scope. Real named mutexes and in-process pipe servers; every test uses a
/// unique name prefix, so it never meets a running PhotoReview or another test, and disposes everything it creates.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InstanceScopeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _prefix = "PhotoReviewTest" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewScope_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public InstanceScopeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string MakeFile(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllBytes(path, [1]);
        return path;
    }

    private sealed class Received
    {
        private readonly System.Threading.Channels.Channel<IReadOnlyList<string>> _requests =
            System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<string>>();

        public void Add(IReadOnlyList<string> paths) => _requests.Writer.TryWrite(paths);

        public Task<IReadOnlyList<string>> NextAsync() =>
            _requests.Reader.ReadAsync().AsTask().WithTimeout(Timeout, "forwarded request");
    }

    private InstanceScope NewScope(InstanceMode mode, Received? received = null, Func<string, IInstanceForwardClient>? clientFactory = null)
    {
        var sink = received ?? new Received();
        var scope = new InstanceScope(mode, sink.Add, log: null, _prefix, forwardTimeout: Timeout, clientFactory);
        _disposables.Add(scope);
        return scope;
    }

    // ---- name derivation ----

    [Fact(DisplayName = "SingleWindow: one mutex/pipe pair whatever the folder, distinct from every per-folder pair")]
    public void Keys_SingleWindow_IgnoreFolder()
    {
        var app = InstanceKeys.For(InstanceMode.SingleWindow, null);

        Assert.Equal(app, InstanceKeys.For(InstanceMode.SingleWindow, @"C:\Photos"));
        Assert.Equal(app, InstanceKeys.For(InstanceMode.SingleWindow, @"D:\Other"));
        Assert.NotEqual(app.MutexName, InstanceKeys.For(InstanceMode.PerFolder, null).MutexName);
        Assert.NotEqual(app.PipeName, InstanceKeys.For(InstanceMode.PerFolder, null).PipeName);
        Assert.StartsWith("Local\\", app.MutexName, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "PerFolder: one pair per canonical folder, identical to the pre-Q-R18 names")]
    public void Keys_PerFolder_FollowFolderAndKeepLegacyNames()
    {
        var photos = InstanceKeys.For(InstanceMode.PerFolder, @"C:\Photos");

        Assert.Equal(photos, InstanceKeys.For(InstanceMode.PerFolder, @"c:\photos\"));
        Assert.NotEqual(photos.MutexName, InstanceKeys.For(InstanceMode.PerFolder, @"C:\Photos2").MutexName);
        Assert.NotEqual(photos.PipeName, InstanceKeys.For(InstanceMode.PerFolder, @"C:\Photos2").PipeName);
        Assert.Equal(InstanceKeys.MutexNameFor(@"C:\Photos"), photos.MutexName);
        Assert.Equal(InstanceForwardPipe.NameFor(@"C:\Photos"), photos.PipeName);
    }

    [Fact(DisplayName = "A name prefix changes both names (tests never touch the production names)")]
    public void Keys_Prefix_ChangesBothNames()
    {
        foreach (var mode in new[] { InstanceMode.SingleWindow, InstanceMode.PerFolder })
        {
            var production = InstanceKeys.For(mode, @"C:\Photos");
            var test = InstanceKeys.For(mode, @"C:\Photos", _prefix);
            Assert.NotEqual(production.MutexName, test.MutexName);
            Assert.NotEqual(production.PipeName, test.PipeName);
        }
    }

    // ---- SingleWindow ----

    [Fact(DisplayName = "SingleWindow: a second launch on a DIFFERENT folder is refused the lock and forwards to the running instance")]
    public async Task SingleWindow_SecondLaunchOtherFolder_Forwards()
    {
        var received = new Received();
        var owner = NewScope(InstanceMode.SingleWindow, received);
        Assert.True(owner.TryAcquire(Folder("x")));

        var y = Folder("y");
        var photo = MakeFile(y, "photo.jpg");
        var second = NewScope(InstanceMode.SingleWindow);
        Assert.False(second.TryAcquire(y));
        var outcome = await second.CreateClient(y).SendAsync([photo], Timeout);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.Equal([photo], await received.NextAsync());
    }

    [Fact(DisplayName = "SingleWindow: a second launch with no path forwards an empty request (activate the running window)")]
    public async Task SingleWindow_SecondLaunchNoPath_ForwardsActivation()
    {
        var received = new Received();
        var owner = NewScope(InstanceMode.SingleWindow, received);
        Assert.True(owner.TryAcquire(Folder("x")));

        var second = NewScope(InstanceMode.SingleWindow);
        Assert.False(second.TryAcquire(null));
        var outcome = await second.CreateClient(null).SendAsync([], Timeout);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.Empty(await received.NextAsync());
    }

    [Fact(DisplayName = "SingleWindow: opening another folder proceeds and keeps the one app lock")]
    public async Task SingleWindow_FolderSwitch_KeepsAppLock()
    {
        var owner = NewScope(InstanceMode.SingleWindow);
        var x = Folder("x");
        var y = Folder("y");
        Assert.True(owner.TryAcquire(x));

        Assert.Equal(FolderOpenDecision.Proceed, await owner.BeforeOpenAsync(y, null));
        owner.OnFolderShown(y);
        owner.AfterOpen(y, y);
        await owner.WhenReleased.WithTimeout(Timeout, "lock release");

        Assert.False(NewScope(InstanceMode.SingleWindow).TryAcquire(null));
    }

    // ---- PerFolder ----

    [Fact(DisplayName = "PerFolder: after a folder switch the NEW folder's pipe reaches this instance and the OLD folder's lock is free")]
    public async Task PerFolder_FolderSwitch_LockAndPipeFollowFolder()
    {
        var received = new Received();
        var scope = NewScope(InstanceMode.PerFolder, received);
        var x = Folder("x");
        var y = Folder("y");
        Assert.True(scope.TryAcquire(x));

        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(y, null));
        scope.OnFolderShown(y);
        scope.AfterOpen(y, y);
        await scope.WhenReleased.WithTimeout(Timeout, "lock release");

        // A launch for Y is refused the lock and its request reaches this instance.
        var launcherY = NewScope(InstanceMode.PerFolder);
        Assert.False(launcherY.TryAcquire(y));
        var photo = MakeFile(y, "p.jpg");
        Assert.Equal(ForwardOutcome.Delivered, await launcherY.CreateClient(y).SendAsync([photo], Timeout));
        Assert.Equal([photo], await received.NextAsync());

        // A launch for X now gets its own window (lock free), and X's pipe is no longer served by this instance.
        var launcherX = NewScope(InstanceMode.PerFolder);
        Assert.True(launcherX.TryAcquire(x));
        Assert.True(scope.Holds(y));
        Assert.False(scope.Holds(x));
    }

    [Fact(DisplayName = "PerFolder: switching to a folder another instance shows forwards the request there and keeps the current folder")]
    public async Task PerFolder_SwitchToFolderOwnedElsewhere_ForwardsAndKeepsCurrent()
    {
        var otherReceived = new Received();
        var other = NewScope(InstanceMode.PerFolder, otherReceived);
        var y = Folder("y");
        Assert.True(other.TryAcquire(y));

        var scope = NewScope(InstanceMode.PerFolder);
        var x = Folder("x");
        Assert.True(scope.TryAcquire(x));
        var photo = MakeFile(y, "wanted.jpg");

        var decision = await scope.BeforeOpenAsync(y, photo);

        Assert.Equal(FolderOpenDecision.ForwardedToOtherInstance, decision);
        Assert.Equal([photo], await otherReceived.NextAsync());
        Assert.True(scope.Holds(x));
        Assert.False(scope.Holds(y));
    }

    [Fact(DisplayName = "PerFolder: an owner that does not answer and keeps the folder yields OwnedByOtherInstance")]
    public async Task PerFolder_OwnerSilent_ReportsOwnedByOther()
    {
        var y = Folder("y");
        // Holds Y's mutex but serves no pipe (stuck owner).
        using var stuck = new Mutex(false, InstanceKeys.For(InstanceMode.PerFolder, y, _prefix).MutexName, out var created);
        Assert.True(created);
        var scope = NewScope(InstanceMode.PerFolder, clientFactory: _ => new FixedClient(ForwardOutcome.NoInstance));
        Assert.True(scope.TryAcquire(Folder("x")));

        Assert.Equal(FolderOpenDecision.OwnedByOtherInstance, await scope.BeforeOpenAsync(y, null));
        Assert.False(scope.Holds(y));
    }

    [Fact(DisplayName = "PerFolder race: the owner leaves the folder while the request is in flight, so the switch proceeds here")]
    public async Task PerFolder_OwnerLeavesDuringForward_ClaimsAndProceeds()
    {
        var y = Folder("y");
        var stuck = new Mutex(false, InstanceKeys.For(InstanceMode.PerFolder, y, _prefix).MutexName, out var created);
        Assert.True(created);
        // The "owner" switches away (releases Y) and never answers.
        var scope = NewScope(InstanceMode.PerFolder, clientFactory: _ => new FixedClient(ForwardOutcome.NoInstance, onSend: stuck.Dispose));
        Assert.True(scope.TryAcquire(Folder("x")));

        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(y, null));
        Assert.True(scope.Holds(y));
    }

    [Fact(DisplayName = "PerFolder: a failed open releases the claimed folder and keeps the shown one")]
    public async Task PerFolder_FailedOpen_ReleasesClaimKeepsShown()
    {
        var scope = NewScope(InstanceMode.PerFolder);
        var x = Folder("x");
        var y = Folder("y");
        Assert.True(scope.TryAcquire(x));
        scope.OnFolderShown(x);

        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(y, null));
        scope.AfterOpen(y, shownFolder: x);
        await scope.WhenReleased.WithTimeout(Timeout, "lock release");

        Assert.True(scope.Holds(x));
        Assert.False(scope.Holds(y));
        Assert.True(NewScope(InstanceMode.PerFolder).TryAcquire(y));
    }

    [Fact(DisplayName = "PerFolder: a superseded open does not drop the lock of the open that replaced it")]
    public async Task PerFolder_SupersededOpen_KeepsPendingClaim()
    {
        var scope = NewScope(InstanceMode.PerFolder);
        var x = Folder("x");
        var y = Folder("y");
        var z = Folder("z");
        Assert.True(scope.TryAcquire(x));
        scope.OnFolderShown(x);

        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(y, null));
        Assert.Equal(FolderOpenDecision.Proceed, await scope.BeforeOpenAsync(z, null));
        scope.AfterOpen(y, shownFolder: x); // Y's load was cancelled by Z's
        await scope.WhenReleased.WithTimeout(Timeout, "lock release");
        Assert.True(scope.Holds(z));
        Assert.False(scope.Holds(y));

        scope.OnFolderShown(z);
        scope.AfterOpen(z, shownFolder: z);
        await scope.WhenReleased.WithTimeout(Timeout, "lock release");
        Assert.True(scope.Holds(z));
        Assert.False(scope.Holds(x));
    }

    [Fact(DisplayName = "PerFolder: a launch for the folder an instance shows is refused the lock (it forwards instead of opening a second window)")]
    public void PerFolder_LaunchForShownFolder_IsRefused()
    {
        var x = Folder("x");
        Assert.True(NewScope(InstanceMode.PerFolder).TryAcquire(x));

        Assert.False(NewScope(InstanceMode.PerFolder).TryAcquire(x.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.True(NewScope(InstanceMode.PerFolder).TryAcquire(Folder("other")));
    }

    private sealed class FixedClient(ForwardOutcome outcome, Action? onSend = null) : IInstanceForwardClient
    {
        public Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            onSend?.Invoke();
            return Task.FromResult(outcome);
        }
    }
}
