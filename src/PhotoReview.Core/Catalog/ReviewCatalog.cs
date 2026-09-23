using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Manages an ordered collection of image entries and tracks the current review position.
/// </summary>
/// <remarks>
/// This class is not thread-safe and must only be accessed from the UI thread.
/// </remarks>
public sealed class ReviewCatalog
{
    private readonly List<CatalogEntry> _entries = [];

    // Perf: IndexOf/Find run once per navigation (ImagePresenter, FileActionController) and
    // must not be O(n) per call. The index is rebuilt lazily on the next lookup after any
    // structural change (add/remove/reorder) instead of being kept incrementally in sync,
    // which is simpler to keep correct and still turns "many lookups, few mutations" into
    // O(1) lookups paid for by an occasional O(n) rebuild.
    private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexDirty = true;
    private string[]? _pathsCache;

    /// <summary>
    /// Bumped whenever membership or order changes (Reset/Remove/Restore/ReplaceOrder/
    /// InsertSorted/MoveToFront). Lets callers that derive an expensive, whole-catalog cache
    /// (compare-pair index, total source bytes) cheaply detect "nothing changed since I last
    /// built this" instead of recomputing on every navigation or rebuilding a HashSet of paths
    /// to compare membership.
    /// </summary>
    public int StructuralVersion { get; private set; }

    /// <summary>
    /// Gets the number of items currently in the catalog.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets the zero-based index of the current item, or -1 if the catalog is empty.
    /// </summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>
    /// Gets the current entry, or null if the catalog is empty.
    /// </summary>
    public CatalogEntry? Current => CurrentIndex >= 0 && CurrentIndex < _entries.Count ? _entries[CurrentIndex] : null;

    /// <summary>
    /// Gets a read-only list of paths of all items currently in the catalog. Cached array,
    /// rebuilt only after a structural change -- callers that need a single path should use
    /// <see cref="PathAt"/> instead of indexing into this.
    /// </summary>
    public IReadOnlyList<string> Paths => _pathsCache ??= _entries.Select(e => e.Path).ToArray();

    /// <summary>
    /// Gets all catalog entries, for efficient access to metadata (Length, LastWriteUtc).
    /// </summary>
    public IReadOnlyList<CatalogEntry> Entries => _entries.AsReadOnly();

    /// <summary>Returns the path at <paramref name="index"/> without allocating the full Paths array.</summary>
    public string PathAt(int index) => _entries[index].Path;

    /// <summary>
    /// Finds the zero-based index of the entry with the specified path, using ordinal case-insensitive comparison.
    /// Returns -1 if not found.
    /// </summary>
    public int IndexOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        EnsureIndex();
        return _indexByPath.TryGetValue(path, out var index) ? index : -1;
    }

    private void EnsureIndex()
    {
        if (!_indexDirty) return;
        _indexByPath.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            // First occurrence wins for unusual case-variant duplicate paths, matching the
            // previous linear-scan behavior (first match).
            _indexByPath.TryAdd(_entries[i].Path, i);
        }
        _indexDirty = false;
    }

    private void InvalidateIndex()
    {
        _indexDirty = true;
        _pathsCache = null;
        StructuralVersion++;
    }

    /// <summary>
    /// Resets the catalog with the specified paths. Sets <see cref="CurrentIndex"/> to 0 if non-empty, or -1 if empty.
    /// </summary>
    public void Reset(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _entries.Clear();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _entries.Add(new CatalogEntry(path));
            }
        }
        CurrentIndex = _entries.Count > 0 ? 0 : -1;
        InvalidateIndex();
    }

    public void Reset(IEnumerable<CatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        _entries.AddRange(entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path)));
        CurrentIndex = _entries.Count > 0 ? 0 : -1;
        InvalidateIndex();
    }

    public CatalogEntry? Find(string path) => IndexOf(path) is var index && index >= 0 ? _entries[index] : null;

    public bool UpdateMetadata(string path, long length, DateTime lastWriteUtc, int? width = null, int? height = null)
    {
        var index = IndexOf(path);
        if (index < 0) return false;
        _entries[index] = _entries[index].WithMetadata(length, lastWriteUtc, width, height);
        return true;
    }

    /// <summary>
    /// Moves the specified item to the front of the catalog (index 0) and sets <see cref="CurrentIndex"/> to 0.
    /// Returns true if the item was found and moved (or already at front), false otherwise.
    /// </summary>
    public bool MoveToFront(string path)
    {
        var index = IndexOf(path);
        if (index < 0) return false;

        if (index > 0)
        {
            var entry = _entries[index];
            _entries.RemoveAt(index);
            _entries.Insert(0, entry);
            InvalidateIndex();
        }

        CurrentIndex = 0;
        return true;
    }

    /// <summary>
    /// Sets the current position to the specified index.
    /// </summary>
    public bool SetCurrent(int index)
    {
        if (_entries.Count == 0 && index == -1)
        {
            CurrentIndex = -1;
            return true;
        }

        if (index >= 0 && index < _entries.Count)
        {
            CurrentIndex = index;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes the entry with the specified path.
    /// Calculates nextIndex using the same formula as AdvanceBeforeFileActionAsync:
    /// Math.Min(Math.Max(removedIndex, 0), Count - 1), or -1 if the catalog becomes empty.
    /// Returns the new <see cref="CurrentIndex"/>.
    /// </summary>
    public int Remove(string path)
    {
        var removedIndex = IndexOf(path);
        if (removedIndex < 0) return CurrentIndex;

        _entries.RemoveAt(removedIndex);
        InvalidateIndex();
        if (_entries.Count == 0)
        {
            CurrentIndex = -1;
            return -1;
        }

        var nextIndex = Math.Min(Math.Max(removedIndex, 0), _entries.Count - 1);
        CurrentIndex = nextIndex;
        return nextIndex;
    }

    /// <summary>
    /// Restores an entry at the specified index (clamped to [0, Count]).
    /// Does not add duplicate paths. Returns true if restored, false if duplicate or invalid.
    /// </summary>
    public bool Restore(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IndexOf(path) >= 0) return false; // Avoid duplicates

        var clampedIndex = Math.Clamp(index, 0, _entries.Count);
        _entries.Insert(clampedIndex, new CatalogEntry(path));
        InvalidateIndex();

        if (CurrentIndex == -1)
        {
            CurrentIndex = clampedIndex;
        }
        else if (clampedIndex <= CurrentIndex)
        {
            CurrentIndex++;
        }

        return true;
    }

    /// <summary>
    /// Replaces the ordering of items in the catalog with <paramref name="newOrder"/>.
    /// Preserves the currently selected entry by path.
    /// Returns false if the set of files in <paramref name="newOrder"/> does not match the current catalog.
    /// </summary>
    public bool ReplaceOrder(IReadOnlyList<string> newOrder)
    {
        ArgumentNullException.ThrowIfNull(newOrder);

        if (newOrder.Count != _entries.Count) return false;

        // Verify exact set match (case-insensitive)
        var currentSet = new HashSet<string>(_entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in newOrder)
        {
            if (!currentSet.Contains(path)) return false;
        }

        var currentPath = Current?.Path;

        // Build mapping and reorder
        var entryMap = _entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        foreach (var path in newOrder)
        {
            _entries.Add(entryMap[path]);
        }
        InvalidateIndex();

        if (currentPath is not null)
        {
            var newIndex = IndexOf(currentPath);
            CurrentIndex = newIndex >= 0 ? newIndex : (_entries.Count > 0 ? 0 : -1);
        }
        else
        {
            CurrentIndex = _entries.Count > 0 ? 0 : -1;
        }

        return true;
    }

    /// <summary>
    /// Inserts an entry into the catalog preserving sorted order defined by <paramref name="comparison"/>.
    /// Used for undo operations. Returns the index where the item was inserted.
    /// </summary>
    public int InsertSorted(string path, Comparison<string> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path cannot be empty.", nameof(path));

        // If duplicate exists, return existing index
        var existingIndex = IndexOf(path);
        if (existingIndex >= 0) return existingIndex;

        var targetIndex = 0;
        while (targetIndex < _entries.Count && comparison(_entries[targetIndex].Path, path) < 0)
        {
            targetIndex++;
        }

        _entries.Insert(targetIndex, new CatalogEntry(path));
        InvalidateIndex();

        if (CurrentIndex == -1)
        {
            CurrentIndex = targetIndex;
        }
        else if (targetIndex <= CurrentIndex)
        {
            CurrentIndex++;
        }

        return targetIndex;
    }

    /// <summary>
    /// Creates a snapshot array of all paths currently in the catalog.
    /// </summary>
    public string[] Snapshot() => _entries.Select(e => e.Path).ToArray();

    /// <summary>
    /// Creates a true copy of all entries (unlike <see cref="Entries"/>, which wraps the live
    /// list). For consumers -- e.g. the background preload scheduler -- that need a stable
    /// snapshot including each entry's cached Length/LastWriteUtc, safe to read off the UI thread.
    /// </summary>
    public CatalogEntry[] EntriesSnapshot() => _entries.ToArray();
}
