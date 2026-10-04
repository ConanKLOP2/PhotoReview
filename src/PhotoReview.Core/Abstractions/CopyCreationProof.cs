namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Proof, raised by an <see cref="IFileSystem.TryCopyNew(string, string, CopyCreationProof)"/> implementation, that THIS call created the
/// destination file. It stays false when the destination already existed or when the call failed before it created anything (for
/// example the source vanished, so a destination that appeared meanwhile belongs to someone else). A caller may clean up a destination
/// after a failed copy only when this is true; a pre-check that found the path free, or a shorter length, proves nothing.
/// </summary>
public sealed class CopyCreationProof
{
    private volatile bool _destinationCreated;

    /// <summary>True once the implementation has created the destination file itself.</summary>
    public bool DestinationCreated => _destinationCreated;

    /// <summary>Called by the implementation at the moment (or right after) it created the destination.</summary>
    public void MarkCreated() => _destinationCreated = true;
}
