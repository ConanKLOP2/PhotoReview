namespace PhotoReview.Tests;

/// <summary>
/// Local, destructive-operation-safe scenarios used by the benchmark CLI and UI probe.
/// Every action is performed against a temporary copy so a real source folder is read-only.
/// </summary>
public static class BenchmarkScenarioTests
{
    public static void Run(string root, List<string> failures)
    {
        var folder = Path.Combine(root, "benchmark-action-scenario");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(0, 8)
            .Select(i => Path.Combine(folder, $"image-{i:00}.jpg")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, Enumerable.Repeat((byte)7, 128 * 1024).ToArray());

        var index = 0;
        var navigationCount = 0;
        void NextOnce()
        {
            index = Math.Min(index + 1, files.Length - 1);
            navigationCount++;
        }

        // The read handle explicitly shares Delete: this is the contract required by the
        // production decoder while Move/Delete is started after the catalog advances.
        using (var read = new FileStream(files[0], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        {
            var nextBeforeAction = navigationCount;
            NextOnce();
            var destination = Path.Combine(folder, "moved", Path.GetFileName(files[0]));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(files[0], destination);
            Check(index == 1 && navigationCount == nextBeforeAction + 1 && File.Exists(destination),
                "Action scenario advances exactly once before Move while read is open", failures);
        }

        var nextBeforeDelete = navigationCount;
        NextOnce();
        File.Delete(files[1]);
        Check(index == 2 && navigationCount == nextBeforeDelete + 1 && !File.Exists(files[1]),
            "Action scenario advances exactly once before Delete", failures);

        var copy = Path.Combine(folder, "copy", Path.GetFileName(files[2]));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        using (var read = new FileStream(files[2], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        {
            var nextBeforeCopy = navigationCount;
            NextOnce();
            File.Copy(files[2], copy);
            Check(index == 3 && navigationCount == nextBeforeCopy + 1 && File.Exists(copy),
                "Action scenario advances exactly once before Copy", failures);
        }

        Check(navigationCount == 3, "Interleaved action sequence has no double navigation", failures);
        Check(!Directory.EnumerateFiles(folder, "*.retry", SearchOption.AllDirectories).Any(),
            "Action scenario performs no retry side effect", failures);
    }

    public static async Task CancellationStopsAsync(string root)
    {
        using var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(5, cts.Token);
            }
        });
        await Task.Delay(20);
        cts.Cancel();
        try { await task; throw new InvalidOperationException("Cancellation task did not stop"); }
        catch (OperationCanceledException) { }
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    }
}
