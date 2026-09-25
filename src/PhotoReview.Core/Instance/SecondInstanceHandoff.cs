using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Instance;

public static class SecondInstanceHandoff
{
    /// <summary>The wait for the owner to answer before a second launch gives up (covers the owner still starting up).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>True when the running instance took over the request and this process can simply exit.</summary>
    public static async Task<bool> TryForwardAsync(
        IInstanceForwardClient client, IReadOnlyList<string> existingPaths, TimeSpan timeout, ILog? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var l = log ?? NullLog.Instance;
        ForwardOutcome outcome;
        try
        {
            outcome = await client.SendAsync(existingPaths, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            l.Warn($"Forwarding to the running instance failed: {ex.GetType().Name}");
            return false;
        }
        l.Info($"Forward to running instance: {outcome} ({existingPaths.Count} path(s))");
        return outcome == ForwardOutcome.Delivered;
    }
}
