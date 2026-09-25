using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.IO;

/// <summary>
/// Name-collision and failure contract that every <see cref="IFileSystem"/> must honor, because the file-action safety
/// story ("never overwrite, never lose the source") rests on it: the fake and <see cref="PhysicalFileSystem"/> agree.
/// </summary>
public sealed class FileSystemCollisionContractTests : IDisposable
{
    private readonly TempRoot _root = new("fs-collision");

    public void Dispose() => _root.Dispose();

    public static TheoryData<string> Kinds() => ["physical", "memory"];

    private (IFileSystem Fs, string Dir) Make(string kind)
    {
        if (kind == "physical") return (new PhysicalFileSystem(), _root.Dir("phys"));
        var memory = new InMemoryFileSystem();
        memory.CreateDirectory(@"C:\VirtualRoot");
        return (memory, @"C:\VirtualRoot");
    }

    [Theory(DisplayName = "Move onto an existing file throws and leaves both files untouched")]
    [MemberData(nameof(Kinds))]
    public void Move_DestinationExists_ThrowsAndKeepsBoth(string kind)
    {
        var (fs, dir) = Make(kind);
        var source = Path.Combine(dir, "a.txt");
        var destination = Path.Combine(dir, "b.txt");
        fs.WriteAllTextAtomic(source, "source-bytes");
        fs.WriteAllTextAtomic(destination, "other-bytes");

        Assert.ThrowsAny<IOException>(() => fs.Move(source, destination));

        Assert.Equal("source-bytes", fs.ReadAllText(source));
        Assert.Equal("other-bytes", fs.ReadAllText(destination));
    }

    [Theory(DisplayName = "Move onto a name differing only by case is a collision (Windows names are case-insensitive)")]
    [MemberData(nameof(Kinds))]
    public void Move_DestinationDiffersByCaseOnly_IsACollision(string kind)
    {
        var (fs, dir) = Make(kind);
        var source = Path.Combine(dir, "a.txt");
        fs.WriteAllTextAtomic(source, "source-bytes");
        fs.WriteAllTextAtomic(Path.Combine(dir, "Photo.JPG"), "existing");

        Assert.True(fs.FileExists(Path.Combine(dir, "photo.jpg")));
        Assert.ThrowsAny<IOException>(() => fs.Move(source, Path.Combine(dir, "photo.jpg")));

        Assert.Equal("existing", fs.ReadAllText(Path.Combine(dir, "Photo.JPG")));
        Assert.True(fs.FileExists(source));
    }

    [Theory(DisplayName = "Copy onto an existing file throws and keeps the existing destination bytes")]
    [MemberData(nameof(Kinds))]
    public void Copy_DestinationExists_ThrowsAndKeepsBoth(string kind)
    {
        var (fs, dir) = Make(kind);
        var source = Path.Combine(dir, "a.txt");
        var destination = Path.Combine(dir, "b.txt");
        fs.WriteAllTextAtomic(source, "source-bytes");
        fs.WriteAllTextAtomic(destination, "other-bytes");

        Assert.ThrowsAny<IOException>(() => fs.Copy(source, destination));

        Assert.Equal("other-bytes", fs.ReadAllText(destination));
        Assert.Equal("source-bytes", fs.ReadAllText(source));
    }

    [Theory(DisplayName = "Move into a missing folder throws DirectoryNotFound and keeps the source")]
    [MemberData(nameof(Kinds))]
    public void Move_MissingDestinationFolder_KeepsSource(string kind)
    {
        var (fs, dir) = Make(kind);
        var source = Path.Combine(dir, "a.txt");
        fs.WriteAllTextAtomic(source, "source-bytes");

        Assert.Throws<DirectoryNotFoundException>(() => fs.Move(source, Path.Combine(dir, "nope", "a.txt")));

        Assert.Equal("source-bytes", fs.ReadAllText(source));
    }

    [Theory(DisplayName = "WriteAllTextAtomic replaces an existing file completely")]
    [MemberData(nameof(Kinds))]
    public void WriteAllTextAtomic_Overwrite_ReplacesWholeContent(string kind)
    {
        var (fs, dir) = Make(kind);
        var path = Path.Combine(dir, "s.json");
        fs.WriteAllTextAtomic(path, new string('x', 5000));

        fs.WriteAllTextAtomic(path, "short");

        Assert.Equal("short", fs.ReadAllText(path));
    }

    [Fact(DisplayName = "A path longer than 260 characters can be written, copied, moved, stat-ed and deleted")]
    public void LongPath_FullLifecycle()
    {
        var fs = new PhysicalFileSystem();
        var deep = _root.Combine(string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 40), 8)));
        fs.CreateDirectory(deep);
        var file = Path.Combine(deep, new string('f', 60) + ".jpg");
        Assert.True(file.Length > 300);

        fs.WriteAllTextAtomic(file, "long");
        fs.Copy(file, Path.Combine(deep, "copy.jpg"));
        fs.Move(file, Path.Combine(deep, "moved.jpg"));

        Assert.Equal(4, fs.GetFileStat(Path.Combine(deep, "moved.jpg"))!.Length);
        Assert.False(fs.FileExists(file));
        fs.Delete(Path.Combine(deep, "copy.jpg"));
        Assert.False(fs.FileExists(Path.Combine(deep, "copy.jpg")));
    }

    [Fact(DisplayName = "Copy onto a destination another process holds open fails and the source stays intact")]
    public void Copy_DestinationLocked_FailsAndKeepsSource()
    {
        var fs = new PhysicalFileSystem();
        var source = _root.File("a.txt", 1, 2, 3);
        var destination = Path.Combine(_root.Path, "b.txt");
        File.WriteAllBytes(destination, [9]);
        using var lockHandle = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => fs.Copy(source, destination));

        Assert.Equal(3, fs.GetFileStat(source)!.Length);
    }

    [Fact(DisplayName = "Move of a read-only file inside one volume succeeds and keeps the read-only flag")]
    public void Move_ReadOnlySource_SameVolume_Works()
    {
        var fs = new PhysicalFileSystem();
        var source = _root.File("ro.txt", 1);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var destination = Path.Combine(_root.Path, "moved.txt");

        try
        {
            fs.Move(source, destination);
            Assert.False(fs.FileExists(source));
            Assert.True(File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact(DisplayName = "Concurrent atomic writers to one path always leave one complete text and no temp files")]
    public void WriteAllTextAtomic_ConcurrentWriters_LeaveACompleteFile()
    {
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(_root.Path, "settings.json");
        var texts = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 20000)).ToArray();
        var start = new Barrier(texts.Length);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = texts.Select(text => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 25; i++)
            {
                try { fs.WriteAllTextAtomic(path, text, durable: false); }
                catch (Exception ex) { failures.Add(ex); }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(failures); // the rename over a target another writer is replacing is retried, not surfaced as "access denied"
        Assert.Contains(fs.ReadAllText(path), texts);
        Assert.Empty(Directory.GetFiles(_root.Path, "*.tmp"));
    }
}
