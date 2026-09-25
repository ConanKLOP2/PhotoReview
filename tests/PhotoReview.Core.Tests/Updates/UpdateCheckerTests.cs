using System.Net;
using System.Text;
using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

public sealed class UpdateCheckerTests
{
    private const string TagUrl = "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static FakeHandler Json(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static string Release(string tag, string url = TagUrl, bool draft = false, bool prerelease = false) =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}}}""";

    private static Task<UpdateCheckResult> Check(FakeHandler handler, string? current = "2.0.93+abc123", TimeSpan? timeout = null, CancellationToken ct = default) =>
        new UpdateChecker(handler, timeout).CheckAsync(current, ct);

    [Fact(DisplayName = "200 with a newer tag -> UpdateAvailable with version and project URL")]
    public async Task Newer_ReturnsUpdateAvailable()
    {
        var result = await Check(Json(HttpStatusCode.OK, Release("v2.0.94")));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("2.0.94", result.LatestVersion);
        Assert.Equal(TagUrl, result.DownloadUrl);
    }

    [Theory(DisplayName = "200 with the same or an older tag -> UpToDate")]
    [InlineData("v2.0.93")]
    [InlineData("v2.0.10")]
    public async Task SameOrOlder_ReturnsUpToDate(string tag)
    {
        var result = await Check(Json(HttpStatusCode.OK, Release(tag)));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.DownloadUrl);
    }

    [Fact(DisplayName = "A newer release whose html_url is not a project URL falls back to the releases page")]
    public async Task ForeignUrl_FallsBackToReleasesPage()
    {
        var result = await Check(Json(HttpStatusCode.OK, Release("v2.0.94", url: "https://evil.example/download.exe")));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(UpdateUrlPolicy.ReleasesPageUrl, result.DownloadUrl);
    }

    [Theory(DisplayName = "Drafts and pre-releases are never offered")]
    [InlineData(true, false, "v2.0.94")]
    [InlineData(false, true, "v2.0.94")]
    [InlineData(false, false, "v2.0.94-beta.1")]
    public async Task DraftOrPrerelease_IsIgnored(bool draft, bool prerelease, string tag)
    {
        var result = await Check(Json(HttpStatusCode.OK, Release(tag, draft: draft, prerelease: prerelease)));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Theory(DisplayName = "HTTP errors map to a failure code")]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateFailure.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.BadResponse)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.BadResponse)]
    public async Task HttpError_MapsToFailure(HttpStatusCode status, UpdateFailure expected)
    {
        var result = await Check(Json(status, "{}"));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(expected, result.Failure);
    }

    [Theory(DisplayName = "Unreadable or unusable JSON -> BadResponse")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"garbage","html_url":"x"}""")]
    [InlineData("""{"tag_name":42}""")]
    public async Task BadJson_ReturnsBadResponse(string body)
    {
        var result = await Check(Json(HttpStatusCode.OK, body));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailure.BadResponse, result.Failure);
    }

    [Fact(DisplayName = "Network exception -> Offline")]
    public async Task NetworkException_ReturnsOffline()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("no network"));
        var result = await Check(handler);
        Assert.Equal(UpdateFailure.Offline, result.Failure);
    }

    [Fact(DisplayName = "A cancellation not requested by the caller (HttpClient timeout style) -> Timeout")]
    public async Task TimeoutStyleCancellation_ReturnsTimeout()
    {
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("timed out"));
        var result = await Check(handler);
        Assert.Equal(UpdateFailure.Timeout, result.Failure);
    }

    [Fact(DisplayName = "A server that never answers is cut off by the configured timeout")]
    public async Task SlowServer_TimesOut()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // released only by the checker's timeout token
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var result = await Check(handler, timeout: TimeSpan.FromMilliseconds(50));
        Assert.Equal(UpdateFailure.Timeout, result.Failure);
    }

    [Fact(DisplayName = "Caller cancellation is propagated, not reported as a timeout")]
    public async Task CallerCancel_Throws()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var pending = Check(handler, ct: cts.Token);
        await started.Task;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory(DisplayName = "Unparsable running version -> InvalidVersion without any request")]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task InvalidCurrentVersion_FailsWithoutNetwork(string? current)
    {
        var handler = Json(HttpStatusCode.OK, Release("v9.9.9"));
        var result = await Check(handler, current);
        Assert.Equal(UpdateFailure.InvalidVersion, result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact(DisplayName = "Request is a single HTTPS GET to the latest-release endpoint with a User-Agent and no user data")]
    public async Task Request_IsMinimalAndHttps()
    {
        var handler = Json(HttpStatusCode.OK, Release("v2.0.93"));
        await Check(handler, "2.0.93+abc123");
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/ConanKLOP2/PhotoReview/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.NotEmpty(request.Headers.UserAgent);
        Assert.Null(request.Content);
        Assert.Empty(request.RequestUri.Query);
    }
}
