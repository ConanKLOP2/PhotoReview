namespace PhotoReview.Core.FileActions;

/// <summary>One member's immutable identity in a journaled capture-group operation.</summary>
public sealed record JournalGroupMember(
    string Source,
    string? Destination,
    long Size,
    DateTime LastWriteUtc,
    bool Permanent = false);
