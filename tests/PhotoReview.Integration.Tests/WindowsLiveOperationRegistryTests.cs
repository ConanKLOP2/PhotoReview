using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R27: the Windows live-operation markers are real named kernel events. Like <see cref="InstanceScopeTests"/> (named
/// mutexes) every test uses a unique name prefix, so it never meets a running PhotoReview or another test, and disposes
/// every handle it creates (the kernel then deletes the object). Two registry instances stand for two processes: they
/// share nothing but the kernel namespace.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WindowsLiveOperationRegistryTests
{
    private readonly string _prefix = "PhotoReviewTest" + Guid.NewGuid().ToString("N");

    [Fact(DisplayName = "Q-R27: a marker begun by one registry is live for another until it is disposed")]
    public void Begin_VisibleToSecondRegistry_UntilDisposed()
    {
        var owner = new WindowsLiveOperationRegistry(prefix: _prefix);
        var other = new WindowsLiveOperationRegistry(prefix: _prefix);
        var id = Guid.NewGuid().ToString("N");

        Assert.False(other.IsLive(id));
        var marker = owner.Begin(id);
        try
        {
            Assert.True(other.IsLive(id));
            Assert.True(owner.IsLive(id)); // the owning process must not reconcile its own live operation either
            Assert.True(other.IsLive(id)); // IsLive releases the handle it opened: checking does not keep a marker alive
        }
        finally
        {
            marker.Dispose();
        }

        Assert.False(other.IsLive(id));
        Assert.False(owner.IsLive(id));
    }

    [Fact(DisplayName = "Q-R27: markers of different Ids are independent")]
    public void Begin_DifferentIds_Independent()
    {
        var registry = new WindowsLiveOperationRegistry(prefix: _prefix);
        using var a = registry.Begin("op-a");
        var b = registry.Begin("op-b");

        b.Dispose();

        Assert.True(registry.IsLive("op-a"));
        Assert.False(registry.IsLive("op-b"));
        Assert.False(registry.IsLive("op-c"));
    }

    [Fact(DisplayName = "Q-R27: a marker lives while any holder keeps it (two processes preparing the same Id)")]
    public void Begin_SameIdTwice_LiveUntilLastHolderReleases()
    {
        var first = new WindowsLiveOperationRegistry(prefix: _prefix);
        var second = new WindowsLiveOperationRegistry(prefix: _prefix);
        var one = first.Begin("same");
        using var two = second.Begin("same");

        one.Dispose();

        Assert.True(first.IsLive("same"));
    }

    [Theory(DisplayName = "Q-R27: any journal Id (separators, colons, very long, non-ASCII) maps to a valid, bounded object name")]
    [InlineData(@"C:\photos\a.jpg")]
    [InlineData("Global\\evil/name:with*odd?chars\"<>|")]
    [InlineData("ảnh-đẹp 📷")]
    [InlineData(" ")]
    public void Begin_OddIds_SanitisedAndWork(string id)
    {
        var registry = new WindowsLiveOperationRegistry(prefix: _prefix);
        var longId = id + new string('x', 400);

        foreach (var candidate in new[] { id, longId })
        {
            var name = registry.NameFor(candidate);
            Assert.StartsWith(@"Local\" + _prefix + "_LiveOp_", name, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', name[@"Local\".Length..]);
            Assert.True(name.Length < 260, name);

            var marker = registry.Begin(candidate);
            try
            {
                Assert.True(new WindowsLiveOperationRegistry(prefix: _prefix).IsLive(candidate));
            }
            finally
            {
                marker.Dispose();
            }
            Assert.False(registry.IsLive(candidate));
        }
    }

    [Fact(DisplayName = "Q-R27: names are distinct per Id and per prefix (tests never meet a real PhotoReview)")]
    public void NameFor_DistinctPerIdAndPrefix()
    {
        var registry = new WindowsLiveOperationRegistry(prefix: _prefix);
        var production = new WindowsLiveOperationRegistry();

        Assert.NotEqual(registry.NameFor("a"), registry.NameFor("b"));
        Assert.NotEqual(registry.NameFor("a"), production.NameFor("a"));
        Assert.Equal(registry.NameFor("a"), new WindowsLiveOperationRegistry(prefix: _prefix).NameFor("a"));
    }

    [Fact(DisplayName = "Q-R27: a name already taken by another kind of kernel object does not fail Begin (no marker, logged)")]
    public void Begin_NameTakenByMutex_ReturnsNoMarkerAndLogs()
    {
        var log = new RecordingLog();
        var registry = new WindowsLiveOperationRegistry(log, _prefix);
        using var squatter = new Mutex(false, registry.NameFor("taken"));

        var marker = registry.Begin("taken");
        marker.Dispose();

        Assert.Single(log.Errors);
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Errors { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
    }
}
