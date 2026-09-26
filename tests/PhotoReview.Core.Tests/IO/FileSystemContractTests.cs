using System.Text;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.IO;

[Trait("Category", "HotPath")]
public class FileSystemContractTests
{
    public interface IFileSystemHarness : IDisposable
    {
        IFileSystem FileSystem { get; }
        string RootDirectory { get; }
        string Combine(params string[] paths);
    }

    private sealed class PhysicalFileSystemHarness : IFileSystemHarness
    {
        public IFileSystem FileSystem { get; } = new PhysicalFileSystem();
        public string RootDirectory { get; }

        public PhysicalFileSystemHarness()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "PhotoReview-FSTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDirectory);
        }

        public string Combine(params string[] paths) =>
            Path.Combine([RootDirectory, .. paths]);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private sealed class InMemoryFileSystemHarness : IFileSystemHarness
    {
        public IFileSystem FileSystem { get; }
        public string RootDirectory { get; } = Path.Combine("C:", "VirtualRoot");

        public InMemoryFileSystemHarness()
        {
            var memoryFs = new InMemoryFileSystem();
            memoryFs.CreateDirectory(RootDirectory);
            FileSystem = memoryFs;
        }

        public string Combine(params string[] paths) =>
            Path.Combine([RootDirectory, .. paths]);

        public void Dispose()
        {
        }
    }

    public static IEnumerable<object[]> GetHarnessFactories()
    {
        yield return [new Func<IFileSystemHarness>(() => new PhysicalFileSystemHarness())];
        yield return [new Func<IFileSystemHarness>(() => new InMemoryFileSystemHarness())];
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void FileExistsReturnsTrueWhenPresentAndFalseWhenMissing(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("test.txt");

        Assert.False(fs.FileExists(filePath));

        fs.WriteAllTextAtomic(filePath, "sample content");

        Assert.True(fs.FileExists(filePath));
        // Case-insensitivity check (Windows style)
        Assert.True(fs.FileExists(filePath.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void DirectoryExistsReturnsTrueWhenPresentAndFalseWhenMissing(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var subDir = harness.Combine("SubFolder");

        Assert.False(fs.DirectoryExists(subDir));

        fs.CreateDirectory(subDir);

        Assert.True(fs.DirectoryExists(subDir));
        Assert.True(fs.DirectoryExists(subDir.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void GetFileStatReturnsMetadataForExistingFileAndNullForMissing(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("stat-test.txt");

        Assert.Null(fs.GetFileStat(filePath));

        const string content = "Hello UTF-8 World Tiếng Việt";
        fs.WriteAllTextAtomic(filePath, content);

        var stat = fs.GetFileStat(filePath);
        Assert.NotNull(stat);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), stat.Length);
        Assert.True(stat.LastWriteUtc > DateTime.UtcNow.AddMinutes(-5));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void MoveRelocatesFileAndRemovesSource(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var source = harness.Combine("source.txt");
        var destination = harness.Combine("destination.txt");

        fs.WriteAllTextAtomic(source, "data to move");
        fs.Move(source, destination);

        Assert.False(fs.FileExists(source));
        Assert.True(fs.FileExists(destination));
        Assert.Equal("data to move", fs.ReadAllText(destination));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void MoveThrowsWhenSourceDoesNotExist(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var source = harness.Combine("non-existent.txt");
        var destination = harness.Combine("dst.txt");

        Assert.ThrowsAny<FileNotFoundException>(() => fs.Move(source, destination));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void CopyDuplicatesFilePreservingSource(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var source = harness.Combine("source.txt");
        var destination = harness.Combine("copied.txt");

        fs.WriteAllTextAtomic(source, "shared data");
        fs.Copy(source, destination);

        Assert.True(fs.FileExists(source));
        Assert.True(fs.FileExists(destination));
        Assert.Equal("shared data", fs.ReadAllText(destination));
        Assert.Equal("shared data", fs.ReadAllText(source));
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void DeleteRemovesFileAndIsIdempotentOnMissingFile(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("delete-me.txt");

        fs.WriteAllTextAtomic(filePath, "to be deleted");
        Assert.True(fs.FileExists(filePath));

        fs.Delete(filePath);
        Assert.False(fs.FileExists(filePath));

        // Deleting non-existent file should not throw
        fs.Delete(filePath);
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void OpenReadSharedAllowsReadingEntireContent(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("shared-read.bin");

        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC };
        using (var append = fs.OpenAppendDurable(filePath))
        {
            append.Write(payload, 0, payload.Length);
        }

        using var readStream = fs.OpenReadShared(filePath);
        using var ms = new MemoryStream();
        readStream.CopyTo(ms);

        Assert.Equal(payload, ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void OpenAppendDurableAppendsSequentially(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("journal.jsonl");

        using (var stream1 = fs.OpenAppendDurable(filePath))
        {
            var line1 = Encoding.UTF8.GetBytes("Line 1\n");
            stream1.Write(line1, 0, line1.Length);
        }

        using (var stream2 = fs.OpenAppendDurable(filePath))
        {
            var line2 = Encoding.UTF8.GetBytes("Line 2\n");
            stream2.Write(line2, 0, line2.Length);
        }

        var lines = fs.ReadLines(filePath).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void WriteAllTextAtomicAndReadAllTextRoundTrip(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var filePath = harness.Combine("nested", "dir", "atomic.txt");

        const string unicodeText = "Dữ liệu thử nghiệm có dấu và ký tự đặc biệt !@#$%^&*()";
        fs.WriteAllTextAtomic(filePath, unicodeText);

        var readBack = fs.ReadAllText(filePath);
        Assert.Equal(unicodeText, readBack);
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void EnumerateFilesMatchesPatternAndRespectsTopDirectoryOnly(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var dir = harness.Combine("Gallery");
        fs.CreateDirectory(dir);

        var file1 = Path.Combine(dir, "photo1.jpg");
        var file2 = Path.Combine(dir, "photo2.png");
        var file3 = Path.Combine(dir, "document.pdf");
        var subDir = Path.Combine(dir, "Nested");
        fs.CreateDirectory(subDir);
        var fileInSubDir = Path.Combine(subDir, "photo3.jpg");

        fs.WriteAllTextAtomic(file1, "1");
        fs.WriteAllTextAtomic(file2, "2");
        fs.WriteAllTextAtomic(file3, "3");
        fs.WriteAllTextAtomic(fileInSubDir, "sub");

        // Enumerate all files in top directory
        var allFiles = fs.EnumerateFiles(dir, "*").Select(Path.GetFileName).ToList();
        Assert.Equal(3, allFiles.Count);
        Assert.Contains("photo1.jpg", allFiles);
        Assert.Contains("photo2.png", allFiles);
        Assert.Contains("document.pdf", allFiles);

        // Pattern filtering
        var jpgFiles = fs.EnumerateFiles(dir, "*.jpg").Select(Path.GetFileName).ToList();
        Assert.Single(jpgFiles);
        Assert.Contains("photo1.jpg", jpgFiles);
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void EnumerateDirectoriesListsImmediateSubdirectories(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var parent = harness.Combine("Parent");
        var sub1 = Path.Combine(parent, "Child1");
        var sub2 = Path.Combine(parent, "Child2");

        fs.CreateDirectory(sub1);
        fs.CreateDirectory(sub2);

        var dirs = fs.EnumerateDirectories(parent).Select(Path.GetFileName).ToList();
        Assert.Equal(2, dirs.Count);
        Assert.Contains("Child1", dirs);
        Assert.Contains("Child2", dirs);
    }

    [Theory]
    [MemberData(nameof(GetHarnessFactories))]
    public void TryProbeReadable_ExistingFileIsReadable_MissingFileReportsAReason(Func<IFileSystemHarness> factory)
    {
        using var harness = factory();
        var fs = harness.FileSystem;
        var present = harness.Combine("present.jpg");
        fs.WriteAllTextAtomic(present, "img");

        Assert.True(fs.TryProbeReadable(present, out var none));
        Assert.Null(none);
        Assert.False(fs.TryProbeReadable(harness.Combine("missing.jpg"), out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
