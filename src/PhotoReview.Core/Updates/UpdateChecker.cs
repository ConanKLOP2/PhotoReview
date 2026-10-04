using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhotoReview.Core.Updates;

/// <summary>GitHub "latest release" lookup. HTTPS only, 10 s timeout, sends nothing about the user's photos or paths.</summary>
public sealed class UpdateChecker : IUpdateChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/ConanKLOP2/PhotoReview/releases/latest";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A release JSON is a few KB; a hostile or broken endpoint must not make the check buffer an unbounded body.</summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Test seam; null uses the default network handler.</param>
    public UpdateChecker(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<UpdateCheckResult> CheckAsync(string? currentVersion, CancellationToken cancellationToken)
    {
        if (!AppVersion.TryParse(currentVersion, out var current)) return UpdateCheckResult.Failed(UpdateFailure.InvalidVersion);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        using var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan; // the linked source owns the timeout so it can be told apart from user cancel
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PhotoReview", "update-check"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (IsRateLimited(response)) return UpdateCheckResult.Failed(UpdateFailure.RateLimited);
            if (!response.IsSuccessStatusCode) return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            if (response.Content.Headers.ContentLength > MaxBodyBytes) return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            var body = await ReadCappedAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
            return body is null ? UpdateCheckResult.Failed(UpdateFailure.BadResponse) : Interpret(body, current);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(UpdateFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed(Classify(ex.HttpRequestError));
        }
        // Anything else from the handler/stack (e.g. InvalidOperationException) is a failed check, never a crash;
        // a user cancel (OperationCanceledException) still propagates.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
        }
    }

    /// <summary>429 is always a rate limit; 403 only when GitHub says so (<c>X-RateLimit-Remaining: 0</c> or a <c>Retry-After</c>), else it is a refusal.</summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        if (response.Headers.RetryAfter is not null) return true;
        return response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && values.Any(v => string.Equals(v.Trim(), "0", StringComparison.Ordinal));
    }

    /// <summary>No route to the server is Offline; a connection that was made but spoke wrongly (TLS or certificate failure behind an
    /// intercepting proxy, a malformed or truncated response) is a bad response, not a missing network.</summary>
    internal static UpdateFailure Classify(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError => UpdateFailure.Offline,
        HttpRequestError.SecureConnectionError or HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse
            or HttpRequestError.ResponseEnded or HttpRequestError.ConfigurationLimitExceeded or HttpRequestError.UserAuthenticationError
            or HttpRequestError.VersionNegotiationError or HttpRequestError.ExtendedConnectNotSupported => UpdateFailure.BadResponse,
        _ => UpdateFailure.Offline, // Unknown (and any value added later): the historical behavior
    };

    /// <summary>Reads at most <see cref="MaxBodyBytes"/> bytes (UTF-8); null when the body is larger.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static UpdateCheckResult Interpret(string body, AppVersion current)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            var tag = ReadString(root, "tag_name");
            if (!AppVersion.TryParse(tag, out var latest)) return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
            var draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
            var pre = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            // Drafts and pre-releases are never offered; the running version is then the newest stable one we know of.
            if (draft || pre || latest.IsPrerelease) return UpdateCheckResult.UpToDate(current.ToString());
            if (latest.CompareTo(current) <= 0) return UpdateCheckResult.UpToDate(latest.ToString());
            var url = UpdateUrlPolicy.Validate(ReadString(root, "html_url")) ?? UpdateUrlPolicy.ReleasesPageUrl;
            return UpdateCheckResult.UpdateAvailable(latest.ToString(), url);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(UpdateFailure.BadResponse);
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
