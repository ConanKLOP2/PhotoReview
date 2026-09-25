using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

public sealed class BenchmarkRecycleBinTests
{
    [Fact(DisplayName = "By default the benchmark delete removes the temp copy directly and never touches the real Recycle Bin")]
    public void Create_Default_DeletesTempCopyWithoutRealBin()
    {
        Assert.False(BenchmarkRecycleBin.UseRealBin, $"{BenchmarkRecycleBin.RealBinEnvironmentVariable} must not be set for the test run.");
        var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-BenchBin-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(temp, [1]);
        try
        {
            var bin = BenchmarkRecycleBin.Create(() => throw new InvalidOperationException("the real bin must not be created"));

            bin.SendToRecycleBin(temp);

            Assert.False(File.Exists(temp));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
