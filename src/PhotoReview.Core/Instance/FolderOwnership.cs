namespace PhotoReview.Core.Instance;

/// <summary>Q-R18: what a window does with a folder-open request once the instance ownership has been checked.</summary>
public enum FolderOpenDecision
{
    /// <summary>This window may load the folder (it holds, or has just taken, the folder's instance lock).</summary>
    Proceed,
    /// <summary>Another instance owns the folder and accepted the request (or very likely did, Q-R12); this window keeps its folder.</summary>
    ForwardedToOtherInstance,
    /// <summary>Another instance owns the folder but did not take the request; this window keeps its folder.</summary>
    OwnedByOtherInstance,
}

/// <summary>
/// Q-R18: keeps the single-instance lock and the forward pipe in step with the folder a window shows (PerFolder mode;
/// in SingleWindow mode every folder maps to the one app-wide lock, so every open proceeds).
/// Contract: <see cref="AfterOpen"/> is called exactly once for every <see cref="BeforeOpenAsync"/> that returned
/// <see cref="FolderOpenDecision.Proceed"/>, whether the load succeeded, failed or was superseded.
/// </summary>
public interface IFolderOwnership
{
    Task<FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default);

    /// <summary>The window now shows <paramref name="folder"/>: locks of folders it no longer shows (and no open is pending for) are released.</summary>
    void OnFolderShown(string folder);

    /// <summary>The open of <paramref name="folder"/> finished; <paramref name="shownFolder"/> is what the window shows now (null: nothing yet).</summary>
    void AfterOpen(string folder, string? shownFolder);
}
