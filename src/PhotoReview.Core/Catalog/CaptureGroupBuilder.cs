using System.IO;

namespace PhotoReview.Core.Catalog;

/// <summary>Finds unambiguous same-folder, same-basename JPEG+RAW captures.</summary>
public static class CaptureGroupBuilder
{
    private const string XmpExtension = ".xmp";

    /// <summary>
    /// Builds groups only when a basename has exactly one supported JPEG and exactly one supported RAW file.
    /// A matching XMP sidecar is included when exactly one is present.
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
            if (!ImageFileTypes.SupportedExtensions.Contains(extension) && !ImageFileTypes.RawExtensions.Contains(extension)) continue;

            var key = new CaptureKey(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new PathBucket();
                buckets.Add(key, bucket);
                order.Add(key);
            }

            if (ImageFileTypes.SupportedExtensions.Contains(extension)) bucket.JpegPaths.Add(path);
            else bucket.RawPaths.Add(path);
        }

        var sidecars = IndexSidecars(sidecarPaths);
        var groups = new List<CaptureGroup>();
        foreach (var key in order)
        {
            var bucket = buckets[key];
            if (bucket.JpegPaths.Count != 1 || bucket.RawPaths.Count != 1) continue;

            sidecars.TryGetValue(key, out var matchingSidecars);
            var xmp = matchingSidecars is { Count: 1 } ? matchingSidecars[0] : null;
            groups.Add(new CaptureGroup(bucket.JpegPaths[0], bucket.RawPaths[0], xmp));
        }

        return groups;
    }

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
