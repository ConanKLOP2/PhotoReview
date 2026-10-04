using System.IO;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;

namespace PhotoReview.Imaging;

/// <summary>Named values of <see cref="ImageCacheKey.SourceKind"/> (kept a <see cref="byte"/> on the key for compatibility).</summary>
public static class ImageSourceKind
{
    /// <summary>An ordinary image file.</summary>
    public const byte Standard = 0;

    /// <summary>A camera RAW container decoded through its embedded preview.</summary>
    public const byte RawPreview = 1;

    /// <summary>A camera RAW container decoded at full resolution by the LibRaw full decoder (not the embedded preview, not the WIC RAW codec); never RAM- or disk-cached.</summary>
    public const byte RawFullDecode = 2;
}

/// <summary>Identity of a decoded bitmap, including its source version and decode quality.</summary>
// Constructor is private so Create(...) is the only way to build a valid instance;
// a public one would let callers bypass the full-path/uppercase normalization below.
public readonly record struct ImageCacheKey
{
    public string Path { get; }
    public long Length { get; }
    public long LastWriteUtcTicks { get; }
    public bool IsOriginal { get; }

    /// <summary>Decode box width (displayed pixels); 0 = unconstrained. Always 0 for Original.</summary>
    public int TargetWidth { get; }

    /// <summary>Decode box height (displayed pixels); 0 = unconstrained (width-only). Always 0 for Original.</summary>
    public int TargetHeight { get; }
    public bool OrientationApplied { get; }
    public DecoderBackend Backend { get; }
    public byte SourceKind { get; }

    /// <summary>The decode box this key was built for (<see cref="DecodeBox.Unbounded"/> for Original).</summary>
    public DecodeBox TargetBox => new(TargetWidth, TargetHeight);

    private ImageCacheKey(string path, long length, long lastWriteUtcTicks, bool isOriginal, DecodeBox box, bool orientationApplied = true, DecoderBackend backend = DecoderBackend.Wpf, byte sourceKind = 0)
    {
        Path = path;
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
        IsOriginal = isOriginal;
        // Original mode always decodes at full size, whatever box the caller passed.
        TargetWidth = isOriginal ? 0 : box.Width;
        TargetHeight = isOriginal ? 0 : box.Height;
        OrientationApplied = orientationApplied;
        Backend = backend;
        SourceKind = sourceKind;
    }

    /// <summary>Width-only key (height unconstrained); kept for thumbnails-style/legacy callers and tests.</summary>
    public static ImageCacheKey Create(string path, bool isOriginal, int targetWidth, bool orientationApplied = true, DecoderBackend backend = DecoderBackend.Wpf, byte sourceKind = 0) =>
        Create(new FileInfo(path), isOriginal, new DecodeBox(targetWidth, 0), orientationApplied, backend, sourceKind);

    public static ImageCacheKey Create(string path, bool isOriginal, DecodeBox box, bool orientationApplied = true, DecoderBackend backend = DecoderBackend.Wpf, byte sourceKind = 0) =>
        Create(new FileInfo(path), isOriginal, box, orientationApplied, backend, sourceKind);

    /// <summary>Reuses a FileInfo the caller already fetched instead of stat-ing the path again.</summary>
    public static ImageCacheKey Create(FileInfo info, bool isOriginal, DecodeBox box, bool orientationApplied = true, DecoderBackend backend = DecoderBackend.Wpf, byte sourceKind = 0)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.Exists)
        {
            var fullName = info.FullName;
            throw UserFacingError.Localized(new FileNotFoundException("Image source no longer exists", fullName), () => Tr.ErrIoSourceNoLongerExists(fullName));
        }
        var fullPath = System.IO.Path.GetFullPath(info.FullName).ToUpperInvariant();
        return new ImageCacheKey(fullPath, info.Length, info.LastWriteTimeUtc.Ticks, isOriginal, box, orientationApplied, backend, sourceKind);
    }

    public static ImageCacheKey Create(CatalogEntry entry, bool isOriginal, DecodeBox box, bool orientationApplied = true, DecoderBackend backend = DecoderBackend.Wpf, byte sourceKind = 0)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length is null || entry.LastWriteUtc is null)
            return Create(entry.Path, isOriginal, box, orientationApplied, backend, sourceKind);
        var fullPath = System.IO.Path.GetFullPath(entry.Path).ToUpperInvariant();
        return new ImageCacheKey(fullPath, entry.Length.Value, entry.LastWriteUtc.Value.Ticks, isOriginal, box, orientationApplied, backend, sourceKind);
    }

    /// <summary>
    /// Builds the "Original loading mode" key for the same source identity as <paramref name="source"/>,
    /// without re-stating the file. Used when the caller already holds a key for this source (e.g. from
    /// the current navigation) and only needs the full-resolution dimensions cache entry.
    /// </summary>
    public static ImageCacheKey CreateOriginal(ImageCacheKey source) =>
        new(source.Path, source.Length, source.LastWriteUtcTicks, isOriginal: true, DecodeBox.Unbounded, source.OrientationApplied, source.Backend, source.SourceKind);

    /// <summary>Builds an original-quality key while selecting a distinct source representation, such as a RAW full decode.</summary>
    public static ImageCacheKey CreateOriginal(ImageCacheKey source, byte sourceKind) =>
        new(source.Path, source.Length, source.LastWriteUtcTicks, isOriginal: true, DecodeBox.Unbounded,
            source.OrientationApplied, source.Backend, sourceKind);

    public bool MatchesCurrentSource()
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists && info.Length == Length && info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
