using System.IO;
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
        var order = new List<CaptureKey>();
        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var extension = Path.GetExtension(path);
            var isJpeg = ImageFileTypes.JpegExtensions.Contains(extension);
            if (!isJpeg && !ImageFileTypes.RawExtensions.Contains(extension)) continue;

            var key = new CaptureKey(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new PathBucket();
                buckets.Add(key, bucket);
                order.Add(key);
            }

            if (isJpeg) bucket.JpegPaths.Add(path);
            else bucket.RawPaths.Add(path);
        }

        var sidecars = IndexSidecars(sidecarPaths);
        var groups = new List<CaptureGroup>();
        foreach (var key in order)
        {
            var bucket = buckets[key];
            if (bucket.JpegPaths.Count != 1 || bucket.RawPaths.Count != 1) continue;

            var xmp = ChooseSidecar(sidecars, key, bucket.JpegPaths[0], bucket.RawPaths[0]);
            groups.Add(new CaptureGroup(bucket.JpegPaths[0], bucket.RawPaths[0], xmp));
        }

        return groups;
    }

    private static string? ChooseSidecar(Dictionary<CaptureKey, List<string>> sidecars, CaptureKey key, string jpegPath, string rawPath)
    {
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

        var entryByPath = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) entryByPath.TryAdd(entry.Path, entry);

        var groupByMemberPath = new Dictionary<string, CaptureGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            groupByMemberPath[group.JpegPath] = group;
            groupByMemberPath[group.RawPath] = group;
        }

        var emitted = new HashSet<CaptureGroup>(ReferenceEqualityComparer.Instance); // one emission per group instance: no path hashing on the 10k-entry hot path
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

    private sealed class PathBucket
    {
        public List<string> JpegPaths { get; } = [];
        public List<string> RawPaths { get; } = [];
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
