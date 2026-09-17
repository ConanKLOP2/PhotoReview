namespace PhotoReview.App;

public static class PreloadOrderService
{
    private const int ForwardLookahead = 32;
    private const int BackwardLookahead = 8;

    public static IEnumerable<int> Build(int center, int count, bool fullFolder)
    {
        if (center < 0 || center >= count) yield break;
        for (var offset = 1; offset <= ForwardLookahead; offset++)
            if (center + offset < count) yield return center + offset;
        for (var offset = 1; offset <= BackwardLookahead; offset++)
            if (center - offset >= 0) yield return center - offset;
        if (!fullFolder) yield break;
        for (var index = center + ForwardLookahead + 1; index < count; index++) yield return index;
        for (var index = center - BackwardLookahead - 1; index >= 0; index--) yield return index;
    }
}
