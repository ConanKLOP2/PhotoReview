using PhotoReview.Core.Model;

namespace PhotoReview.Core.Catalog;

/// <summary>Finds unambiguous same-folder, same-basename JPEG+RAW captures.</summary>
public static class CaptureGroupBuilder
{
    private const string XmpExtension = ".xmp";

    /// <summary>
    /// Builds groups only when a basename has exactly one JPEG (<c>.jpg</c>/<c>.jpeg</c>; other image types such as an
    /// edited <c>.tif</c> are never the JPEG side) and exactly one supported RAW file.
    /// An XMP sidecar is included when it can be chosen deterministically: exactly one <c>&lt;base&gt;.xmp</c>, else
    /// (when none exists) exactly one <c>&lt;file&gt;.&lt;ext&gt;.xmp</c> (e.g. <c>IMG.CR3.xmp</c>) for either member.
    /// With two or more competing candidates no sidecar is claimed: the pair is still grouped, but the sidecars stay
    /// where they are on Move/Recycle.
    /// </summary>
    public static IReadOnlyList<CaptureGroup> Build(IEnumerable<string> imagePaths, IEnumerable<string>? sidecarPaths = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        var buckets = new Dictionary<CaptureKey, PathBucket>(CaptureKeyComparer.Instance);
        var order = new List<PathBucket>(); // first-seen order; each bucket carries its own key (no second dictionary lookup)
        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var extension = Path.GetExtension(path);
            var isJpeg = ImageFileTypes.JpegExtensions.Contains(extension);
            if (!isJpeg && !ImageFileTypes.RawExtensions.Contains(extension)) continue;

            var key = new CaptureKey(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new PathBucket(key);
                buckets.Add(key, bucket);
                order.Add(bucket);
            }

            if (isJpeg) bucket.AddJpeg(path);
            else bucket.AddRaw(path);
        }

        var sidecars = IndexSidecars(sidecarPaths);
        var groups = new List<CaptureGroup>();
        foreach (var bucket in order)
        {
            if (bucket.JpegCount != 1 || bucket.RawCount != 1) continue;

            var xmp = ChooseSidecar(sidecars, bucket.Key, bucket.JpegPath!, bucket.RawPath!);
            groups.Add(new CaptureGroup(bucket.JpegPath!, bucket.RawPath!, xmp));
        }

        return groups;
    }

    private static string? ChooseSidecar(Dictionary<CaptureKey, List<string>> sidecars, CaptureKey key, string jpegPath, string rawPath)
    {
        // Hot path (10k-entry folders): most folders have no sidecars at all, so skip the per-group key/list allocations.
        if (sidecars.Count == 0) return null;

        // <base>.xmp wins; two of them (case variants) are ambiguous and are not silently replaced by a weaker match.
        if (sidecars.TryGetValue(key, out var exact))
            return exact.Count == 1 ? exact[0] : null;

        var candidates = new List<string>();
        foreach (var memberPath in new[] { jpegPath, rawPath })
        {
            var memberKey = new CaptureKey(Path.GetDirectoryName(memberPath) ?? string.Empty, Path.GetFileName(memberPath));
            if (sidecars.TryGetValue(memberKey, out var byFile)) candidates.AddRange(byFile);
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Replaces unambiguous pair members with one catalog entry using the selected representative.
    /// The returned entry retains the representative member's metadata and exposes both members through its group.
    /// The collapsed entry keeps the position of the first member seen in <paramref name="entries"/> (not necessarily
    /// the representative's), so name/size/date orders place a pair where its earliest member sorts.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> GroupEntries(
        IReadOnlyList<CatalogEntry> entries,
        RawPairMode mode,
        IEnumerable<string>? sidecarPaths = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == RawPairMode.Separate) return entries.ToArray();

        var groups = Build(entries.Select(entry => entry.Path), sidecarPaths);
        if (groups.Count == 0) return entries.ToArray();

        var entryByPath = new Dictionary<string, CatalogEntry>(entries.Count, StringComparer.OrdinalIgnoreCase); // presized: no rehash-growth on 10k entries
        foreach (var entry in entries) entryByPath.TryAdd(entry.Path, entry);

        var groupByMemberPath = new Dictionary<string, CaptureGroup>(groups.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            groupByMemberPath[group.JpegPath] = group;
            groupByMemberPath[group.RawPath] = group;
        }

        var emitted = new HashSet<CaptureGroup>(groups.Count, ReferenceEqualityComparer.Instance); // one emission per group instance: no path hashing on the 10k-entry hot path
        var grouped = new List<CatalogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!groupByMemberPath.TryGetValue(entry.Path, out var group))
            {
                grouped.Add(entry);
                continue;
            }

            if (!emitted.Add(group)) continue;
            var representativePath = group.GetRepresentativePath(mode);
            if (entryByPath.TryGetValue(representativePath, out var representative))
            {
                grouped.Add(representative with { CaptureGroup = group });
            }
        }

        return grouped;
    }

    /// <summary>
    /// Indexes sidecars by (folder, name without ".xmp"): <c>a.xmp</c> is under "a", <c>a.cr2.xmp</c> under "a.cr2"
    /// (which <see cref="ChooseSidecar"/> looks up with each member's full file name).
    /// </summary>
    private static Dictionary<CaptureKey, List<string>> IndexSidecars(IEnumerable<string>? sidecarPaths)
    {
        var result = new Dictionary<CaptureKey, List<string>>(CaptureKeyComparer.Instance);
        if (sidecarPaths is null) return result;

        foreach (var path in sidecarPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), XmpExtension, StringComparison.OrdinalIgnoreCase)) continue;
            var key = new CaptureKey(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            if (!result.TryGetValue(key, out var paths))
            {
                paths = [];
                result.Add(key, paths);
            }
            paths.Add(path);
        }

        return result;
    }

    private readonly record struct CaptureKey(string Directory, string BaseName);

    /// <summary>Only the first path and a count per kind are needed: a bucket is grouped iff it has exactly one of each (no per-bucket lists).</summary>
    private sealed class PathBucket(CaptureKey key)
    {
        public CaptureKey Key { get; } = key;
        public string? JpegPath { get; private set; }
        public string? RawPath { get; private set; }
        public int JpegCount { get; private set; }
        public int RawCount { get; private set; }

        public void AddJpeg(string path)
        {
            if (JpegCount++ == 0) JpegPath = path;
        }

        public void AddRaw(string path)
        {
            if (RawCount++ == 0) RawPath = path;
        }
    }

    private sealed class CaptureKeyComparer : IEqualityComparer<CaptureKey>
    {
        public static CaptureKeyComparer Instance { get; } = new();

        public bool Equals(CaptureKey x, CaptureKey y) =>
            string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.BaseName, y.BaseName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(CaptureKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Directory),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BaseName));
    }
}
