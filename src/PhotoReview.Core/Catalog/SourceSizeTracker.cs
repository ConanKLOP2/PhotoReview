using System;
using System.Collections.Generic;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Tracks total byte size of all sources in a catalog efficiently,
/// avoiding filesystem calls when possible by using cached Length metadata.
/// </summary>
/// <remarks>
/// Thread-safe via lock. Caches total size against <see cref="ReviewCatalog.StructuralVersion"/>;
/// invalidates (and recomputes) only when membership or order (add/remove/restore/reorder)
/// changes, instead of rebuilding a HashSet of every path to check for a change on each call --
/// this runs once per preload priority version change (i.e. roughly once per navigation).
/// </remarks>
public sealed class SourceSizeTracker
{
    private readonly ReviewCatalog _catalog;
    private readonly IFileSystem _fileSystem;
    private readonly object _gate = new();
    private int _cachedVersion = -1;
    private long _cachedTotalBytes;
    private int _fsCallCount;
    private CatalogEntry[]? _observed;
    private bool _observedDirty;

    public SourceSizeTracker(ReviewCatalog catalog, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _catalog = catalog;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// R2-F-11: called (on the UI thread) with the immutable entry snapshot a preload lifetime works on. From then on
    /// <see cref="GetTotal"/> sums that snapshot and never touches the live catalog, which the UI thread keeps mutating
    /// while <see cref="GetTotal"/> runs on the preload thread (ADR 0005).
    /// </summary>
    public void Observe(CatalogEntry[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _observed = snapshot;
            _observedDirty = true;
        }
    }

    /// <summary>
    /// Returns total byte size of all sources currently in the catalog.
    /// Computes from cached entry metadata when possible, falling back to filesystem stat for uncached paths.
    /// </summary>
    public long GetTotal()
    {
        CatalogEntry[]? observed;
        lock (_gate)
        {
            // Once a preload snapshot has been observed, only that snapshot is read (thread-safe); otherwise the live
            // catalog is read, which is only valid from the thread that owns it.
            observed = _observed;
            var version = observed is null ? _catalog.StructuralVersion : -2;
            if (observed is not null ? !_observedDirty : _cachedVersion == version) return _cachedTotalBytes;

            // Unobserved (single-threaded) mode: the live catalog must be read on the calling thread, so keep the lock.
            if (observed is null)
            {
                var (total, fsCalls) = Sum(_catalog.Entries);
                _cachedTotalBytes = total;
                _fsCallCount = fsCalls;
                _cachedVersion = version;
                return total;
            }
        }

        // Observed mode: the snapshot is immutable, so the file-system stats run OUTSIDE the lock. Observe() is called on
        // the UI thread and must never wait behind stat I/O on the preload thread.
        var (sum, calls) = Sum(observed);
        lock (_gate)
        {
            // A newer snapshot may have been observed (and even cached) while this one was statting: only the result for
            // the still-current snapshot may be committed, otherwise a late older total would overwrite the newer one.
            if (ReferenceEquals(observed, _observed))
            {
                _cachedTotalBytes = sum;
                _fsCallCount = calls;
                _cachedVersion = -2;
                _observedDirty = false;
            }
        }
        return sum;
    }

    private (long Total, int FsCalls) Sum(IReadOnlyList<CatalogEntry> entries)
    {
        long total = 0;
        var fsCalls = 0;
        foreach (var entry in entries)
        {
            long size;
            if (entry.Length.HasValue)
            {
                size = entry.Length.Value;
            }
            else
            {
                try
                {
                    size = _fileSystem.GetFileStat(entry.Path)?.Length ?? 0L;
                    fsCalls++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Runs on the preload thread: a path the file system rejects (odd characters, unsupported
                    // format) counts as size 0 like an unreadable file instead of faulting the whole preload.
                    size = 0L;
                }
            }

            total += size;
        }

        return (total, fsCalls);
    }

    /// <summary>
    /// Returns the count of filesystem stat() calls in the most recent GetTotal() invocation.
    /// For diagnostics and testing.
    /// </summary>
    public int LastFsCallCount => _fsCallCount;
}
