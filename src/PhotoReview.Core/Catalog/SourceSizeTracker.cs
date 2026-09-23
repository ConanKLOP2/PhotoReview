using System;
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

    public SourceSizeTracker(ReviewCatalog catalog, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _catalog = catalog;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Returns total byte size of all sources currently in the catalog.
    /// Computes from cached entry metadata when possible, falling back to filesystem stat for uncached paths.
    /// </summary>
    public long GetTotal()
    {
        lock (_gate)
        {
            var version = _catalog.StructuralVersion;
            if (_cachedVersion == version) return _cachedTotalBytes;

            var entries = _catalog.Entries;
            _cachedTotalBytes = 0;
            _fsCallCount = 0;

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
                        _fsCallCount++;
                    }
                    catch (IOException)
                    {
                        size = 0L;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        size = 0L;
                    }
                }

                _cachedTotalBytes += size;
            }

            _cachedVersion = version;
            return _cachedTotalBytes;
        }
    }

    /// <summary>
    /// Returns the count of filesystem stat() calls in the most recent GetTotal() invocation.
    /// For diagnostics and testing.
    /// </summary>
    public int LastFsCallCount => _fsCallCount;
}
