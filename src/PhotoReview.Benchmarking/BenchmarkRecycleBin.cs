using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Benchmarking;

/// <summary>
/// The "delete" benchmark workloads act on a temp COPY of a photo, but each iteration would still add an item to the user's real
/// Recycle Bin (dozens per profile, and the bin cannot be cleaned up safely). By default the delete is therefore a direct
/// <see cref="File.Delete"/> of the temp copy; set <c>PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN=1</c> to measure the real shell
/// operation and accept the leftovers. Timings of the two modes are not comparable.
/// </summary>
public static class BenchmarkRecycleBin
{
    public const string RealBinEnvironmentVariable = "PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN";

    public static bool UseRealBin =>
        string.Equals(Environment.GetEnvironmentVariable(RealBinEnvironmentVariable), "1", StringComparison.Ordinal);

    /// <param name="realBin">The production bin, used only when the opt-in variable is set.</param>
    public static IRecycleBin Create(Func<IRecycleBin> realBin)
    {
        ArgumentNullException.ThrowIfNull(realBin);
        return UseRealBin ? realBin() : new TempCopyDeleter();
    }

    private sealed class TempCopyDeleter : IRecycleBin
    {
        public void SendToRecycleBin(string path) => File.Delete(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
