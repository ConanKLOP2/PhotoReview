using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

public static class ImageSortService
{
    public static List<string> Sort(IEnumerable<string> files, string? mode)
    {
        var byName = files.OrderBy(path => NaturalKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(mode, "PortraitFirst", StringComparison.OrdinalIgnoreCase)) return byName.ToList();

        return byName.Select(path => (Path: path, Orientation: GetOrientation(path)))
            .OrderBy(item => item.Orientation == 1 ? 0 : item.Orientation == 2 ? 1 : 2)
            .ThenBy(item => NaturalKey(Path.GetFileName(item.Path)), StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .ToList();
    }

    public static string NaturalKey(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "\\d+", match => match.Value.PadLeft(12, '0'));

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
