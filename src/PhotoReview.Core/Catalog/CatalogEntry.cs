using System;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Represents a single image item in a review catalog.
/// </summary>
/// <param name="Path">The file system path to the image.</param>
public sealed record CatalogEntry(string Path)
{
    // The init accessor validates too, so `entry with { Path = " " }` cannot smuggle an empty path past the constructor check.
    public string Path
    {
        get;
        init => field = RequirePath(value);
    } = RequirePath(Path);

    public long? Length { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    public bool Matches(FileStat stat) => Length == stat.Length && LastWriteUtc == stat.LastWriteUtc;

    /// <summary>
    /// Records a fresh stat. Dimensions that the caller does not supply are kept while the stat is unchanged (a metadata
    /// refresh of an untouched file must not forget its known size) and dropped once the file changed on disk.
    /// </summary>
    public CatalogEntry WithMetadata(long length, DateTime lastWriteUtc, int? width = null, int? height = null)
    {
        var unchanged = Length == length && LastWriteUtc == lastWriteUtc;
        return this with
        {
            Length = length,
            LastWriteUtc = lastWriteUtc,
            Width = width ?? (unchanged ? Width : null),
            Height = height ?? (unchanged ? Height : null),
        };
    }

    private static string RequirePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new ArgumentException("Path cannot be null or whitespace.", nameof(Path));
}
