using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.TestSupport;

/// <summary>
/// Test-only convenience overloads of <see cref="ReviewCatalog.Reset(IEnumerable{CatalogEntry}, RawPairMode, IEnumerable{string}?)"/>
/// (the production API takes entries plus a pair mode; tests mostly seed plain paths).
/// </summary>
public static class ReviewCatalogTestExtensions
{
    /// <summary>Resets the catalog with plain paths (blank paths skipped, no capture groups).</summary>
    public static void Reset(this ReviewCatalog catalog, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(paths);
        catalog.Reset(paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new CatalogEntry(p)), RawPairMode.Separate);
    }

    /// <summary>Resets the catalog with entries, without collapsing JPEG+RAW pairs.</summary>
    public static void Reset(this ReviewCatalog catalog, IEnumerable<CatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Reset(entries, RawPairMode.Separate);
    }
}
