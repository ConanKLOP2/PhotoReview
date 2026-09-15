using System.Diagnostics;
using System.IO;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// Migrated from PhotoReview.Tests/FileActionConcurrencyTests.cs — deterministic filesystem
/// probes for actions racing an open/read stream.
/// </summary>
public sealed class FileActionConcurrencyTests : IDisposable
{
    private readonly TempRoot _root = new("file-action");

    public void Dispose() => _root.Dispose();

    private string NewCase(string name) => _root.Dir(Path.Combine("file-action-tests", name));

    [Fact(DisplayName = "Move succeeds immediately while decoder keeps a delete-sharing read handle")]
    public void MoveSucceedsWhileReadIsOpen()
    {
        var dir = NewCase("move-read");
        var source = Path.Combine(dir, "01.jpg");
        var target = Path.Combine(dir, "moved", "01.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var watch = Stopwatch.StartNew();
        File.Move(source, target);
        watch.Stop();
        Assert.True(!File.Exists(source) && File.Exists(target) && watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "Delete succeeds immediately while decoder keeps a delete-sharing read handle")]
    public void DeleteSucceedsWhileReadIsOpen()
    {
        var dir = NewCase("delete-read");
        var source = Path.Combine(dir, "01.jpg");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        File.Delete(source);
        Assert.False(File.Exists(source));
    }

    [Fact(DisplayName = "Copy preserves source and bytes while decoder is reading")]
    public void CopyPreservesSourceAndBytesWhileReadIsOpen()
    {
        var dir = NewCase("copy-read");
        var source = Path.Combine(dir, "01.jpg");
        var target = Path.Combine(dir, "copy", "01.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        File.Copy(source, target);
        Assert.True(File.Exists(source) && File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact(DisplayName = "Next/Move/Delete/Copy interleaving leaves deterministic filesystem state")]
    public void InterleavedSequenceLeavesDeterministicState()
    {
        var dir = NewCase("sequence-read");
        var files = Enumerable.Range(1, 5).Select(i => Path.Combine(dir, $"{i:00}.jpg")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [1, 2]);
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
        Assert.True(survivors.SequenceEqual(["01.jpg", "03.jpg", "03.jpg", "04.jpg", "05.jpg"],
            StringComparer.OrdinalIgnoreCase));
    }
}
