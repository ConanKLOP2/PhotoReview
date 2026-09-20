using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PhotoReview.TestSupport.Windows.Fixtures;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TS02: Verify fixture generation performance and size.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FixturePerfTest
{
    [Fact(DisplayName = "TS02: Fixture generation is fast and small")]
    public void FixturePerfMetrics()
    {
        // Measure fixture generation
        var sw = Stopwatch.StartNew();
        var folder = PhotoFolderBuilder.BuildFolder(20);
        sw.Stop();

        Assert.NotNull(folder);
        Assert.True(Directory.Exists(folder));

        // Verify file count (>= 20 to account for potential caching)
        var jpgFiles = Directory.GetFiles(folder, "*.jpg");
        Assert.True(jpgFiles.Length >= 20, $"Expected >= 20 JPEGs, got {jpgFiles.Length}");

        // Measure sizes
        var jpgSizes = jpgFiles.Select(f => new FileInfo(f).Length).ToList();
        var totalJpgSize = jpgSizes.Sum();
        var totalJpgSizeMB = totalJpgSize / (1024.0 * 1024.0);

        var allFiles = new DirectoryInfo(folder).GetFiles("*", SearchOption.TopDirectoryOnly);
        var allSize = allFiles.Sum(f => f.Length);
        var allSizeMB = allSize / (1024.0 * 1024.0);

        // Output metrics
        var output = new List<string>
        {
            $"Fixture generation time: {sw.ElapsedMilliseconds} ms (~{sw.Elapsed.TotalSeconds:F2} s)",
            $"JPEG count: {jpgFiles.Length}",
            $"Average JPEG size: {jpgSizes.Average() / (1024.0 * 1024.0):F3} MB",
            $"Total JPEG size: {totalJpgSizeMB:F2} MB",
            $"Total folder size: {allSizeMB:F2} MB",
        };

        foreach (var line in output)
        {
            System.Console.WriteLine(line);
        }

        // Verify performance targets
        Assert.True(sw.ElapsedMilliseconds <= 3000, $"Fixture should be built in <= 3s, actual: {sw.Elapsed.TotalSeconds:F2}s");
        Assert.True(allSizeMB <= 60, $"Fixture should be <= 60 MB, actual: {allSizeMB:F2} MB");

        // Show individual sizes (first 10)
        System.Console.WriteLine("\nFirst 10 JPEG sizes:");
        foreach (var (file, i) in jpgFiles.Take(10).Select((f, i) => (f, i)))
        {
            var size = new FileInfo(file).Length / (1024.0 * 1024.0);
            System.Console.WriteLine($"  {Path.GetFileName(file)}: {size:F3} MB");
        }
    }
}
