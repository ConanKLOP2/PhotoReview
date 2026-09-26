using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PhotoReview.Core.Instance;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>Q-R10: real named-pipe round trips. Each test uses a unique pipe name and disposes its server.</summary>
[Trait("Category", "Slow")]
[Trait("Category", "Native")]
public sealed class InstanceForwardPipeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] OversizedReplies = ["ERR", ""];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewFwd_" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "PhotoReview.Test." + Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _disposables = [];

    public InstanceForwardPipeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string MakeFile(string name)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, [1]);
        return p;
    }

    private (InstanceForwardServer Server, List<IReadOnlyList<string>> Received, SemaphoreSlim Signal) StartServer(TimeSpan? readTimeout = null)
    {
        var received = new List<IReadOnlyList<string>>();
        var signal = new SemaphoreSlim(0);
        _disposables.Add(signal);
        var server = new InstanceForwardServer(_pipe, p => { lock (received) received.Add(p); signal.Release(); }, readTimeout: readTimeout);
        _disposables.Add(server);
        server.Start();
        return (server, received, signal);
    }

    [Fact(DisplayName = "A second launch forwards its paths to the running instance and is acknowledged")]
    public async Task Forward_DeliversPathsToServer()
    {
        var (_, received, signal) = StartServer();
        var a = MakeFile("a.jpg");
        var b = MakeFile("b.jpg");

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([a, b], Timeout);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.True(await signal.WaitAsync(Timeout));
        lock (received)
        {
            Assert.Single(received);
            Assert.Equal([a, b], received[0]);
        }
    }

    [Fact(DisplayName = "A launch with more paths than the protocol limit is still delivered, trimmed to the limit, and the first path is kept (audit F2: only the first is ever opened)")]
    public async Task Forward_MoreThanMaxPaths_DeliversFirstMaxPathsInOrder()
    {
        var (_, received, signal) = StartServer();
        var files = Enumerable.Range(0, ForwardedPathProtocol.MaxPaths + 5).Select(i => MakeFile($"p{i:D2}.jpg")).ToList();

        var outcome = await new InstanceForwardClient(_pipe).SendAsync(files, Timeout);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.True(await signal.WaitAsync(Timeout));
        lock (received)
        {
            var only = Assert.Single(received);
            Assert.Equal(files.Take(ForwardedPathProtocol.MaxPaths), only);
        }
    }

    [Fact(DisplayName = "N concurrent launches through the pipe and the coalescer produce one open of a forwarded path")]
    public async Task Forward_ConcurrentLaunches_CoalesceToOneOpen()
    {
        var files = Enumerable.Range(0, 6).Select(i => MakeFile($"p{i}.jpg")).ToList();
        var gate = new TaskCompletionSource();
        using var delivered = new CountdownEvent(files.Count);
        var opened = new List<string?>();
        var flushed = new TaskCompletionSource();
        using var coalescer = new ForwardedOpenCoalescer(
            p => { lock (opened) opened.Add(p); flushed.TrySetResult(); }, TimeSpan.FromSeconds(1), delay: (_, ct) => gate.Task.WaitAsync(ct));
        var server = new InstanceForwardServer(_pipe, p => { coalescer.Submit(p); delivered.Signal(); });
        _disposables.Add(server);
        server.Start();

        var outcomes = await Task.WhenAll(files.Select(f => new InstanceForwardClient(_pipe).SendAsync([f], Timeout)));
        Assert.All(outcomes, o => Assert.Equal(ForwardOutcome.Delivered, o));
        Assert.True(delivered.Wait(Timeout));
        gate.SetResult();
        await flushed.Task.WithTimeout(Timeout, "coalesced open");

        lock (opened)
        {
            Assert.Single(opened);
            Assert.Contains(opened[0], files);
        }
    }

    [Fact(DisplayName = "A request naming a missing path is refused and never reaches the handler")]
    public async Task Forward_NonExistentPath_IsRejected()
    {
        var (_, received, _) = StartServer();

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([Path.Combine(_root, "missing.jpg")], Timeout);

        Assert.Equal(ForwardOutcome.Rejected, outcome);
        lock (received) Assert.Empty(received);
    }

    [Fact(DisplayName = "Raw malformed and oversized bytes are refused and the listener keeps serving")]
    public async Task Server_MalformedAndOversizedInput_IsRefusedAndSurvives()
    {
        var (_, received, signal) = StartServer();

        Assert.Equal("ERR", await RawExchangeAsync(Encoding.UTF8.GetBytes("not the protocol\n\n")));
        // The server stops reading at the cap and drops the connection, so the requester may see ERR or just a closed pipe.
        Assert.Contains(await RawExchangeAsync(new byte[ForwardedPathProtocol.MaxMessageBytes + 4096]), OversizedReplies);
        Assert.Equal("ERR", await RawExchangeAsync([0xFF, 0xFE, 0xFD, (byte)'\n', (byte)'\n']));
        lock (received) Assert.Empty(received);

        var ok = MakeFile("ok.jpg");
        Assert.Equal(ForwardOutcome.Delivered, await new InstanceForwardClient(_pipe).SendAsync([ok], Timeout));
        Assert.True(await signal.WaitAsync(Timeout));
    }

    [Fact(DisplayName = "A client that connects and stalls is timed out and does not block the next launch")]
    public async Task Server_StalledClient_TimesOut()
    {
        var (_, _, signal) = StartServer(readTimeout: TimeSpan.FromMilliseconds(300));
        using var stalled = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalled.ConnectAsync((int)Timeout.TotalMilliseconds);

        var ok = MakeFile("after-stall.jpg");
        var outcome = await new InstanceForwardClient(_pipe).SendAsync([ok], Timeout);

        Assert.Equal(ForwardOutcome.Delivered, outcome);
        Assert.True(await signal.WaitAsync(Timeout));
    }

    [Fact(DisplayName = "A busy owner that answers after the client's deadline yields Unknown, not NoInstance (Q-R12: no false error dialog)")]
    public async Task Client_OwnerAnswersTooLate_ReportsUnknown()
    {
        var release = new ManualResetEventSlim();
        var server = new InstanceForwardServer(_pipe, _ => release.Wait(Timeout));
        _disposables.Add(server);
        _disposables.Add(release);
        server.Start();

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([MakeFile("slow.jpg")], TimeSpan.FromMilliseconds(400));
        release.Set();

        Assert.Equal(ForwardOutcome.Unknown, outcome);
    }

    [Fact(DisplayName = "An owner that hangs up without answering (nothing accepted) yields NoInstance so the second launch opens its own window")]
    public async Task Client_OwnerHangsUpSilently_ReportsNoInstance()
    {
        var listening = new SemaphoreSlim(0);
        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            listening.Release();
            await server.WaitForConnectionAsync();
            var buffer = new byte[256];
            _ = await server.ReadAsync(buffer); // read the request, then close without replying
        });
        await listening.WaitAsync(Timeout);

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([MakeFile("silent.jpg")], Timeout);
        await serverTask.WaitAsync(Timeout);

        Assert.Equal(ForwardOutcome.NoInstance, outcome);
    }

    [Fact(DisplayName = "With no listening instance the client reports NoInstance within its timeout (stale mutex fallback)")]
    public async Task Client_NoServer_ReportsNoInstance()
    {
        var outcome = await new InstanceForwardClient(_pipe).SendAsync([MakeFile("x.jpg")], TimeSpan.FromMilliseconds(300));

        Assert.Equal(ForwardOutcome.NoInstance, outcome);
    }

    [Fact(DisplayName = "The pipe DACL admits only the current user (no other user, session or Everyone)")]
    public void PipeSecurity_AllowsOnlyCurrentUser()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = InstanceForwardPipe.CreateSecurity();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(rules);
        Assert.Equal(me, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact(DisplayName = "A live listening pipe carries only the current user in its DACL")]
    public void LivePipe_HasSingleUserDacl()
    {
        StartServer();
        using var probe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.None);
        probe.Connect((int)Timeout.TotalMilliseconds);
        var rules = probe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Equal(WindowsIdentity.GetCurrent().User, r.IdentityReference));
    }

    [Fact(DisplayName = "The pipe name follows the folder key and is scoped per folder")]
    public void PipeName_DerivedFromFolderKey()
    {
        Assert.Equal(InstanceForwardPipe.NameFor(@"C:\Photos"), InstanceForwardPipe.NameFor(@"c:\photos\"));
        Assert.NotEqual(InstanceForwardPipe.NameFor(@"C:\Photos"), InstanceForwardPipe.NameFor(@"C:\Photos2"));
        Assert.NotEqual(InstanceForwardPipe.NameFor(null), InstanceForwardPipe.NameFor(@"C:\Photos"));
    }

    [Fact(DisplayName = "Disposing the server frees the pipe name")]
    public async Task Dispose_StopsListening()
    {
        var (server, _, _) = StartServer();
        server.Dispose();

        var outcome = await new InstanceForwardClient(_pipe).SendAsync([], TimeSpan.FromMilliseconds(300));

        Assert.Equal(ForwardOutcome.NoInstance, outcome);
    }

    private async Task<string> RawExchangeAsync(byte[] payload)
    {
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)Timeout.TotalMilliseconds);
        try
        {
            await client.WriteAsync(payload);
            await client.FlushAsync();
        }
        catch (IOException) { /* the server may close early on oversized input; the reply below is what counts */ }
        var buf = new byte[16];
        var n = await client.ReadAsync(buf).AsTask().WaitAsync(Timeout);
        return Encoding.ASCII.GetString(buf, 0, n).Trim();
    }
}
