using System;
using PhotoReview.Core.Abstractions;

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

    public long? Length { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    public bool Matches(FileStat stat) => Length == stat.Length && LastWriteUtc == stat.LastWriteUtc;

    public CatalogEntry WithMetadata(long length, DateTime lastWriteUtc, int? width = null, int? height = null) =>
        this with { Length = length, LastWriteUtc = lastWriteUtc, Width = width, Height = height };
}
