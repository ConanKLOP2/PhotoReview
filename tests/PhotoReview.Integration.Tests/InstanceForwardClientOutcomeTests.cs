using System.IO;
using System.IO.Pipes;
using System.Text;
using PhotoReview.Core.Instance;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Fast (no Slow/Native trait) outcome matrix of <see cref="InstanceForwardClient"/> against a scripted fake owner on a real
/// per-test named pipe. The owner is created before the client connects, so no test polls or sleeps; the only waits are
/// event-driven with a generous upper bound.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InstanceForwardClientOutcomeTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly string _pipe = "PhotoReview.ClientTest." + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewFwdOut_" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _servers = [];

    public InstanceForwardClientOutcomeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(_root, recursive: true);
    }

    private string File1(string name)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, [1]);
        return p;
    }

    /// <summary>Starts a one-shot owner; <paramref name="script"/> gets the connected stream and the raw request bytes.</summary>
    private Task StartOwner(Func<NamedPipeServerStream, byte[], Task> script)
    {
        var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _servers.Add(server);
        return Task.Run(async () =>
        {
            await server.WaitForConnectionAsync().WaitAsync(Bound);
            var buffer = new byte[ForwardedPathProtocol.MaxMessageBytes + 1];
            var length = 0;
            while (length < buffer.Length && !ForwardedPathProtocol.IsComplete(buffer.AsSpan(0, length)))
            {
                var read = await server.ReadAsync(buffer.AsMemory(length)).AsTask().WaitAsync(Bound);
                if (read == 0) break;
                length += read;
            }
            try { await script(server, buffer[..length]); }
            finally { await server.DisposeAsync(); } // the owner hangs up as soon as its script ends
        });
    }

    private static Task Reply(NamedPipeServerStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    [Fact(DisplayName = "An owner answering OK yields Delivered and receives exactly the forwarded path")]
    public async Task OkReply_IsDelivered()
    {
        var file = File1("a.jpg");
        IReadOnlyList<string>? got = null;
        var owner = StartOwner(async (s, req) =>
        {
            Assert.True(ForwardedPathProtocol.TryDecode(req, _ => true, out var paths));
            got = paths;
            await Reply(s, "OK\n");
        });

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([file], Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.Equal([file], got);
    }

    [Fact(DisplayName = "An owner answering anything other than OK yields Rejected")]
    public async Task NonOkReply_IsRejected()
    {
        var owner = StartOwner((s, _) => Reply(s, "ERR\n"));

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([File1("b.jpg")], Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Rejected, outcome);
    }

    [Fact(DisplayName = "A reply that contains OK but does not start with it is Rejected")]
    public async Task ReplyNotStartingWithOk_IsRejected()
    {
        var owner = StartOwner((s, _) => Reply(s, "NOK\n"));

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([File1("c.jpg")], Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Rejected, outcome);
    }

    [Theory(DisplayName = "Paths the protocol cannot encode are Rejected locally without touching the pipe")]
    [InlineData("relative\\x.jpg")]
    [InlineData("")]
    [InlineData("C:\\a\\..\\b.jpg")]
    [InlineData("\\\\?\\C:\\x.jpg")]
    public async Task UnencodablePath_IsRejectedWithoutConnecting(string path)
    {
        // No owner is listening: a client that tried to connect would report NoInstance instead.
        var outcome = await new InstanceForwardClient(_pipe).SendAsync([path], TimeSpan.FromMilliseconds(200));

        Assert.Equal(ForwardOutcome.Rejected, outcome);
    }

    [Fact(DisplayName = "With nobody listening the client reports NoInstance")]
    public async Task NoServer_IsNoInstance()
    {
        var outcome = await new InstanceForwardClient(_pipe).SendAsync([File1("d.jpg")], TimeSpan.FromMilliseconds(200));

        Assert.Equal(ForwardOutcome.NoInstance, outcome);
    }

    [Fact(DisplayName = "An owner that reads the request and hangs up without replying yields Unknown")]
    public async Task HangupAfterRead_IsUnknown()
    {
        var owner = StartOwner((_, _) => Task.CompletedTask);

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([File1("e.jpg")], Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Unknown, outcome);
    }

    [Fact(DisplayName = "An owner that reads the request but never replies makes the client time out with Unknown")]
    public async Task NoReplyBeforeDeadline_IsUnknown()
    {
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        var owner = StartOwner(async (_, _) => { received.SetResult(); await release.Task.WaitAsync(Bound); });

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([File1("f.jpg")], TimeSpan.FromMilliseconds(300));
        await received.Task.WaitAsync(Bound); // the request really reached the owner
        release.SetResult();
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Unknown, outcome);
    }

    [Fact(DisplayName = "Caller cancellation while waiting for the reply propagates OperationCanceledException (not folded into an outcome)")]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        var owner = StartOwner(async (_, _) => { received.SetResult(); await release.Task.WaitAsync(Bound); });

        var send = new InstanceForwardClient(_pipe).SendAsync([File1("g.jpg")], Bound, cts.Token);
        await received.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        release.SetResult();
        await owner.WaitAsync(Bound);
    }

    [Fact(DisplayName = "A token cancelled before the call propagates OperationCanceledException")]
    public async Task AlreadyCancelledToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new InstanceForwardClient(_pipe).SendAsync([File1("h.jpg")], Bound, cts.Token));
    }

    [Fact(DisplayName = "More paths than the protocol limit are trimmed to the limit (in order) and still Delivered")]
    public async Task MoreThanMaxPaths_AreTrimmedAndDelivered()
    {
        var files = Enumerable.Range(0, ForwardedPathProtocol.MaxPaths + 3).Select(i => File1($"m{i:D2}.jpg")).ToList();
        IReadOnlyList<string>? got = null;
        var owner = StartOwner(async (s, req) =>
        {
            Assert.True(ForwardedPathProtocol.TryDecode(req, _ => true, out var paths));
            got = paths;
            await Reply(s, "OK\n");
        });

        var outcome = await new InstanceForwardClient(_pipe).SendAsync(files, Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.Equal(files.Take(ForwardedPathProtocol.MaxPaths), got);
    }

    [Fact(DisplayName = "allowServerForeground=true still delivers (AllowSetForegroundWindow is best effort)")]
    public async Task AllowServerForeground_StillDelivers()
    {
        var file = File1("fg.jpg");
        var owner = StartOwner((s, _) => Reply(s, "OK\n"));

        var outcome = await new InstanceForwardClient(_pipe, allowServerForeground: true).SendAsync([file], Bound);
        await owner.WaitAsync(Bound);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
    }

    [Fact(DisplayName = "A null pipe name is refused")]
    public void NullPipeName_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new InstanceForwardClient(null!));
}
