using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Preload;

/// <summary>Estimates decoded image memory before deciding whether a folder can be preloaded.</summary>
public sealed record RamBudgetEntry(long CompressedBytes, int? Width = null, int? Height = null, string? Extension = null)
{
    public RamBudgetEntry(long compressedBytes, int width, int height, string? extension = null)
        : this(compressedBytes, (int?)width, (int?)height, extension) { }
}

public sealed record RamBudgetDecision(long EstimatedDecodedBytes, long CapacityBytes, bool HasHeadroom, bool ShouldPreloadWholeFolder);

public static class RamBudgetPolicy
{
    public const double JpegExpansionFactor = 10;
    public const double PngExpansionFactor = 3;
    public const double DefaultAverageBytesPerPixel = 4;

    public static long EstimateDecodedBytes(IEnumerable<RamBudgetEntry> entries, int targetWidth,
        double? measuredBytesPerPixel = null)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        var fallbackBpp = measuredBytesPerPixel is > 0 ? measuredBytesPerPixel.Value : DefaultAverageBytesPerPixel;
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.CompressedBytes < 0) throw new ArgumentOutOfRangeException(nameof(entries));
            long estimate;
            if (entry.Width is > 0 && entry.Height is > 0)
            {
                var width = Math.Min(entry.Width.Value, targetWidth);
                var height = Math.Max(1, (int)Math.Round(entry.Height.Value * (double)width / entry.Width.Value));
                estimate = checked((long)(width * (double)height * fallbackBpp));
            }
            else
            {
                var factor = string.Equals(entry.Extension, ".png", StringComparison.OrdinalIgnoreCase)
                    ? PngExpansionFactor : JpegExpansionFactor;
                estimate = checked((long)(entry.CompressedBytes * factor));
            }
            total = checked(total + estimate);
        }
        return total;
    }

    public static RamBudgetDecision Decide(IEnumerable<RamBudgetEntry> entries, int targetWidth,
        long capacityBytes, IMemoryProbe memoryProbe, long reserveBytes = PerformanceOptionsDefaults.MemoryReserveBytes,
        double? measuredBytesPerPixel = null)
    {
        if (memoryProbe is null) throw new ArgumentNullException(nameof(memoryProbe));
        if (capacityBytes < 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        var estimated = EstimateDecodedBytes(entries, targetWidth, measuredBytesPerPixel);
        var headroom = memoryProbe.HasHeadroom(PerformanceOptionsDefaults.PreloadMemoryLoadLimit, reserveBytes);
        return new RamBudgetDecision(estimated, capacityBytes, headroom,
            estimated <= capacityBytes && headroom);
    }

    public static bool ShouldPreloadWholeFolder(long totalSourceBytes, long capacityBytes, IMemoryProbe memoryProbe,
        long reserveBytes = PerformanceOptionsDefaults.MemoryReserveBytes)
    {
        if (totalSourceBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalSourceBytes));
        if (memoryProbe is null) throw new ArgumentNullException(nameof(memoryProbe));
        var estimated = checked((long)(totalSourceBytes * JpegExpansionFactor));
        return estimated <= capacityBytes && memoryProbe.HasHeadroom(
            PerformanceOptionsDefaults.PreloadMemoryLoadLimit, reserveBytes);
    }

    private static class PerformanceOptionsDefaults
    {
        public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
        public const double PreloadMemoryLoadLimit = 0.80;
    }
}
