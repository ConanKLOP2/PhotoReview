using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core.Caching;

internal static class LocalImageBenchmark
{
    public static async Task RunAsync(string folder, int workers = 8)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
        var files = Directory.EnumerateFiles(folder).Where(p => supported.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        if (files.Length < 2) throw new InvalidOperationException("Benchmark needs at least two images");
        var cache = new BoundedLruCache<ImageCacheKey, BitmapImage>(16L * 1024 * 1024 * 1024,
            b => Math.Max(1, b.PixelWidth * (long)b.PixelHeight * 4));
        var decodeTimes = new long[files.Length];
        var startup = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (index, _) =>
            {
                var path = files[index];
                var key = ImageCacheKey.Create(path, false, 2200);
                var sw = Stopwatch.StartNew();
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
                decodeTimes[index] = sw.ElapsedMilliseconds;
            });
        startup.Stop();
        var nextTimes = new long[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            var sw = Stopwatch.StartNew();
            var key = ImageCacheKey.Create(files[index], false, 2200);
            if (!cache.TryGet(key, out _)) throw new InvalidOperationException("Preloaded image was not in RAM cache");
            nextTimes[index] = sw.ElapsedMilliseconds;
        }
        static long Percentile(long[] values, double fraction)
        {
            var sorted = values.Order().ToArray();
            return sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
        }
        Console.WriteLine($"real-images={files.Length} workers={workers} preloadMs={startup.ElapsedMilliseconds} " +
            $"decodeMedianMs={Percentile(decodeTimes, .5)} decodeP95Ms={Percentile(decodeTimes, .95)} " +
            $"warmNextMedianMs={Percentile(nextTimes, .5)} warmNextP95Ms={Percentile(nextTimes, .95)} " +
            $"cacheMB={cache.CurrentSize / 1024 / 1024} workingSetMB={Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}");
    }
}
