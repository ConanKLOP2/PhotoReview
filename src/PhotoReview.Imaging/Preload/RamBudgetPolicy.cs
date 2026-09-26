using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;

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

    /// <summary>Largest share of physical RAM a single in-memory cache budget may claim (IMG-11).</summary>
    public const double MaxPhysicalMemoryShare = 0.5;

    /// <summary>Largest share of physical RAM the optional source-bytes cache may claim (R2-A-06).</summary>
    public const double MaxSourceBytesShare = 0.2;

    /// <summary>
    /// Physical memory the runtime may use (physical RAM, or the container/GC hard limit when one is
    /// set); 0 when unknown.
    /// </summary>
    public static long GetPhysicalMemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// Clamps a requested cache budget to <see cref="MaxPhysicalMemoryShare"/> of physical RAM so a
    /// 16 GiB default (tuned for the 32 GB dev box) cannot over-commit a smaller machine. An unknown
    /// physical size (&lt;= 0) leaves the request unchanged.
    /// </summary>
    public static long ClampToPhysicalMemory(long requestedBytes, long physicalBytes)
    {
        if (physicalBytes <= 0) return requestedBytes;
        var limit = (long)(physicalBytes * MaxPhysicalMemoryShare);
        return Math.Min(requestedBytes, limit);
    }

    // ---- User-configurable cache share (percent of physical RAM) ----

    /// <summary>Decode box (UHD) the minimum cache percent is sized for.</summary>
    public const int MinimumBudgetBoxWidth = 3840;

    /// <inheritdoc cref="MinimumBudgetBoxWidth"/>
    public const int MinimumBudgetBoxHeight = 2160;

    /// <summary>Previews in the full preload window: current image plus the forward and backward lookahead.</summary>
    public const int PreloadWindowImageCount = PreloadOrderService.ForwardLookahead + PreloadOrderService.BackwardLookahead + 1;

    /// <summary>
    /// Bytes needed to hold the whole default preload window (<see cref="PreloadWindowImageCount"/> previews decoded
    /// to a <see cref="MinimumBudgetBoxWidth"/> x <see cref="MinimumBudgetBoxHeight"/> box at 4 bytes/pixel, ~1.27 GiB).
    /// A smaller cache would evict previews the scheduler is still preloading. feat/preload-window-setting: shortcut
    /// for <see cref="MinimumPreviewWindowBytes(PreloadWindow)"/> with <see cref="PreloadWindow.Default"/>.
    /// </summary>
    public static long MinimumPreviewWindowBytes() => MinimumPreviewWindowBytes(PreloadWindow.Default);

    /// <summary>
    /// Bytes needed to hold the whole preload <paramref name="window"/> (<see cref="PreloadWindow.ImageCount"/> previews
    /// decoded to a <see cref="MinimumBudgetBoxWidth"/> x <see cref="MinimumBudgetBoxHeight"/> box at 4 bytes/pixel). A
    /// smaller cache would evict previews the scheduler is still preloading.
    /// </summary>
    public static long MinimumPreviewWindowBytes(PreloadWindow window) =>
        (long)window.ImageCount * MinimumBudgetBoxWidth * MinimumBudgetBoxHeight * 4;

    /// <summary>
    /// Smallest selectable cache percent on a device with <paramref name="physicalBytes"/> of RAM for the default preload
    /// window: the percent (rounded up) that holds <see cref="MinimumPreviewWindowBytes()"/>, at least
    /// <see cref="PerformanceOptions.MinImageCacheRamPercent"/> and never above <see cref="PerformanceOptions.MaxImageCacheRamPercent"/>.
    /// Unknown RAM (&lt;= 0) gives the absolute floor.
    /// </summary>
    public static int MinimumCachePercent(long physicalBytes) => MinimumCachePercent(physicalBytes, PreloadWindow.Default);

    /// <summary>
    /// Smallest selectable cache percent on a device with <paramref name="physicalBytes"/> of RAM for a user-configured
    /// <paramref name="window"/>: the percent (rounded up) that holds <see cref="MinimumPreviewWindowBytes(PreloadWindow)"/>,
    /// at least <see cref="PerformanceOptions.MinImageCacheRamPercent"/> and never above
    /// <see cref="PerformanceOptions.MaxImageCacheRamPercent"/>. Unknown RAM (&lt;= 0) gives the absolute floor.
    /// </summary>
    public static int MinimumCachePercent(long physicalBytes, PreloadWindow window)
    {
        if (physicalBytes <= 0) return PerformanceOptions.MinImageCacheRamPercent;
        var percent = (MinimumPreviewWindowBytes(window) * 100 + physicalBytes - 1) / physicalBytes; // ceiling, no overflow for real RAM sizes
        return (int)Math.Clamp(percent, PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
    }

    /// <summary>Clamps a requested percent to [<see cref="MinimumCachePercent"/>, <see cref="PerformanceOptions.MaxImageCacheRamPercent"/>].</summary>
    public static int ClampCachePercent(int requestedPercent, long physicalBytes) =>
        Math.Clamp(requestedPercent, MinimumCachePercent(physicalBytes), PerformanceOptions.MaxImageCacheRamPercent);

    /// <summary><paramref name="percent"/> % of <paramref name="physicalBytes"/> (exact integer math, no overflow); 0 for unknown RAM.</summary>
    public static long BytesForPercent(int percent, long physicalBytes)
    {
        if (physicalBytes <= 0 || percent <= 0) return 0;
        return physicalBytes / 100 * percent + physicalBytes % 100 * percent / 100;
    }

    /// <summary>
    /// Preview cache budget for a user percent: the clamped percent of physical RAM minus whatever the source-bytes cache
    /// holds, so both caches together stay within the chosen share (the R2-A-06 rule with the user percent instead of
    /// <see cref="MaxPhysicalMemoryShare"/>). Never below 1 byte (the LRU cache rejects a zero capacity).
    /// Requires known physical RAM; callers fall back to <see cref="ClampPreviewToPhysicalMemory"/> otherwise.
    /// </summary>
    public static long PreviewBytesForPercent(int requestedPercent, long physicalBytes, long sourceBytesCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalBytes);
        var budget = BytesForPercent(ClampCachePercent(requestedPercent, physicalBytes), physicalBytes);
        return Math.Max(1, budget - Math.Max(0, sourceBytesCapacity));
    }

    /// <summary>
    /// Source-bytes cache request for a user percent: the usual <see cref="ClampSourceBytesToPhysicalMemory"/> limit, and
    /// additionally small enough to leave <see cref="MinimumPreviewWindowBytes()"/> of the percent budget to the preview cache
    /// (a low percent must not let the source-bytes cache starve the previews). 0 = no room for a source-bytes cache.
    /// Unknown physical RAM (&lt;= 0) returns the request unchanged.
    /// </summary>
    public static long SourceBytesForPercent(long requestedBytes, int requestedPercent, long physicalBytes)
    {
        if (physicalBytes <= 0) return requestedBytes;
        var budget = BytesForPercent(ClampCachePercent(requestedPercent, physicalBytes), physicalBytes);
        var room = Math.Max(0, budget - MinimumPreviewWindowBytes());
        return Math.Min(ClampSourceBytesToPhysicalMemory(requestedBytes, physicalBytes), room);
    }

    /// <summary>Clamps the source-bytes cache budget to <see cref="MaxSourceBytesShare"/> of physical RAM (also bounded by the overall share).</summary>
    public static long ClampSourceBytesToPhysicalMemory(long requestedBytes, long physicalBytes)
    {
        if (physicalBytes <= 0) return requestedBytes;
        return Math.Min(ClampToPhysicalMemory(requestedBytes, physicalBytes), (long)(physicalBytes * MaxSourceBytesShare));
    }

    /// <summary>
    /// Clamps the preview cache so that it plus the source-bytes cache together stay within
    /// <see cref="MaxPhysicalMemoryShare"/> of physical RAM (R2-A-06). With no source-bytes cache this equals
    /// <see cref="ClampToPhysicalMemory"/>.
    /// </summary>
    public static long ClampPreviewToPhysicalMemory(long requestedBytes, long physicalBytes, long sourceBytesCapacity)
    {
        var clamped = ClampToPhysicalMemory(requestedBytes, physicalBytes);
        if (physicalBytes <= 0) return clamped;
        var remaining = Math.Max(0, ClampToPhysicalMemory(long.MaxValue, physicalBytes) - Math.Max(0, sourceBytesCapacity));
        return Math.Min(clamped, remaining);
    }

    public static long EstimateDecodedBytes(IEnumerable<RamBudgetEntry> entries, int targetWidth,
        double? measuredBytesPerPixel = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
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
        ArgumentNullException.ThrowIfNull(memoryProbe);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        var estimated = EstimateDecodedBytes(entries, targetWidth, measuredBytesPerPixel);
        var headroom = memoryProbe.HasHeadroom(PerformanceOptionsDefaults.PreloadMemoryLoadLimit, reserveBytes);
        return new RamBudgetDecision(estimated, capacityBytes, headroom,
            estimated <= capacityBytes && headroom);
    }

    /// <summary>
    /// Q-R17: margin on the measured mean preview size, so a folder whose later images are a bit
    /// larger than the sampled ones still fits.
    /// </summary>
    public const double MeasuredPreviewMargin = 1.25;

    /// <summary>
    /// Q-R17: decoded bytes the preview cache needs to hold every one of <paramref name="imageCount"/>
    /// images at <paramref name="targetBox"/>, without reading any file header.
    /// <list type="bullet">
    /// <item>Measured (<paramref name="measuredMeanPreviewBytes"/>, the mean size of previews already
    /// decoded at this box): mean x <see cref="MeasuredPreviewMargin"/>, capped at the box bound unless the
    /// measured images themselves exceed it (e.g. 16-bit PNG).</item>
    /// <item>Not measured yet, bounded box: <c>w x h x 4</c> per image -- previews are only ever
    /// scaled down into the box, so this is an upper bound for 4-byte pixels.</item>
    /// <item>Box unbounded on an axis (Original loading mode, width-only box): compressed source
    /// bytes x <see cref="JpegExpansionFactor"/> (the pre-Q-R17 estimate).</item>
    /// </list>
    /// </summary>
    public static long EstimateFolderPreviewBytes(int imageCount, DecodeBox targetBox, long totalSourceBytes,
        double? measuredMeanPreviewBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(imageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalSourceBytes);
        var bounded = targetBox.Width > 0 && targetBox.Height > 0;
        var boxBound = bounded ? (double)targetBox.Width * targetBox.Height * DefaultAverageBytesPerPixel : 0;
        double perImage;
        if (measuredMeanPreviewBytes is > 0)
        {
            var measured = measuredMeanPreviewBytes.Value;
            perImage = measured * MeasuredPreviewMargin;
            // Capped at the 4-byte box bound, unless the images already measure above it (16-bit pixels).
            if (bounded && measured <= boxBound) perImage = Math.Min(perImage, boxBound);
        }
        else if (bounded)
        {
            perImage = boxBound;
        }
        else
        {
            return checked((long)(totalSourceBytes * JpegExpansionFactor));
        }
        return checked((long)Math.Ceiling(imageCount * perImage));
    }

    public static bool ShouldPreloadWholeFolder(long totalSourceBytes, long capacityBytes, IMemoryProbe memoryProbe,
        long reserveBytes = PerformanceOptionsDefaults.MemoryReserveBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSourceBytes);
        return ShouldPreloadWholeFolderEstimate(checked((long)(totalSourceBytes * JpegExpansionFactor)),
            capacityBytes, memoryProbe, reserveBytes);
    }

    /// <summary>Whole-folder preload when an already computed estimate (see <see cref="EstimateFolderPreviewBytes"/>) fits the budget and RAM has headroom.</summary>
    public static bool ShouldPreloadWholeFolderEstimate(long estimatedBytes, long capacityBytes, IMemoryProbe memoryProbe,
        long reserveBytes = PerformanceOptionsDefaults.MemoryReserveBytes,
        double maximumLoad = PerformanceOptionsDefaults.PreloadMemoryLoadLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        ArgumentNullException.ThrowIfNull(memoryProbe);
        return estimatedBytes <= capacityBytes && memoryProbe.HasHeadroom(maximumLoad, reserveBytes);
    }

    private static class PerformanceOptionsDefaults
    {
        public const long MemoryReserveBytes = 2L * 1024 * 1024 * 1024;
        public const double PreloadMemoryLoadLimit = 0.80;
    }
}
