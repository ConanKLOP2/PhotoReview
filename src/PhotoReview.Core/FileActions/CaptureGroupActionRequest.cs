using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>A Move, Copy, or Recycle to apply to every member of one capture group.</summary>
public sealed record CaptureGroupActionRequest(
    CaptureGroup Group,
    FileOperationType Operation,
    string? Destination = null,
    bool AllowPermanentDelete = false,
    // FEAT-RENAME: Move only. Source path -> NEW FILE NAME in the member's own folder (one entry per member); replaces the
    // destination folder. Build it with CaptureGroupRenamePlan; null = the ordinary Move/Copy/Recycle.
    IReadOnlyDictionary<string, string>? DestinationFileNames = null);
