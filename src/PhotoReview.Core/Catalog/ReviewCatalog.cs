using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Manages an ordered collection of image entries and tracks the current review position.
/// </summary>
/// <remarks>
/// This class is not thread-safe and must only be accessed from the UI thread (ADR 0005).
/// The composition root calls <see cref="BindToCurrentThread"/>; in Debug builds every mutator
/// then throws if it is invoked from any other thread. Unbound catalogs (unit tests) are unchecked.
/// </remarks>
public sealed class ReviewCatalog
{
    private readonly List<CatalogEntry> _entries = [];
    private RawPairMode _rawPairMode = RawPairMode.Separate;

    // 0 = unbound (no owner check). Managed thread ids start at 1.
    private int _ownerThreadId;

    /// <summary>
    /// Binds this catalog to the calling thread (the UI thread, from the composition root). In Debug
    /// builds, mutators invoked from any other thread afterwards throw <see cref="InvalidOperationException"/>
    /// (ADR 0005). Release builds only record the id.
    /// </summary>
    public void BindToCurrentThread() => Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);

    [Conditional("DEBUG")]
    internal void AssertOwnerThread([System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var owner = Volatile.Read(ref _ownerThreadId);
        var current = Environment.CurrentManagedThreadId;
        if (owner != 0 && owner != current)
        {
            throw new InvalidOperationException(
                $"ReviewCatalog.{member} called on thread {current}; the catalog is bound to thread {owner} (UI thread only, ADR 0005).");
        }
    }

    // Perf: IndexOf/Find run once per navigation (ImagePresenter, FileActionController) and
    // must not be O(n) per call. The index is rebuilt lazily on the next lookup after any
    // structural change (add/remove/reorder) instead of being kept incrementally in sync,
    // which is simpler to keep correct and still turns "many lookups, few mutations" into
    // O(1) lookups paid for by an occasional O(n) rebuild.
    private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexDirty = true;

    // Remove is the hot mutation of the review loop (Recycle/Move burst), so it must not force an O(n) rebuild of the
    // index before the next photo is shown. Instead the dictionary keeps its (now stale) positions and the removed
    // positions are recorded here, sorted; the live index is `stored - (removed positions below stored)`.
    // A full rebuild only happens once this list grows past MaxRemovedBeforeRebuild.
    private const int MaxRemovedBeforeRebuild = 64;
    private readonly List<int> _removedPositions = [];
    private bool _indexHasDuplicateKeys; // case-variant duplicate paths: a removal could unhide the second one, so rebuild

    /// <summary>Test seam: how many times the path index was rebuilt from scratch.</summary>
    internal int IndexRebuildCountForTests { get; private set; }
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
        if (!_indexByPath.TryGetValue(path, out var stored)) return -1;
        if (_removedPositions.Count == 0) return stored;
        var found = _removedPositions.BinarySearch(stored);
        return found >= 0 ? -1 : stored - ~found; // ~found = number of removed positions below `stored`
    }

    private void EnsureIndex()
    {
        if (!_indexDirty) return;
        _indexByPath.Clear();
        _removedPositions.Clear();
        _indexHasDuplicateKeys = false;
        IndexRebuildCountForTests++;
        for (var i = 0; i < _entries.Count; i++)
        {
            // First occurrence wins for unusual case-variant duplicate paths, matching the
            // previous linear-scan behavior (first match).
            if (!_indexByPath.TryAdd(_entries[i].Path, i)) _indexHasDuplicateKeys = true;
            if (_entries[i].CaptureGroup is { } group)
            {
                foreach (var memberPath in group.ImagePaths)
                {
                    if (!string.Equals(memberPath, _entries[i].Path, StringComparison.OrdinalIgnoreCase)
                        && !_indexByPath.TryAdd(memberPath, i))
                    {
                        _indexHasDuplicateKeys = true;
                    }
                }
            }
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
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(paths);
        // Materialise first: an enumeration that throws (or a live view of this catalog) must not leave the
        // catalog cleared with a stale index/version.
        var fresh = new List<CatalogEntry>();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                fresh.Add(new CatalogEntry(path));
            }
        }
        _entries.Clear();
        _entries.AddRange(fresh);
        _rawPairMode = RawPairMode.Separate; // plain paths never form capture groups: forget the previous mode too
        CurrentIndex = _entries.Count > 0 ? 0 : -1;
        InvalidateIndex();
    }

    public void Reset(IEnumerable<CatalogEntry> entries)
        => Reset(entries, RawPairMode.Separate);

    /// <summary>Resets the catalog and optionally collapses matched JPEG+RAW pairs.</summary>
    public void Reset(IEnumerable<CatalogEntry> entries, RawPairMode rawPairMode, IEnumerable<string>? sidecarPaths = null)
    {
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(entries);
        var fresh = entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path)).ToList();
        var grouped = CaptureGroupBuilder.GroupEntries(fresh, rawPairMode, sidecarPaths);
        _entries.Clear();
        _entries.AddRange(grouped);
        _rawPairMode = rawPairMode;
        CurrentIndex = _entries.Count > 0 ? 0 : -1;
        InvalidateIndex();
    }

    public CatalogEntry? Find(string path) => IndexOf(path) is var index && index >= 0 ? _entries[index] : null;

    public bool UpdateMetadata(string path, long length, DateTime lastWriteUtc, int? width = null, int? height = null)
    {
        AssertOwnerThread();
        var index = IndexOf(path);
        if (index < 0) return false;
        // A capture-group entry is found by any member path, but the stat belongs to ONE file: stamping the entry's own
        // (representative) path with another member's length/time would make it look changed on the next comparison.
        if (!string.Equals(_entries[index].Path, path, StringComparison.OrdinalIgnoreCase)) return false;
        _entries[index] = _entries[index].WithMetadata(length, lastWriteUtc, width, height);
        return true;
    }

    /// <summary>
    /// Moves the specified item to the front of the catalog (index 0) and sets <see cref="CurrentIndex"/> to 0.
    /// Returns true if the item was found and moved (or already at front), false otherwise.
    /// </summary>
    public bool MoveToFront(string path)
    {
        AssertOwnerThread();
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
        AssertOwnerThread();
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
        AssertOwnerThread();
        var removedIndex = IndexOf(path);
        if (removedIndex < 0) return CurrentIndex;

        // IndexOf just made the index clean, so the stored position of `path` is known.
        if (!_indexHasDuplicateKeys && _removedPositions.Count < MaxRemovedBeforeRebuild
            && _indexByPath.TryGetValue(path, out var storedPosition))
        {
            var at = _removedPositions.BinarySearch(storedPosition);
            _removedPositions.Insert(~at, storedPosition);
            _entries.RemoveAt(removedIndex);
            _pathsCache = null;
            StructuralVersion++;
        }
        else
        {
            _entries.RemoveAt(removedIndex);
            InvalidateIndex();
        }
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
        AssertOwnerThread();
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
    /// Restores members of a previously collapsed capture (or a plain photo set) at <paramref name="index"/>.
    /// Only image and RAW files ever become entries: an .xmp sidecar in <paramref name="paths"/> is never listed (it only
    /// supplies the group's sidecar when a group is derived). When the catalog is in a pairing mode and both image members
    /// of a capture are among <paramref name="paths"/>, ONE grouped entry is restored (the explicit
    /// <paramref name="group"/>, else one derived from the paths) whose representative follows the mode; members that
    /// cannot form a pair come back as independent entries. <see cref="CurrentIndex"/> keeps pointing at the image that
    /// was displayed, exactly like <see cref="Restore"/>; it only moves when the catalog was empty.
    /// </summary>
    /// <returns>True when at least one entry was restored.</returns>
    public bool RestoreMembers(IEnumerable<string> paths, int index, CaptureGroup? group = null)
    {
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(paths);
        var all = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A retried partial undo: one member is already listed as an independent entry (restored earlier) and the other has
        // just come back -- fold them into the grouped entry at the present member's position instead of listing a second one.
        var reformed = TryReformGroup(all, group);
        if (reformed) all = all.Where(path => !group!.ImagePaths.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
        var unique = all.Where(IsRestorableImage).Where(path => IndexOf(path) < 0).ToArray();
        if (unique.Length == 0) return reformed;

        var groups = new List<CaptureGroup>();
        if (_rawPairMode != RawPairMode.Separate)
        {
            var present = new HashSet<string>(unique, StringComparer.OrdinalIgnoreCase);
            if (group is not null && group.ImagePaths.All(present.Contains)) groups.Add(group);
            var rest = unique.Where(path => group is null || groups.Count == 0 || !group.ImagePaths.Contains(path, StringComparer.OrdinalIgnoreCase));
            groups.AddRange(CaptureGroupBuilder.Build(rest,
                all.Where(path => string.Equals(System.IO.Path.GetExtension(path), ".xmp", StringComparison.OrdinalIgnoreCase))));
        }

        var groupByMember = new Dictionary<string, CaptureGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in groups)
        {
            groupByMember[candidate.JpegPath] = candidate;
            groupByMember[candidate.RawPath] = candidate;
        }

        var emitted = new HashSet<CaptureGroup>();
        var restored = new List<CatalogEntry>(unique.Length);
        foreach (var path in unique)
        {
            if (!groupByMember.TryGetValue(path, out var owner)) restored.Add(new CatalogEntry(path));
            else if (emitted.Add(owner)) restored.Add(new CatalogEntry(owner.GetRepresentativePath(_rawPairMode)) { CaptureGroup = owner });
        }

        var insertAt = Math.Clamp(index, 0, _entries.Count);
        _entries.InsertRange(insertAt, restored);
        InvalidateIndex();
        if (CurrentIndex < 0) CurrentIndex = insertAt;
        else if (insertAt <= CurrentIndex) CurrentIndex += restored.Count;
        return true;
    }

    /// <summary>
    /// Re-forms <paramref name="group"/> when at least one of its image members is already an independent catalog entry
    /// and every missing member is among <paramref name="restoredPaths"/>: the standalone entry becomes the grouped one
    /// (same position, same <see cref="CurrentIndex"/>) and nothing is inserted. The entry keeps the currently displayed member
    /// as its path when that member is one of the standalone entries. Returns false (nothing changed) otherwise.
    /// </summary>
    private bool TryReformGroup(IReadOnlyCollection<string> restoredPaths, CaptureGroup? group)
    {
        if (group is null || _rawPairMode == RawPairMode.Separate) return false;
        var standalone = new List<int>();
        foreach (var member in group.ImagePaths)
        {
            var at = IndexOf(member);
            if (at >= 0)
            {
                if (_entries[at].CaptureGroup is not null) return false; // already grouped: not a partial restore
                standalone.Add(at);
            }
            else if (!restoredPaths.Contains(member, StringComparer.OrdinalIgnoreCase) || !IsRestorableImage(member))
            {
                return false; // a member is still missing: keep the independent entry
            }
        }
        if (standalone.Count == 0) return false;

        standalone.Sort();
        var slot = standalone[0];
        var representative = group.GetRepresentativePath(_rawPairMode);
        // The displayed image must not switch under the user: when one of the standalone entries is the current one, it stays
        // the entry's path (even if it is not the mode's representative); otherwise the representative follows the mode.
        var current = standalone.Contains(CurrentIndex) ? _entries[CurrentIndex] : null;
        var existing = current ?? _entries[slot];
        _entries[slot] = current is not null || string.Equals(existing.Path, representative, StringComparison.OrdinalIgnoreCase)
            ? existing with { CaptureGroup = group }
            : new CatalogEntry(representative) { CaptureGroup = group };
        for (var i = standalone.Count - 1; i >= 1; i--) // both members were listed separately: one entry remains
        {
            _entries.RemoveAt(standalone[i]);
            if (CurrentIndex == standalone[i]) CurrentIndex = slot;
            else if (CurrentIndex > standalone[i]) CurrentIndex--;
        }
        InvalidateIndex();
        return true;
    }

    private static bool IsRestorableImage(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return ImageFileTypes.SupportedExtensions.Contains(extension) || ImageFileTypes.RawExtensions.Contains(extension);
    }

    /// <summary>
    /// Replaces the ordering of items in the catalog with <paramref name="newOrder"/>.
    /// Preserves the currently selected entry by path.
    /// Returns false if the set of files in <paramref name="newOrder"/> does not match the current catalog.
    /// </summary>
    public bool ReplaceOrder(IReadOnlyList<string> newOrder)
    {
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(newOrder);

        IReadOnlyList<string> effectiveOrder = newOrder;
        if (_rawPairMode != RawPairMode.Separate && TryCollapseMemberOrder(newOrder, out var collapsedOrder))
        {
            effectiveOrder = collapsedOrder;
        }

        if (effectiveOrder.Count != _entries.Count) return false;

        // Verify exact set match (case-insensitive)
        // CORE-01: must be a true permutation -- every current path exactly once (a duplicate such as [A,A] for [A,B]
        // has the right count and only known members, yet would drop B from the catalog).
        var currentSet = new HashSet<string>(_entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in effectiveOrder)
        {
            if (path is null || !currentSet.Contains(path) || !seen.Add(path)) return false;
        }

        var currentPath = Current?.Path;

        // Build mapping and reorder
        var entryMap = _entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        foreach (var path in effectiveOrder)
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

    private bool TryCollapseMemberOrder(IReadOnlyList<string> memberOrder, out IReadOnlyList<string> collapsedOrder)
    {
        collapsedOrder = [];
        var representativeByMember = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expectedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            var paths = entry.CaptureGroup?.ImagePaths ?? [entry.Path];
            foreach (var path in paths)
            {
                expectedMembers.Add(path);
                representativeByMember.TryAdd(path, entry.Path);
            }
        }

        if (memberOrder.Count != expectedMembers.Count) return false;
        var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRepresentatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(_entries.Count);
        foreach (var path in memberOrder)
        {
            if (path is null || !expectedMembers.Contains(path) || !seenMembers.Add(path)) return false;
            var representative = representativeByMember[path];
            if (seenRepresentatives.Add(representative)) result.Add(representative);
        }

        if (result.Count != _entries.Count) return false;
        collapsedOrder = result;
        return true;
    }

    /// <summary>
    /// AR16: removes the unreadable files in <paramref name="paths"/> (the background readability probe's result) and
    /// returns the paths actually removed, in catalog order. A JPEG+RAW capture with only ONE of its image members
    /// listed keeps its readable partner: the entry degrades in place to that survivor as a standalone (group-less)
    /// entry, whichever member is the representative; only when every image member is listed does the capture go
    /// (all its image members are then returned). The current position is kept when the current entry survives or
    /// degrades (a degraded current may show the partner instead). When the current
    /// entry itself is removed, the position advances as <see cref="Remove"/> does for a Delete: the
    /// first surviving entry after it, else the last one, or -1 once the catalog is empty.
    /// </summary>
    public IReadOnlyList<string> RemovePaths(IEnumerable<string> paths)
    {
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(paths);
        var doomed = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);
        if (doomed.Count == 0 || _entries.Count == 0) return [];

        var removed = new List<string>();
        var kept = new List<CatalogEntry>(_entries.Count);
        var oldCurrent = CurrentIndex;
        var nextCurrent = -1;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var entryDoomed = doomed.Contains(entry.Path);
            if (entry.CaptureGroup is { } group)
            {
                var bad = group.ImagePaths.Where(doomed.Contains).ToArray();
                if (bad.Length == group.ImagePaths.Count)
                {
                    removed.Add(entry.Path);
                    removed.AddRange(bad.Where(p => !string.Equals(p, entry.Path, StringComparison.OrdinalIgnoreCase)));
                    continue;
                }

                if (bad.Length > 0)
                {
                    // Degrade in place: the readable partner stays reachable as a standalone entry.
                    var survivor = group.ImagePaths.First(p => !doomed.Contains(p));
                    removed.AddRange(bad);
                    entry = string.Equals(entry.Path, survivor, StringComparison.OrdinalIgnoreCase)
                        ? entry with { CaptureGroup = null }
                        : new CatalogEntry(survivor);
                    entryDoomed = false;
                }
            }

            if (entryDoomed)
            {
                removed.Add(entry.Path);
                continue;
            }

            // The survivor at the current position (kept current), or the first one after a removed current.
            if (nextCurrent < 0 && oldCurrent >= 0 && i >= oldCurrent) nextCurrent = kept.Count;
            kept.Add(entry);
        }

        if (removed.Count == 0) return [];

        _entries.Clear();
        _entries.AddRange(kept);
        InvalidateIndex();
        CurrentIndex = _entries.Count == 0 ? -1
            : oldCurrent < 0 ? 0
            : nextCurrent >= 0 ? nextCurrent
            : _entries.Count - 1;
        return removed;
    }

    /// <summary>
    /// Inserts an entry into the catalog preserving sorted order defined by <paramref name="comparison"/>.
    /// Used for undo operations. Returns the index where the item was inserted.
    /// </summary>
    public int InsertSorted(string path, Comparison<string> comparison)
    {
        AssertOwnerThread();
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
    /// Creates a true copy of all entries (unlike <see cref="Entries"/>, which wraps the live
    /// list). For consumers -- e.g. the background preload scheduler -- that need a stable
    /// snapshot including each entry's cached Length/LastWriteUtc, safe to read off the UI thread.
    /// </summary>
    public CatalogEntry[] EntriesSnapshot() => _entries.ToArray();
}
