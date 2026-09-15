namespace PhotoReview.App;

public static class PreloadOrderService
{
    public static IEnumerable<int> Build(int center, int count, bool fullFolder)
    {
        if (center < 0 || center >= count) yield break;
        for (var offset = 1; offset <= 32; offset++)
            if (center + offset < count) yield return center + offset;
        for (var offset = 1; offset <= 8; offset++)
            if (center - offset >= 0) yield return center - offset;
        if (!fullFolder) yield break;
        for (var index = center + 33; index < count; index++) yield return index;
        for (var index = center - 9; index >= 0; index--) yield return index;
    }
}
