namespace PhotoReview.Imaging;

public static class PreloadOrderService
{
    /// <summary>Size of the window on the side the user is moving towards.</summary>
    public const int ForwardLookahead = 32;

    /// <summary>Size of the small window kept behind the user.</summary>
    public const int BackwardLookahead = 8;

    public static IEnumerable<int> Build(int center, int count, bool fullFolder) =>
        Build(center, count, fullFolder, direction: 1, lead: 0);

    /// <summary>
    /// perf(preload): preload order around <paramref name="center"/>.
    /// <paramref name="direction"/> (+1 Next, -1 Prev) picks which side gets the
    /// <see cref="ForwardLookahead"/> window; the other side keeps a <see cref="BackwardLookahead"/>
    /// window. <paramref name="lead"/> &gt; 0 (a key-held burst, see NavigationPace) shifts the travel
    /// window to start <c>lead + 1</c> images ahead -- the images between would be passed before their
    /// decode finished -- and queues those skipped images right after the shifted window, so they are
    /// still reached first if the user stops. direction = +1, lead = 0 is the original order.
    /// </summary>
    public static IEnumerable<int> Build(int center, int count, bool fullFolder, int direction, int lead)
    {
        if (center < 0 || center >= count) yield break;
        var dir = direction < 0 ? -1 : 1;
        lead = Math.Clamp(lead, 0, count);

        for (var offset = lead + 1; offset <= lead + ForwardLookahead; offset++)
            if (InRange(center + dir * offset, count)) yield return center + dir * offset;
        for (var offset = 1; offset <= lead; offset++)
            if (InRange(center + dir * offset, count)) yield return center + dir * offset;
        for (var offset = 1; offset <= BackwardLookahead; offset++)
            if (InRange(center - dir * offset, count)) yield return center - dir * offset;
        if (!fullFolder) yield break;
        for (var index = center + dir * (lead + ForwardLookahead + 1); InRange(index, count); index += dir) yield return index;
        for (var index = center - dir * (BackwardLookahead + 1); InRange(index, count); index -= dir) yield return index;
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;
}
