using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PhotoReview.TestSupport.Windows.Fixtures;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TS02: Verify fixture generation size/shape (gate) and cost (Slow).
/// </summary>
/// <remarks>
/// TC09: the original test asserted wall-clock time (&lt;= 3 s) in the gate. Under full-suite parallel
/// load that is non-deterministic: the builder is a process-wide Lazy singleton, so the timed call may
/// pay for encoding three 24-48 MP master JPEGs, or wait on another test's build, or be a cache hit —
/// and all of it competes for cores with the rest of the suite. The gate now asserts only
/// deterministic properties (file count, folder composition, total size). The cost budget moved to a
/// Slow-category test that measures the calling thread's CPU time, which excludes time spent
/// descheduled or blocked on the builder lock.
/// </remarks>
[Trait("Category", "HotPath")]
public sealed class FixturePerfTest
{
    private const int ImageCount = 20;

    [Fact(DisplayName = "TS02: Fixture is well-formed and within size budget")]
    public void FixtureShapeAndSize()
    {
        var folder = PhotoFolderBuilder.BuildFolder(ImageCount);

        Assert.NotNull(folder);
        Assert.True(Directory.Exists(folder));

        // >= ImageCount: the process-wide cache may already hold more images from other tests.
        var jpgFiles = Directory.GetFiles(folder, "photo_*.jpg");
        Assert.True(jpgFiles.Length >= ImageCount, $"Expected >= {ImageCount} photo JPEGs, got {jpgFiles.Length}");
        Assert.All(jpgFiles, f => Assert.True(new FileInfo(f).Length > 1024, $"{Path.GetFileName(f)} is suspiciously small"));

        // Edge-case files every hot-path consumer relies on.
        Assert.True(File.Exists(Path.Combine(folder, "sample.png")));
        Assert.True(File.Exists(Path.Combine(folder, "corrupt.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));

        // Size budget, scaled per image so a cache holding more than ImageCount photos is judged fairly.
        var allFiles = new DirectoryInfo(folder).GetFiles("*", SearchOption.TopDirectoryOnly);
        var allSizeMB = allFiles.Sum(f => f.Length) / (1024.0 * 1024.0);
        var budgetMB = 60.0 * jpgFiles.Length / ImageCount;
        Console.WriteLine($"JPEG count: {jpgFiles.Length}, total folder size: {allSizeMB:F2} MB (budget {budgetMB:F0} MB)");
        Assert.True(allSizeMB <= budgetMB, $"Fixture should be <= {budgetMB:F0} MB, actual: {allSizeMB:F2} MB");
    }

    [Fact(DisplayName = "TS02: Fixture generation CPU cost is bounded")]
    [Trait("Category", "Slow")]
    public void FixtureGenerationCpuBudget()
    {
        // Thread CPU time, not wall-clock: stable under parallel load. If another test already built
        // the singleton this measures the cache-hit path, which must also be within budget.
        var before = CurrentThreadCpuTime();
        var folder = PhotoFolderBuilder.BuildFolder(ImageCount);
        var cpu = CurrentThreadCpuTime() - before;

        Console.WriteLine($"Fixture generation thread CPU time: {cpu.TotalMilliseconds:F0} ms");
        Assert.True(Directory.Exists(folder));
        // Cold build measured at ~1-2 s alone; 10 s leaves room for slow CI hardware.
        Assert.True(cpu <= TimeSpan.FromSeconds(10), $"Fixture build should use <= 10 s CPU, actual: {cpu.TotalSeconds:F2} s");
    }

    private static TimeSpan CurrentThreadCpuTime()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            throw new InvalidOperationException($"GetThreadTimes failed: {Marshal.GetLastWin32Error()}");
        return TimeSpan.FromTicks(kernel + user); // FILETIME units are 100 ns, same as TimeSpan ticks
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
