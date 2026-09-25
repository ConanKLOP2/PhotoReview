using System.Text;
using PhotoReview.Core.Instance;

namespace PhotoReview.Core.Tests.Instance;

public sealed class ForwardingCoreTests
{
    private static readonly string Photo = Path.Combine(Path.GetTempPath(), "fwd", "a.jpg");
    private static readonly string Photo2 = Path.Combine(Path.GetTempPath(), "fwd", "b.jpg");
    private static bool Exists(string p) => p == Photo || p == Photo2;

    private static byte[] Raw(string text) => new UTF8Encoding(false).GetBytes(text);

    [Fact(DisplayName = "Encoded paths round-trip through the strict decoder")]
    public void Protocol_RoundTrip()
    {
        var ok = ForwardedPathProtocol.TryDecode(ForwardedPathProtocol.Encode([Photo, Photo2]), Exists, out var paths);

        Assert.True(ok);
        Assert.Equal([Photo, Photo2], paths);
    }

    [Fact(DisplayName = "An empty path list is a valid bring-to-front request")]
    public void Protocol_EmptyList_IsValid()
    {
        Assert.True(ForwardedPathProtocol.TryDecode(ForwardedPathProtocol.Encode([]), Exists, out var paths));
        Assert.Empty(paths);
    }

    [Theory(DisplayName = "Malformed, hostile or non-existent requests are rejected")]
    [InlineData("garbage\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 2\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nrelative\\a.jpg\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\n\\\\?\\C:\\x.jpg\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\n\\\\.\\pipe\\x\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\does\\not\\exist.jpg\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\a\tb.jpg\n\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\a.jpg")] // missing terminator
    public void Protocol_RejectsBadInput(string text)
    {
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(text), Exists, out var paths));
        Assert.Empty(paths);
    }

    [Fact(DisplayName = "Invalid UTF-8, oversized and over-long lists are rejected")]
    public void Protocol_RejectsInvalidUtf8_Oversize_TooMany()
    {
        Assert.False(ForwardedPathProtocol.TryDecode([0xFF, 0xFE, (byte)'\n', (byte)'\n'], Exists, out _));
        Assert.False(ForwardedPathProtocol.TryDecode(new byte[ForwardedPathProtocol.MaxMessageBytes + 1], Exists, out _));

        var many = "PHOTOREVIEW-OPEN 1\n" + string.Concat(Enumerable.Repeat(Photo + "\n", ForwardedPathProtocol.MaxPaths + 1)) + "\n";
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(many), Exists, out _));
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode(Enumerable.Repeat(Photo, ForwardedPathProtocol.MaxPaths + 1).ToList()));
    }

    [Fact(DisplayName = "N near-simultaneous forwards open the first path exactly once")]
    public async Task Coalescer_BurstOfLaunches_OpensFirstPathOnce()
    {
        var gate = new TaskCompletionSource();
        var opened = new List<string?>();
        var flushed = new TaskCompletionSource();
        using var coalescer = new ForwardedOpenCoalescer(
            p => { lock (opened) opened.Add(p); flushed.TrySetResult(); }, TimeSpan.FromSeconds(1),
            delay: (_, ct) => gate.Task.WaitAsync(ct));

        coalescer.Submit([Photo]);
        coalescer.Submit([Photo2]);
        coalescer.Submit([Photo2]);
        coalescer.Submit([]);
        lock (opened) Assert.Empty(opened); // still inside the window
        gate.SetResult();
        await flushed.Task.WithTimeout(TimeSpan.FromSeconds(10), "coalesced open");

        lock (opened) Assert.Equal([Photo], opened);
    }

    [Fact(DisplayName = "A later burst opens again; a path-less request still activates; a path in the same window is adopted")]
    public async Task Coalescer_SeparateBursts_AreSeparateOpens()
    {
        var release = new Queue<TaskCompletionSource>();
        var opens = new List<string?>();
        using var signal = new SemaphoreSlim(0);
        using var coalescer = new ForwardedOpenCoalescer(
            p => { lock (opens) opens.Add(p); signal.Release(); }, TimeSpan.FromSeconds(1),
            delay: (_, ct) =>
            {
                var tcs = new TaskCompletionSource();
                lock (release) release.Enqueue(tcs);
                return tcs.Task.WaitAsync(ct);
            });

        coalescer.Submit([]);
        coalescer.Submit([Photo]);
        lock (release) release.Dequeue().SetResult();
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));
        coalescer.Submit([]);
        lock (release) release.Dequeue().SetResult();
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));

        lock (opens) Assert.Equal([Photo, (string?)null], opens);
    }

    [Fact(DisplayName = "Disposing the coalescer cancels a pending open")]
    public async Task Coalescer_Dispose_CancelsPendingOpen()
    {
        var opened = 0;
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        var coalescer = new ForwardedOpenCoalescer(
            _ => Interlocked.Increment(ref opened), TimeSpan.FromSeconds(1),
            delay: async (_, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            });

        coalescer.Submit([Photo]);
        await started.Task.WithTimeout(TimeSpan.FromSeconds(10), "window started");
        coalescer.Dispose();
        await cancelled.Task.WithTimeout(TimeSpan.FromSeconds(10), "window cancelled");

        Assert.Equal(0, opened);
    }

    private sealed class FakeClient(ForwardOutcome outcome) : IInstanceForwardClient
    {
        public List<IReadOnlyList<string>> Sent { get; } = [];
        public Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Sent.Add(paths);
            return Task.FromResult(outcome);
        }
    }

    [Theory(DisplayName = "A second launch exits only when the running instance took the request")]
    [InlineData(ForwardOutcome.Delivered, true)]
    [InlineData(ForwardOutcome.Rejected, false)]
    [InlineData(ForwardOutcome.NoInstance, false)]
    public async Task Handoff_ExitsOnlyWhenDelivered(ForwardOutcome outcome, bool expected)
    {
        var client = new FakeClient(outcome);

        var result = await SecondInstanceHandoff.TryForwardAsync(client, [Photo], TimeSpan.FromSeconds(1));

        Assert.Equal(expected, result);
        Assert.Single(client.Sent);
        Assert.Equal([Photo], client.Sent[0]);
    }

    [Fact(DisplayName = "A throwing client falls back to the old behaviour instead of crashing startup")]
    public async Task Handoff_ClientThrows_FallsBack()
    {
        var result = await SecondInstanceHandoff.TryForwardAsync(new ThrowingClient(), [Photo], TimeSpan.FromSeconds(1));

        Assert.False(result);
    }

    private sealed class ThrowingClient : IInstanceForwardClient
    {
        public Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
