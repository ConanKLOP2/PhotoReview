using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows.Explorer;

/// <summary>
/// Picks the Explorer window that shows a folder and reads it. Separated from the COM plumbing so the policy
/// (windows closing mid-query, several windows/tabs on one folder, cancellation, release of every wrapper) is
/// testable with fake windows.
/// </summary>
internal static class ExplorerWindowSelector
{
    /// <summary>
    /// Walks <paramref name="windows"/> lazily, releasing each one exactly once. A window whose location cannot be
    /// read (it closed or is not a folder view) is skipped. Every window on the folder is tried in turn: the first
    /// one that yields an available snapshot wins; if none does, the first failure is returned so the diagnostics
    /// show why the primary candidate failed, and only a walk without any match reports NoMatchingWindow.
    /// </summary>
    internal static ExplorerViewSnapshot Select<TWindow>(string folder, IEnumerable<TWindow> windows,
        Func<TWindow, string?> locationOf, Func<TWindow, ExplorerViewSnapshot> read, Action<TWindow> release,
        ILog log, Stopwatch? timer, CancellationToken cancellationToken)
    {
        var inspected = 0;
        ExplorerViewSnapshot? firstFailure = null;
        foreach (var window in windows)
        {
            try
            {
                inspected++;
                if (cancellationToken.IsCancellationRequested) return ExplorerOrderService.Unavailable(folder, ExplorerOrderStatus.Canceled, ExplorerReason.Canceled);
                string? location;
                try { location = locationOf(window); }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { continue; }
                catch (Exception ex) { log.Error($"Explorer window inspection failed after {inspected} window(s)", ex); continue; }
                if (!TryCanonicalizeLocation(location, out var current) || !ExplorerSnapshotValidator.SamePath(current, folder)) continue;

                ExplorerViewSnapshot snapshot;
                try { snapshot = read(window); }
                catch (OperationCanceledException) { return ExplorerOrderService.Unavailable(folder, ExplorerOrderStatus.Canceled, ExplorerReason.Canceled); }
                catch (Exception ex)
                {
                    snapshot = ExplorerOrderService.Unavailable(folder, ExplorerOrderStatus.Failed,
                        ExplorerReason.Format(ExplorerReason.NativeViewFailed, ex.GetType().Name, $"0x{ex.HResult:X8}"));
                }
                if (snapshot.Status == ExplorerOrderStatus.Available) return snapshot;
                if (snapshot.Status == ExplorerOrderStatus.Canceled) return snapshot;
                firstFailure ??= snapshot;
            }
            finally { release(window); }
        }

        if (firstFailure is not null) return firstFailure;
        var unavailable = ExplorerOrderService.Unavailable(folder, ExplorerOrderStatus.NoMatchingWindow,
            ExplorerReason.Format(ExplorerReason.NoMatchingWindow, inspected.ToString(CultureInfo.InvariantCulture)));
        log.Info($"Explorer query-complete: status={unavailable.Status}, windows={inspected}, elapsedMs={timer?.ElapsedMilliseconds ?? 0}");
        return unavailable;
    }

    /// <summary>Folder for a shell window's <c>LocationURL</c> (<c>file:///C:/x</c>, <c>file://srv/share/x</c>), or false for anything else.</summary>
    internal static bool TryCanonicalizeLocation(string? location, out string folder)
    {
        folder = string.Empty;
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        try { folder = ExplorerSnapshotValidator.CanonicalizeFolder(uri.LocalPath); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
