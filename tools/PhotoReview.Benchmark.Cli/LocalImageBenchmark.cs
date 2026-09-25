using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Catalog;
using PhotoReview.PerfAnalysis;

internal static class LocalImageBenchmark
{
    public static async Task RunAsync(string folder, int workers = 8)
    {
        var files = Directory.EnumerateFiles(folder).Where(ImageFileTypes.IsSupported)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        if (files.Length < 2) throw new InvalidOperationException("Benchmark needs at least two images");
        var cache = new BoundedLruCache<ImageCacheKey, BitmapImage>(16L * 1024 * 1024 * 1024,
            b => Math.Max(1, b.PixelWidth * (long)b.PixelHeight * 4));
        var decodeTimes = new double[files.Length];
        var startup = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (index, _) =>
            {
                var path = files[index];
                var key = ImageCacheKey.Create(path, false, 2200);
                var swStart = Stopwatch.GetTimestamp();
                var bitmap = await Task.Run(() =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 2200; image.StreamSource = stream;
                    image.EndInit(); image.Freeze();
                    return image;
                });
                cache.Set(key, bitmap);
                decodeTimes[index] = Stopwatch.GetElapsedTime(swStart).TotalMilliseconds;
            });
        startup.Stop();
        var nextTimes = new double[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            var swStart = Stopwatch.GetTimestamp();
            var key = ImageCacheKey.Create(files[index], false, 2200);
            if (!cache.TryGet(key, out _)) throw new InvalidOperationException("Preloaded image was not in RAM cache");
            nextTimes[index] = Stopwatch.GetElapsedTime(swStart).TotalMilliseconds;
        }
        // Warm-next lookups are sub-millisecond: keep fractional ms (whole-ms timers printed them as 0/1).
        static double Percentile(double[] values, double fraction) => PerfStats.NearestRank(values.Order().ToArray(), fraction * 100.0);
        Console.WriteLine($"real-images={files.Length} workers={workers} preloadMs={startup.ElapsedMilliseconds} " +
            $"decodeMedianMs={Percentile(decodeTimes, .5):F2} decodeP95Ms={Percentile(decodeTimes, .95):F2} " +
            $"warmNextMedianMs={Percentile(nextTimes, .5):F3} warmNextP95Ms={Percentile(nextTimes, .95):F3} " +
            $"cacheMB={cache.CurrentSize / 1024 / 1024} workingSetMB={Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}");
    }
}
