using System;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// feat/image-crossfade: pure decision of whether a bitmap assignment should play the optional image-change
/// transition (fade). WPF-free and side-effect-free so it is unit-tested directly (see
/// <c>ImageTransitionDecisionTests</c>); <see cref="ImagePresenter"/> is the only caller.
/// </summary>
public static class ImageTransitionDecision
{
    /// <summary>
    /// True only when this bitmap belongs to a NAVIGATION TO A DIFFERENT FILE:
    /// <list type="bullet">
    /// <item><paramref name="newPath"/> names a file (never for clearing the display, e.g. null/error/empty state).</item>
    /// <item>there is a previously presented file (<paramref name="previousPath"/> is not null) -- the first image
    /// after opening a folder has nothing to fade from.</item>
    /// <item><paramref name="previousPath"/> and <paramref name="newPath"/> differ (case-insensitive path compare)
    /// -- a same-file upgrade (thumbnail -> preview -> original within one navigation) must stay instant.</item>
    /// <item>Compare is not currently shown (<paramref name="isCompareVisible"/> is false).</item>
    /// </list>
    /// </summary>
    public static bool ShouldTransition(string? previousPath, string? newPath, bool isCompareVisible)
    {
        if (isCompareVisible) return false;
        if (newPath is null) return false;
        if (previousPath is null) return false;
        return !string.Equals(previousPath, newPath, StringComparison.OrdinalIgnoreCase);
    }
}
