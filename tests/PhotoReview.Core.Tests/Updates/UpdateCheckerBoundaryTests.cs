using System.Net;
using System.Text;
using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

/// <summary>Size-cap boundaries, JSON field typing and the configurable timeout of <see cref="UpdateChecker"/>.</summary>
public sealed class UpdateCheckerBoundaryTests
{
    private const string TagUrl = "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94";

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }

    /// <summary>A stream that is not seekable, so StreamContent cannot report a Content-Length.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that declares a Content-Length and records whether anything ever asked for its bytes.</summary>
    private sealed class DeclaredLengthContent(long declaredLength, byte[] data) : HttpContent
    {
        public bool BodyWasRequested { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            BodyWasRequested = true;
            return stream.WriteAsync(data, 0, data.Length);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            BodyWasRequested = true;
            return Task.FromResult<Stream>(new MemoryStream(data));
        }
    }

    /// <summary>A valid release JSON padded with trailing spaces to exactly <paramref name="totalBytes"/> bytes.</summary>
    private static byte[] PaddedRelease(int totalBytes)
    {
        var json = $$"""{"tag_name":"v2.0.94","html_url":"{{TagUrl}}","draft":false,"prerelease":false}""";
        var bytes = new byte[totalBytes];
        Array.Fill(bytes, (byte)' ');
        Encoding.UTF8.GetBytes(json).CopyTo(bytes, 0);
        return bytes;
    }

    private static Task<UpdateCheckResult> Check(Func<HttpContent> content, TimeSpan? timeout = null) =>
        new UpdateChecker(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() })), timeout)
            .CheckAsync("2.0.93", CancellationToken.None);

    [Fact(DisplayName = "A body of exactly MaxBodyBytes with a declared Content-Length is accepted")]
    public async Task ExactlyMaxBytes_WithContentLength_IsAccepted()
    {
        var result = await Check(() => new ByteArrayContent(PaddedRelease(UpdateChecker.MaxBodyBytes)));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("2.0.94", result.LatestVersion);
    }

    [Fact(DisplayName = "A body of exactly MaxBodyBytes streamed without a Content-Length is accepted")]
    public async Task ExactlyMaxBytes_WithoutContentLength_IsAccepted()
    {
        var result = await Check(() => new StreamContent(new ForwardOnlyStream(PaddedRelease(UpdateChecker.MaxBodyBytes))));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact(DisplayName = "A streamed body one byte over MaxBodyBytes is rejected as a bad response")]
    public async Task OneByteOverMax_WithoutContentLength_IsRejected()
    {
        var result = await Check(() => new StreamContent(new ForwardOnlyStream(PaddedRelease(UpdateChecker.MaxBodyBytes + 1))));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailure.BadResponse, result.Failure);
    }

    [Fact(DisplayName = "A declared Content-Length over MaxBodyBytes is rejected before the body is read")]
    public async Task DeclaredLengthOverMax_IsRejected()
    {
        // T-05: a rejection alone is also what reading the whole body and then refusing it would give; prove the body was never requested.
        var content = new DeclaredLengthContent(UpdateChecker.MaxBodyBytes + 1, PaddedRelease(UpdateChecker.MaxBodyBytes + 1));

        var result = await Check(() => content);

        Assert.Equal(UpdateFailure.BadResponse, result.Failure);
        Assert.False(content.BodyWasRequested, "the oversized body was read although its Content-Length already exceeded the cap");
    }

    [Theory(DisplayName = "A non-string html_url is ignored and the releases page is offered instead")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task NonStringHtmlUrl_FallsBackToReleasesPage(string htmlUrlJson)
    {
        var body = $$"""{"tag_name":"v2.0.94","html_url":{{htmlUrlJson}},"draft":false,"prerelease":false}""";

        var result = await Check(() => new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(UpdateUrlPolicy.ReleasesPageUrl, result.DownloadUrl);
    }

    [Fact(DisplayName = "The timeout passed to the constructor, not the 10 s default, bounds the request")]
    public async Task CustomTimeout_IsHonoured()
    {
        var checker = new UpdateChecker(new Handler(async ct =>
        {
            var never = new TaskCompletionSource<HttpResponseMessage>();
            await using var registration = ct.Register(() => never.TrySetCanceled(ct));
            return await never.Task;
        }), TimeSpan.FromMilliseconds(100));

        var check = checker.CheckAsync("2.0.93", CancellationToken.None);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await check.WaitAsync(limit.Token); // throws if the 100 ms timeout was ignored and the 10 s default is used

        Assert.Equal(UpdateFailure.Timeout, result.Failure);
    }
}
