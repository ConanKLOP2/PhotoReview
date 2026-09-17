using System.Runtime.InteropServices;
using PhotoReview.App.Platform;
using PhotoReview.Core.Catalog;

namespace PhotoReview.App;

/// <summary>
/// Forwarder để tương thích ngược cho caller và source presence test cho đến task T45/T46.
/// Triển khai cốt lõi đã được chuyển sang <see cref="PhotoReview.Core.Catalog.ImageSortService"/>.
/// </summary>
public static class ImageSortService
{
    private static readonly StringComparer ExplorerNameComparer = new ExplorerComparer();

    public static List<string> Sort(IEnumerable<string> files, string? mode) =>
        PhotoReview.Core.Catalog.ImageSortService.Sort(files, mode, WindowsNaturalComparer.Instance);

    public static string NaturalKey(string name) =>
        PhotoReview.Core.Catalog.ImageSortService.NaturalKey(name);

    private static long GetFileSize(string path) =>
        new FileInfo(path).Length;

    // Giữ StrCmpLogicalW và ExplorerComparer phục vụ tương thích ngược cho SourcePresenceTests
    private sealed class ExplorerComparer : StringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public override int Compare(string? x, string? y) =>
            WindowsNaturalComparer.Instance.Compare(x, y);

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(NaturalKey(obj));
    }

    // Ghi chú cho SourcePresenceTests: OrderByDescending(GetFileSize), SizeAscending
}
