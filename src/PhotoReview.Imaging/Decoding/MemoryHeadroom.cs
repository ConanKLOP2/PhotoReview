namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// The RAM-budget check shared by the decoders' upfront output-size guards (LibRaw's <c>DecodeMemoryGuard</c>, TurboJpeg's
/// output buffer): an estimated peak must fit into the memory the process can still use, so a legitimately huge original
/// (100+ MP) is decoded on a machine that has the RAM for it, while a header that claims more than the machine can hold is
/// refused before anything big is allocated.
/// </summary>
public static class MemoryHeadroom
{
    /// <summary>Output buffers below this never consult the memory guard (no GC info query on the common, small decode).</summary>
    public const long GuardThresholdBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Peak of a decode in units of the output buffer: the native BGRX scratch plus the WPF bitmap BitmapSource.Create copies it
    /// into (the source bytes are already resident and counted in the memory load).
    /// </summary>
    public const int OutputPeakFactor = 2;

    /// <summary>
    /// True when a decode whose output buffer is <paramref name="bufferLength"/> bytes fits (peak = <see cref="OutputPeakFactor"/> x the buffer,
    /// see <see cref="HasHeadroom"/>).
    /// </summary>
    public static bool OutputHasHeadroom(long bufferLength, long totalAvailableBytes, long memoryLoadBytes) =>
        HasHeadroom(bufferLength * OutputPeakFactor, totalAvailableBytes, memoryLoadBytes);

    /// <summary>True when <paramref name="estimatedBytes"/> fits into <paramref name="totalAvailableBytes"/> minus <paramref name="memoryLoadBytes"/>. An unknown total (&lt;= 0) never refuses.</summary>
    public static bool HasHeadroom(long estimatedBytes, long totalAvailableBytes, long memoryLoadBytes)
    {
        if (totalAvailableBytes <= 0) return true;
        return estimatedBytes <= totalAvailableBytes - Math.Max(0, memoryLoadBytes);
    }

    /// <summary>Production memory reading: (total available, current load) from the GC (physical RAM or the container limit; memory in use as of the last GC).</summary>
    public static (long TotalAvailable, long Load) ReadGcMemoryInfo()
    {
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, info.MemoryLoadBytes);
    }
}
