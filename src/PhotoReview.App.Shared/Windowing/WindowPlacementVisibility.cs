using System.Drawing;

namespace PhotoReview.App.Windowing;

/// <summary>Luật "thấy được" của placement đã lưu: dời nguyên từ WindowPlacementService.IsVisible (thuần, nhận work area).</summary>
internal static class WindowPlacementVisibility
{
    /// <summary>Cạnh tối thiểu (px) của phần giao với một work area để placement còn dùng được.</summary>
    internal const int MinVisibleSide = 80;

    /// <summary>
    /// True khi hình chữ nhật không rỗng và giao với ít nhất một work area một vùng &gt;= 80x80 px (vật lý).
    /// </summary>
    internal static bool IsVisible(int left, int top, int right, int bottom, IEnumerable<Rectangle> workAreas)
    {
        if (right <= left || bottom <= top) return false;
        var bounds = Rectangle.FromLTRB(left, top, right, bottom);
        return workAreas.Any(workArea =>
        {
            var intersection = Rectangle.Intersect(bounds, workArea);
            return intersection.Width >= MinVisibleSide && intersection.Height >= MinVisibleSide;
        });
    }
}
