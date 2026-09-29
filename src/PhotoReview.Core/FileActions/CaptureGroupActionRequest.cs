using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>A Move, Copy, or Recycle to apply to every member of one capture group.</summary>
public sealed record CaptureGroupActionRequest(
    CaptureGroup Group,
    FileOperationType Operation,
    string? Destination = null,
    bool AllowPermanentDelete = false);
