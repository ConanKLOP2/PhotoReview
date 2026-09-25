using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// The "delete" benchmark workloads act on a temp COPY of a photo, but each iteration would still add an item to the user's real
/// Recycle Bin (dozens per profile, and the bin cannot be cleaned up safely). By default the CLI therefore deletes the temp copy
/// directly; set <c>PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN=1</c> to measure the real shell operation and accept the leftovers.
/// </summary>
internal static class BenchmarkRecycleBin
{
    public const string RealBinEnvironmentVariable = "PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN";

    public static IRecycleBin Create() =>
        string.Equals(Environment.GetEnvironmentVariable(RealBinEnvironmentVariable), "1", StringComparison.Ordinal)
            ? PhotoReview.Platform.Windows.WindowsRecycleBin.Instance
            : new TempCopyDeleter();

    private sealed class TempCopyDeleter : IRecycleBin
    {
        public void SendToRecycleBin(string path) => File.Delete(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
