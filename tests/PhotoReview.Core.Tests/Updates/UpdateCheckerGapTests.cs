using System.Net;
using System.Text;
using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

/// <summary>RV-T12 gap: failures that must come back as a Failed result instead of an exception.</summary>
public sealed class UpdateCheckerGapTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static Task<UpdateCheckResult> Check(Handler handler) =>
        new UpdateChecker(handler).CheckAsync("2.0.93+abc", CancellationToken.None);

    [Fact]
    public async Task CheckAsync_Http403WithANonRateLimitBody_ReturnsFailedWithoutThrowing()
    {
        var result = await Check(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}", Encoding.UTF8, "application/json"),
        })));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task CheckAsync_HandlerThrowsInvalidOperationException_ReturnsFailedWithoutThrowing()
    {
        var result = await Check(new Handler(_ => throw new InvalidOperationException("handler broke")));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    private static HttpResponseMessage Forbidden(Action<HttpResponseMessage> configure)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        configure(response);
        return response;
    }

    [Fact(DisplayName = "W2CM-05: 403 with X-RateLimit-Remaining 0 is a rate limit")]
    public async Task Http403_RemainingZero_IsRateLimited()
    {
        var result = await Check(new Handler(_ => Task.FromResult(Forbidden(r => r.Headers.Add("X-RateLimit-Remaining", "0")))));

        Assert.Equal(UpdateFailure.RateLimited, result.Failure);
    }

    [Fact(DisplayName = "W2CM-05: 403 with Retry-After is a rate limit")]
    public async Task Http403_RetryAfter_IsRateLimited()
    {
        var result = await Check(new Handler(_ => Task.FromResult(Forbidden(r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30))))));

        Assert.Equal(UpdateFailure.RateLimited, result.Failure);
    }

    [Fact(DisplayName = "W2CM-05: 403 with requests remaining is not a rate limit")]
    public async Task Http403_RemainingPositive_IsBadResponse()
    {
        var result = await Check(new Handler(_ => Task.FromResult(Forbidden(r => r.Headers.Add("X-RateLimit-Remaining", "57")))));

        Assert.Equal(UpdateFailure.BadResponse, result.Failure);
    }

    [Theory(DisplayName = "W2CM-05: HttpRequestError maps to Offline only when no connection could be made")]
    [InlineData(HttpRequestError.NameResolutionError, UpdateFailure.Offline)]
    [InlineData(HttpRequestError.ConnectionError, UpdateFailure.Offline)]
    [InlineData(HttpRequestError.ProxyTunnelError, UpdateFailure.Offline)]
    [InlineData(HttpRequestError.Unknown, UpdateFailure.Offline)]
    [InlineData(HttpRequestError.SecureConnectionError, UpdateFailure.BadResponse)]
    [InlineData(HttpRequestError.InvalidResponse, UpdateFailure.BadResponse)]
    [InlineData(HttpRequestError.ResponseEnded, UpdateFailure.BadResponse)]
    public async Task HttpRequestError_IsClassified(HttpRequestError error, UpdateFailure expected)
    {
        var result = await Check(new Handler(_ => throw new HttpRequestException(error, "simulated")));

        Assert.Equal(expected, result.Failure);
    }
}
