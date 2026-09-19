using System;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Represents a single image item in a review catalog.
/// </summary>
/// <param name="Path">The file system path to the image.</param>
public sealed record CatalogEntry(string Path)
{
    public string Path { get; init; } = !string.IsNullOrWhiteSpace(Path)
        ? Path
        : throw new ArgumentException("Path cannot be null or whitespace.", nameof(Path));
}
