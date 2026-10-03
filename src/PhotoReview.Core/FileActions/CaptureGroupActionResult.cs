using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>Post-action state for one image or sidecar in a capture group.</summary>
public sealed record CaptureGroupMemberResult(
    JournalGroupMember Member,
    bool Completed,
    bool Conflict,
    string? Error = null,
    bool SourceExists = false,
    bool DestinationExists = false,
    bool StateKnown = true);

/// <summary>Result of one journaled capture-group action.</summary>
public sealed record CaptureGroupActionResult(
    bool Succeeded,
    bool Rejected,
    FileOperationType Operation,
    string GroupId,
    JournalEntry? Entry,
    IReadOnlyList<CaptureGroupMemberResult> Members,
    string? Error,
    bool JournalPersisted = true,
    string? JournalError = null,
    bool PermanentlyDeleted = false,
    IReadOnlyList<string>? SkippedMissing = null);
