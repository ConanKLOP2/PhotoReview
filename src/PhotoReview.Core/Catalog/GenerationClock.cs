namespace PhotoReview.Core.Catalog;

/// <summary>
/// Thread-safe generation clock managing monotonic generations for image navigation,
/// folder scans/loads, and user catalog interactions.
/// </summary>
public sealed class GenerationClock
{
    private long _navigationGeneration;
    private long _folderGeneration;
    private long _interactionGeneration;

    public GenerationClock(long initialNavigation = 0, long initialFolder = 0, long initialInteraction = 0)
    {
        _navigationGeneration = initialNavigation;
        _folderGeneration = initialFolder;
        _interactionGeneration = initialInteraction;
    }

    /// <summary>
    /// Gets the current navigation generation token.
    /// </summary>
    public long CurrentNavigation => Volatile.Read(ref _navigationGeneration);

    /// <summary>
    /// Advances and returns the next navigation generation token.
    /// </summary>
    public long NextNavigation() => Interlocked.Increment(ref _navigationGeneration);

    /// <summary>
    /// Checks whether the specified token matches the current navigation generation.
    /// </summary>
    public bool IsNavigationCurrent(long generation) => Volatile.Read(ref _navigationGeneration) == generation;

    /// <summary>
    /// Gets the current folder scan/load generation token.
    /// </summary>
    public long CurrentFolder => Volatile.Read(ref _folderGeneration);

    /// <summary>
    /// Advances and returns the next folder scan/load generation token.
    /// </summary>
    public long NextFolder() => Interlocked.Increment(ref _folderGeneration);

    /// <summary>
    /// Checks whether the specified token matches the current folder scan/load generation.
    /// </summary>
    public bool IsFolderCurrent(long generation) => Volatile.Read(ref _folderGeneration) == generation;

    /// <summary>
    /// Gets the current catalog interaction generation token.
    /// </summary>
    public long CurrentInteraction => Volatile.Read(ref _interactionGeneration);

    /// <summary>
    /// Advances and returns the next catalog interaction generation token.
    /// </summary>
    public long NextInteraction() => Interlocked.Increment(ref _interactionGeneration);

    /// <summary>
    /// Checks whether the specified token matches the current catalog interaction generation.
    /// </summary>
    public bool IsInteractionCurrent(long generation) => Volatile.Read(ref _interactionGeneration) == generation;

    /// <summary>
    /// Advances all three generation counters (Navigation, Folder, Interaction) atomically
    /// to invalidate any pending in-flight reads, decodes, and scans when a file action begins.
    /// Equivalent to legacy StopImageReadsForAction.
    /// </summary>
    public void StopForAction()
    {
        Interlocked.Increment(ref _navigationGeneration);
        Interlocked.Increment(ref _folderGeneration);
        Interlocked.Increment(ref _interactionGeneration);
    }
}
