using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chế độ sắp xếp danh sách ảnh.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<ImageSortMode>))]
public enum ImageSortMode
{
    [JsonAlias("PortraitFirst")]
    Name = 0,

    [JsonAlias("Size")]
    SizeDescending = 1,

    SizeAscending = 2,

    /// <summary>The catalog keeps the order the file system enumerates the folder in (no sort, no Explorer order).</summary>
    Default = 3,

    /// <summary>The app's own natural name order, A to Z (same order as <see cref="Name"/>), never Explorer's order.</summary>
    NameAscending = 4,

    /// <summary>Exactly the reverse of <see cref="NameAscending"/>, never Explorer's order.</summary>
    NameDescending = 5
}

/// <summary>Which sort modes let an open Explorer window's view order override the app's own order.</summary>
public static class SortModePolicy
{
    /// <summary>
    /// True for the modes where the Explorer view order (when a window shows the folder) replaces the sorted
    /// order. <see cref="ImageSortMode.Default"/>, <see cref="ImageSortMode.NameAscending"/> and
    /// <see cref="ImageSortMode.NameDescending"/> are decided by the app: the Explorer query is not even started.
    /// </summary>
    public static bool UsesExplorerOrder(ImageSortMode mode) =>
        mode is not (ImageSortMode.Default or ImageSortMode.NameAscending or ImageSortMode.NameDescending);
}
