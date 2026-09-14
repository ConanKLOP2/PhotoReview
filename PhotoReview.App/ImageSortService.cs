using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

public static class ImageSortService
{
    private static readonly StringComparer ExplorerNameComparer = new ExplorerComparer();
    private static readonly BoundedLruCache<string, int> OrientationCache = new(
        1L * 1024 * 1024, _ => 64, StringComparer.OrdinalIgnoreCase);

    public static List<string> Sort(IEnumerable<string> files, string? mode)
    {
        var byName = files.OrderBy(path => Path.GetFileName(path), ExplorerNameComparer);
        if (!string.Equals(mode, "PortraitFirst", StringComparison.OrdinalIgnoreCase)) return byName.ToList();

        return byName.Select(path => (Path: path, Orientation: GetCachedOrientation(path)))
            .OrderBy(item => item.Orientation == 1 ? 0 : item.Orientation == 2 ? 1 : 2)
            .ThenBy(item => Path.GetFileName(item.Path), ExplorerNameComparer)
            .Select(item => item.Path)
            .ToList();
    }

    public static string NaturalKey(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "\\d+", match => match.Value.PadLeft(12, '0'));

    private sealed class ExplorerComparer : StringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            try { return StrCmpLogicalW(x, y); }
            catch (DllNotFoundException) { return StringComparer.OrdinalIgnoreCase.Compare(NaturalKey(x), NaturalKey(y)); }
            catch (EntryPointNotFoundException) { return StringComparer.OrdinalIgnoreCase.Compare(NaturalKey(x), NaturalKey(y)); }
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;
        public override int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }

    private static int GetCachedOrientation(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (OrientationCache.TryGet(key, out var orientation)) return orientation;
            orientation = GetOrientation(path);
            OrientationCache.Set(key, orientation);
            return orientation;
        }
        catch { return 3; }
    }

    private static int GetOrientation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var orientation = metadata.GetQuery("/app1/ifd/{ushort=274}");
                if (orientation is ushort value && value is 5 or 6 or 7 or 8) (width, height) = (height, width);
                else if (orientation is byte byteValue && byteValue is 5 or 6 or 7 or 8) (width, height) = (height, width);
            }
            return height > width ? 1 : width > height ? 2 : 3;
        }
        catch { return 3; }
    }
}
