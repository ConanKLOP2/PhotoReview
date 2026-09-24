using System.Diagnostics;
using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Session;

/// <summary>IO04 (ADR 0007 section 2): session writes are atomic but not durable; settings stay durable.</summary>
public sealed class SessionWritePolicyTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewIo04-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private sealed class RecordingFileSystem(IFileSystem inner) : IFileSystem
    {
        public List<(string Path, bool Durable)> AtomicWrites { get; } = [];

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path) => inner.GetFileStat(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public void WriteAllTextAtomic(string path, string text, bool durable = true)
        {
            AtomicWrites.Add((path, durable));
            inner.WriteAllTextAtomic(path, text, durable);
        }
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    [Fact]
    public void SessionSave_UsesNonDurableAtomicWrite()
    {
        var fs = new RecordingFileSystem(new InMemoryFileSystem());
        var store = new SessionStore(new AppPaths(@"C:\Users\test\AppData\Local"), fs);

        store.Save(new SessionState { Folder = @"C:\photos" });

        var write = Assert.Single(fs.AtomicWrites);
        Assert.False(write.Durable);
    }

    [Fact]
    public void SettingsSave_UsesDurableAtomicWrite()
    {
        var fs = new RecordingFileSystem(new InMemoryFileSystem());
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var store = new SettingsStore(paths, fs, NullLog.Instance, (_, _) => { });

        store.Save(new AppSettings());

        var write = Assert.Single(fs.AtomicWrites);
        Assert.True(write.Durable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"Folder\": \"C:\\\\photos\", \"CurrentPa")]
    [InlineData("\0\0\0\0\0\0")]
    [InlineData("null")]
    public void SessionLoad_EmptyOrPartialFile_ReturnsNoSession(string content)
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new AppPaths(@"C:\Users\test\AppData\Local"), fs);
        fs.WriteAllTextAtomic(store.GetPath(@"C:\photos"), content);

        var loaded = store.Load(@"C:\photos");

        Assert.Equal(@"C:\photos", loaded.Folder);
        Assert.Null(loaded.CurrentPath);
        Assert.Empty(loaded.Skipped);
    }

    [Fact]
    public void PhysicalNonDurableWrite_IsAtomicRoundTripAndLeavesNoTempFiles()
    {
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(_root, "sub", "state.json");

        fs.WriteAllTextAtomic(path, "first", durable: false);
        fs.WriteAllTextAtomic(path, "second é", durable: false);

        Assert.Equal("second é", fs.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void PhysicalNonDurableWrite_FailedRename_CleansUpTempFile()
    {
        var fs = new PhysicalFileSystem();
        var dir = Path.Combine(_root, "d");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target); // a directory in the way: File.Move(overwrite) must fail

        Assert.ThrowsAny<Exception>(() => fs.WriteAllTextAtomic(target, "x", durable: false));

        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void Measure_SessionSaveLatency_DurableVersusNonDurable()
    {
        var fs = new PhysicalFileSystem();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "latency.json");
        var text = new string('x', 4096);
        foreach (var durable in new[] { true, false, true, false })
        {
            var samples = new double[100];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = Stopwatch.GetTimestamp();
                fs.WriteAllTextAtomic(path, text, durable);
                samples[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            }

            Array.Sort(samples);
            output.WriteLine($"durable={durable}: P50={samples[50]:F2} ms P95={samples[95]:F2} ms max={samples[^1]:F2} ms");
        }
    }
}
