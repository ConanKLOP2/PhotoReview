using System.Diagnostics;

namespace PhotoReview.Tests;

/// <summary>Deterministic filesystem probes for actions racing an open/read stream.</summary>
public static class FileActionConcurrencyTests
{
    public static void Run(string root, List<string> failures)
    {
        MoveWhileReadIsOpen(root, failures);
        DeleteWhileReadIsOpen(root, failures);
        CopyWhileReadIsOpen(root, failures);
        InterleavedSequence(root, failures);
    }

    private static void MoveWhileReadIsOpen(string root, List<string> failures)
    {
        var dir = NewCase(root, "move-read");
        var source = Path.Combine(dir, "01.jpg");
        var target = Path.Combine(dir, "moved", "01.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var watch = Stopwatch.StartNew();
        File.Move(source, target);
        watch.Stop();
        Check(!File.Exists(source) && File.Exists(target) && watch.Elapsed < TimeSpan.FromSeconds(1),
            "Move succeeds immediately while decoder keeps a delete-sharing read handle", failures);
    }

    private static void DeleteWhileReadIsOpen(string root, List<string> failures)
    {
        var dir = NewCase(root, "delete-read");
        var source = Path.Combine(dir, "01.jpg");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        File.Delete(source);
        Check(!File.Exists(source), "Delete succeeds immediately while decoder keeps a delete-sharing read handle", failures);
    }

    private static void CopyWhileReadIsOpen(string root, List<string> failures)
    {
        var dir = NewCase(root, "copy-read");
        var source = Path.Combine(dir, "01.jpg");
        var target = Path.Combine(dir, "copy", "01.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        File.Copy(source, target);
        Check(File.Exists(source) && File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "Copy preserves source and bytes while decoder is reading", failures);
    }

    private static void InterleavedSequence(string root, List<string> failures)
    {
        var dir = NewCase(root, "sequence-read");
        var files = Enumerable.Range(1, 5).Select(i => Path.Combine(dir, $"{i:00}.jpg")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [(byte)1, (byte)2]);
        Directory.CreateDirectory(Path.Combine(dir, "moved"));
        Directory.CreateDirectory(Path.Combine(dir, "copy"));
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
        {
            File.Move(files[0], Path.Combine(dir, "moved", "01.jpg"));
        }
        File.Delete(files[1]);
        File.Copy(files[2], Path.Combine(dir, "copy", "03.jpg"));
        var survivors = Directory.EnumerateFiles(dir, "*.jpg", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Check(survivors.SequenceEqual(["01.jpg", "03.jpg", "03.jpg", "04.jpg", "05.jpg"], StringComparer.OrdinalIgnoreCase),
            "Next/Move/Delete/Copy interleaving leaves deterministic filesystem state", failures);
    }

    private static string NewCase(string root, string name)
    {
        var dir = Path.Combine(root, "file-action-tests", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    }
}
