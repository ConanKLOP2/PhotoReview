using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// feat(zoom) (option A for #43): on-demand full-resolution decode of the CURRENT image while the
/// viewer is zoomed. Previews are decoded to the viewport box (a 6000x4000 portrait becomes ~853x1280),
/// so a non-Fit zoom -- which is relative to original pixels, see <c>ViewerState</c> -- would otherwise
/// just magnify the preview. The preview stays on screen (already at the right original-relative size)
/// while the original decodes off the UI thread; the original then replaces it without any layout
/// change, because the element size comes from the original dimensions, not from the bitmap.
/// </summary>
/// <remarks>
/// <para>At most one original is held (the current image's) and it is released as soon as the
/// presenter navigates (<see cref="Reset"/>): a 24 MP original is ~96 MB. The decode bypasses the
/// preview RAM/disk caches for the same reason (<see cref="PreviewImageService.DecodeOriginalAsync"/>).</para>
/// <para>Fit keeps showing the preview; the held original is re-shown without a new decode if the
/// user zooms again on the same image. Previews that are already full size (Original loading mode,
/// or a source smaller than the decode box) never trigger a decode.</para>
/// <para>ADR 0005: UI-affine like the rest of the App layer -- no <c>ConfigureAwait(false)</c>; the
/// continuation after the decode runs on the UI thread and re-checks the navigation token.</para>
/// </remarks>
public sealed class ZoomDetailLoader
{
    private readonly PreviewImageService _previewService;
    private readonly GenerationClock _clock;
    private readonly Action<object, int, int> _show;

    private Target? _target;
    private IDecodedImage? _original;
    private bool _showingOriginal;
    private double? _zoom; // null = Fit
    private CancellationTokenSource? _cts;
    private Task? _pendingLoad;

    /// <param name="show">Displays a platform image with the given original dimensions (the presenter's
    /// current-image update); always called with the preview's recorded original dimensions.</param>
    public ZoomDetailLoader(PreviewImageService previewService, GenerationClock clock, Action<object, int, int> show)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _show = show ?? throw new ArgumentNullException(nameof(show));
    }

    /// <summary>The full-resolution decode currently held for the current image (null when none).</summary>
    public IDecodedImage? HeldOriginal => _original;

    /// <summary>True while the displayed image is the held original rather than the preview.</summary>
    public bool IsShowingOriginal => _showingOriginal;

    /// <summary>The in-flight original decode for the current image (null when idle). Test/measure seam.</summary>
    internal Task? PendingLoad => _pendingLoad;

    /// <summary>Raised on the UI thread right after the original replaced the preview (measure seam).</summary>
    public event Action<string>? OriginalShown;

    /// <summary>Navigation started or the presentation was cleared: drop the target, cancel a
    /// not-yet-started decode and release the held original.</summary>
    public void Reset()
    {
        CancelPending();
        _target = null;
        _original = null;
        _showingOriginal = false;
    }

    /// <summary>The viewer's zoom changed: <paramref name="zoom"/> is the original-relative zoom, or
    /// null for Fit.</summary>
    public void SetZoom(double? zoom)
    {
        _zoom = zoom;
        Update();
    }

    /// <summary>The navigation <paramref name="token"/> finished presenting <paramref name="preview"/> of
    /// <paramref name="path"/> (built from <paramref name="key"/>) as the main image.</summary>
    public void OnPreviewPresented(long token, string path, ImageCacheKey key, IDecodedImage preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Reset();
        if (key.IsOriginal || !preview.Downscaled) return; // already full resolution
        _target = new Target(token, path, key, preview.PlatformImage, preview.PixelWidth, preview.OriginalWidth, preview.OriginalHeight);
        Update();
    }

    private void Update()
    {
        var target = _target;
        if (target is null || !_clock.IsNavigationCurrent(target.Token)) return;

        if (_zoom is not { } zoom)
        {
            if (_showingOriginal)
            {
                _showingOriginal = false;
                _show(target.Preview, target.OriginalWidth, target.OriginalHeight);
            }
            return;
        }

        if (_original is not null)
        {
            if (!_showingOriginal)
            {
                _show(_original.PlatformImage, target.OriginalWidth, target.OriginalHeight);
                _showingOriginal = true;
                OriginalShown?.Invoke(target.Path);
            }
            return;
        }

        // The preview already has at least as many pixels as the screen shows: no decode needed.
        if (zoom * target.OriginalWidth <= target.PreviewPixelWidth) return;
        if (_cts is not null) return; // already decoding this target

        var cts = new CancellationTokenSource();
        _cts = cts;
        var load = LoadAsync(target, cts);
        // Only an in-flight load is kept (a completed task's state machine would pin the original).
        if (!load.IsCompleted) _pendingLoad = load;
    }

    private async Task LoadAsync(Target target, CancellationTokenSource cts)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var original = await _previewService.DecodeOriginalAsync(target.Path, target.Key, cts.Token);
            // INV-1: a navigation (or a newer target) superseded this decode -- drop the pixels.
            if (!ReferenceEquals(_target, target) || !_clock.IsNavigationCurrent(target.Token)) return;
            _original = original;
            if (AppLog.Enabled)
                AppLog.Info($"ZoomDetail original ready token={target.Token} {original.PixelWidth}x{original.PixelHeight} ms={stopwatch.ElapsedMilliseconds} path={target.Path}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Zoom detail is best effort: on any decode failure the preview simply stays on screen.
            AppLog.Error($"ZoomDetail original decode failed token={target.Token} path={target.Path}", ex);
            return;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _pendingLoad = null;
            }
            cts.Dispose();
        }
        Update();
    }

    private void CancelPending()
    {
        var cts = _cts;
        _cts = null;
        _pendingLoad = null;
        // Cancelling only drops a decode that has not started yet; LoadAsync's finally disposes it.
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the load already finished and disposed it */ }
    }

    private sealed record Target(long Token, string Path, ImageCacheKey Key, object Preview, int PreviewPixelWidth, int OriginalWidth, int OriginalHeight);
}
