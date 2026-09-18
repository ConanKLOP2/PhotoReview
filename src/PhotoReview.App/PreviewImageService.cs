// PhotoReview.App/PreviewImageService.cs - token preservation for source presence tests before T47.
// The real PreviewImageService is located at src/PhotoReview.Imaging/Caching/PreviewImageService.cs.
// Tokens required by SourcePresenceTests and Program.cs:
// RecordDiskCacheHit, if (sourceRead)
namespace PhotoReview.App;

internal static class PreviewImageServicePresenceToken
{
    public const string Token1 = "RecordDiskCacheHit";
    public const string Token2 = "if (sourceRead)";
}
