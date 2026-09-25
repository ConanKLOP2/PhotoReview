using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Benchmarking;

/// <summary>
/// One workload iteration's real behavior, shared by the WPF benchmark window
/// (<see cref="BenchmarkWindow"/>) and the CLI runner (PhotoReview.Benchmark.Cli/Program.cs) so both
/// front ends exercise the same correctness guard, file-action mapping and preload warm-up
/// instead of the CLI silently falling back to a decode-only stand-in that can misreport
/// results for profiles the UI already refuses to run without a real check.
/// </summary>
public static class BenchmarkWorkloadRunner
{
    /// <summary>
    /// Runs one profile end to end with the setup/measure split (R2-F-14): each iteration is prepared by
    /// <see cref="PrepareIterationAsync"/> (untimed) and only its measure step is sampled. The CLI runner uses
    /// this so its samples match the WPF window's instead of timing each iteration's own setup I/O.
    /// </summary>
    public static Task<BenchmarkReport> RunProfileAsync(string folder, BenchmarkProfile profile,
        BenchmarkImageExecutor executor, string[] files, Random random, IRecycleBin recycleBin,
        IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default) =>
        RunProfileAsync(folder, profile,
            (workload, iteration, token) => PrepareIterationAsync(executor, files, profile, workload, iteration, random, recycleBin, token),
            progress, timeProvider: null, ct);

    /// <summary>Test seam for <see cref="RunProfileAsync(string, BenchmarkProfile, BenchmarkImageExecutor, string[], Random, IRecycleBin, IProgress{BenchmarkProgress}?, CancellationToken)"/>: a fake prepare step and clock.</summary>
    internal static Task<BenchmarkReport> RunProfileAsync(string folder, BenchmarkProfile profile,
        Func<BenchmarkWorkload, int, CancellationToken, Task<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>>> prepare,
        IProgress<BenchmarkProgress>? progress, TimeProvider? timeProvider, CancellationToken ct) =>
        BenchmarkEngine.RunPreparedAsync(folder, profile,
            (_, workload, iteration, token) => prepare(workload, iteration, token),
            progress, timeProvider, ct);

    public static async Task<(bool Correct, ReviewMetricsSnapshot? Metrics)> RunIterationAsync(
        BenchmarkImageExecutor executor, string[] files, BenchmarkProfile profile, BenchmarkWorkload workload,
        int iteration, Random random, IRecycleBin recycleBin, CancellationToken ct)
    {
        var measure = await PrepareIterationAsync(executor, files, profile, workload, iteration, random, recycleBin, ct).ConfigureAwait(false);
        return await measure().ConfigureAwait(false);
    }

    /// <summary>
    /// R2-F-14: does the untimed setup of one iteration (scratch-file copy, cold-cache eviction) and returns the measure
    /// step, which is the only part <see cref="BenchmarkEngine.RunPreparedAsync"/> times. The measure step must be invoked
    /// exactly once: for file-action iterations it also removes the scratch files.
    /// </summary>
    public static async Task<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>> PrepareIterationAsync(
        BenchmarkImageExecutor executor, string[] files, BenchmarkProfile profile, BenchmarkWorkload workload,
        int iteration, Random random, IRecycleBin recycleBin, CancellationToken ct)
    {
        if (BenchmarkProfiles.IsNotImplemented(profile))
        {
            // These profiles would otherwise just decode a plain sequential file with no
            // check of Explorer native order or of cache clear/rebuild behavior, so they
            // could report Pass without ever exercising what their name claims. Refuse to
            // run rather than keep reporting a misleading Pass; a real check needs to drive
            // ExplorerOrderService/ClearCache from here.
            throw new NotSupportedException(
                $"Benchmark profile '{profile.Id}' does not implement a real {profile.Name} check yet; " +
                "a decode-only stand-in would misreport results, so it refuses to run.");
        }

        if (workload == BenchmarkWorkload.FileAction)
            return await PrepareFileActionAsync(executor, files, profile, iteration, recycleBin, ct).ConfigureAwait(false);

        if (workload == BenchmarkWorkload.FirstFrame)
        {
            var path = SelectFile(files, workload, iteration, random);
            executor.EvictForColdDecode(path); // untimed: clearing the disk cache is not part of the first frame
            return async () =>
            {
                var image = await executor.DecodeAsync(path, ct).ConfigureAwait(false);
                return (image.PixelWidth > 0 && image.PixelHeight > 0, executor.Metrics);
            };
        }

        if (workload is BenchmarkWorkload.Preload or BenchmarkWorkload.WarmNext)
        {
            // One navigation step per iteration, not N parallel decodes: this measures how
            // fast landing on the next/previous image feels, with the real PreloadScheduler
            // given a chance to have already warmed it from the previous iteration's
            // WarmPreloadAround call below -- the actual thing these two workloads exist to
            // measure.
            var center = SelectIndex(files.Length, workload, iteration, random);
            return async () =>
            {
                var image = await executor.DecodeAsync(files[center], ct).ConfigureAwait(false);
                executor.WarmPreloadAround(center);
                return (image.PixelWidth > 0 && image.PixelHeight > 0, executor.Metrics);
            };
        }

        var count = Math.Min(Math.Max(1, profile.Workers), files.Length);
        var selected = Array.ConvertAll(SelectParallelIndices(files.Length, profile, workload, iteration, random), i => files[i]);
        return async () =>
        {
            var results = new bool[selected.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, selected.Length),
                new ParallelOptions { MaxDegreeOfParallelism = count, CancellationToken = ct },
                async (idx, ct2) =>
                {
                    var image = await executor.DecodeAsync(selected[idx], ct2).ConfigureAwait(false);
                    results[idx] = image.PixelWidth > 0 && image.PixelHeight > 0;
                }).ConfigureAwait(false);
            return (results.All(r => r), executor.Metrics);
        };
    }

    private static async Task<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>> PrepareFileActionAsync(
        BenchmarkImageExecutor executor, string[] files, BenchmarkProfile profile, int iteration, IRecycleBin recycleBin, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Action-" + Guid.NewGuid().ToString("N") + ".bin");
        var moved = temp + ".moved";
        var copied = temp + ".copy";
        void Cleanup()
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort cleanup of benchmark temp file */ }
            try { if (File.Exists(moved)) File.Delete(moved); } catch { /* best-effort cleanup of benchmark temp file */ }
            try { if (File.Exists(copied)) File.Delete(copied); } catch { /* best-effort cleanup of benchmark temp file */ }
        }
        try
        {
            // Untimed setup: a full read + write of the source photo (R2-F-14).
            await File.WriteAllBytesAsync(temp, await File.ReadAllBytesAsync(files[iteration % files.Length], ct), ct).ConfigureAwait(false);
        }
        catch
        {
            Cleanup();
            throw;
        }

        return async () =>
        {
            try
            {
                // Start the real decode before mutating the file.  Awaiting it here would
                // turn this workload into "decode then action" and could never expose the
                // move/delete/copy lifetime race that these profiles are intended to measure.
                // DecodeAsync queues the production decode on its worker, so the mutation is
                // deliberately issued while that operation is in flight; awaiting the task
                // afterwards keeps the result and failure semantics observable to the engine.
                var decodeTask = executor.DecodeAsync(temp, ct);
                // Each action profile performs the operation its name promises instead of every
                // Move/Delete/Copy/Interleaved profile running the same move+delete regardless of Id.
                var op = profile.Id switch
                {
                    "action-delete" => "delete",
                    "action-copy" => "copy",
                    "action-interleaved" => (iteration % 3) switch { 0 => "move", 1 => "delete", _ => "copy" },
                    _ => "move",
                };
                switch (op)
                {
                    case "move": File.Move(temp, moved); File.Delete(moved); break;
                    // The bin is injected: production passes WindowsRecycleBin (the real "Delete" action
                    // path); tests pass a fake so the gate never touches the user's real Recycle Bin.
                    case "delete": recycleBin.SendToRecycleBin(temp); break;
                    case "copy": File.Copy(temp, copied, overwrite: true); break;
                }
                // A delete or move can legitimately win the race before the decoder opens
                // the file.  That is the behavior this workload is measuring; consume the
                // task and report the filesystem contract below instead of converting the
                // expected race into an unhandled benchmark exception.  Cancellation still
                // propagates so a cancelled run cannot be reported as a successful action.
                try
                {
                    _ = await decodeTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (IOException) { }
                // A delete/move racing the decoder's open surfaces as "access denied" while the file is pending delete.
                catch (UnauthorizedAccessException) { }
                // The correctness check must match what each operation promises: move/delete
                // must remove the source, copy must leave it in place.
                var sourceExistsAfter = File.Exists(temp);
                var expectedSourceExists = op == "copy";
                return (sourceExistsAfter == expectedSourceExists, executor.Metrics);
            }
            finally
            {
                Cleanup();
            }
        };
    }

    // string.GetHashCode() is randomized per process by design in .NET, so seeding with it
    // made the same profile pick a different file sequence on every run, undermining
    // "benchmark" reproducibility. This hash is stable across processes.
    public static Random CreateSeededRandom(string profileId) => new(StableHash(profileId));

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value) hash = hash * 31 + c;
            return hash;
        }
    }

    /// <summary>File indices one parallel-decode iteration (Random/Sequential/...) decodes; consumes <paramref name="random"/>.</summary>
    internal static int[] SelectParallelIndices(int fileCount, BenchmarkProfile profile, BenchmarkWorkload workload, int iteration, Random random)
    {
        var count = Math.Min(Math.Max(1, profile.Workers), fileCount);
        return Enumerable.Range(0, count).Select(o => SelectIndex(fileCount, workload, iteration + o, random)).ToArray();
    }

    private static string SelectFile(string[] files, BenchmarkWorkload workload, int iteration, Random random) =>
        files[SelectIndex(files.Length, workload, iteration, random)];

    private static int SelectIndex(int fileCount, BenchmarkWorkload workload, int iteration, Random random) => workload switch
    {
        BenchmarkWorkload.FirstFrame => 0,
        BenchmarkWorkload.Random => random.Next(fileCount),
        BenchmarkWorkload.WarmNext => iteration % 2 == 0 ? iteration / 2 % fileCount : fileCount - 1 - iteration / 2 % fileCount,
        BenchmarkWorkload.Preload => iteration * 2 % fileCount,
        _ => iteration % fileCount,
    };
}
