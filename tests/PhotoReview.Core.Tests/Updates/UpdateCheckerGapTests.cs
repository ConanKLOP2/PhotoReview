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
}
