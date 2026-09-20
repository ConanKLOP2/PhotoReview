using System;
using System.Collections.Generic;
using System.Linq;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Tracks total byte size of all sources in a catalog efficiently,
/// avoiding filesystem calls when possible by using cached Length metadata.
/// </summary>
/// <remarks>
/// Thread-safe via lock. Caches total size based on the current entry set;
/// invalidates cache when membership (add/remove/restore/reorder) changes.
/// </remarks>
public sealed class SourceSizeTracker
{
    private readonly ReviewCatalog _catalog;
    private readonly IFileSystem _fileSystem;
    private readonly object _gate = new();
    private HashSet<string> _cachedPaths = [];
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
            var entries = _catalog.Entries;
            var currentPaths = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

            if (_cachedPaths.Count == currentPaths.Count && _cachedPaths.SetEquals(currentPaths))
                return _cachedTotalBytes;

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

            _cachedPaths = currentPaths;
            return _cachedTotalBytes;
        }
    }

    /// <summary>
    /// Returns the count of filesystem stat() calls in the most recent GetTotal() invocation.
    /// For diagnostics and testing.
    /// </summary>
    public int LastFsCallCount => _fsCallCount;
}
