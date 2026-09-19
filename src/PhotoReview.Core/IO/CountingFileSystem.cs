using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.IO;

/// <summary>
/// Decorates an <see cref="IFileSystem"/> and counts metadata queries (<see cref="IFileSystem.FileExists"/>,
/// <see cref="IFileSystem.DirectoryExists"/>, <see cref="IFileSystem.GetFileStat"/>) into <see cref="ReviewMetrics.StatCount"/>.
/// Everything else is forwarded untouched. Wrap only when the counters are wanted; the cost is one
/// interlocked increment per query.
/// </summary>
public sealed class CountingFileSystem(IFileSystem inner, ReviewMetrics metrics) : IFileSystem
{
    private readonly IFileSystem _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ReviewMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    public bool FileExists(string path) { _metrics.RecordStat(); return _inner.FileExists(path); }
    public bool DirectoryExists(string path) { _metrics.RecordStat(); return _inner.DirectoryExists(path); }
    public FileStat? GetFileStat(string path) { _metrics.RecordStat(); return _inner.GetFileStat(path); }

    public void Move(string source, string destination) => _inner.Move(source, destination);
    public void Copy(string source, string destination) => _inner.Copy(source, destination);
    public void Delete(string path) => _inner.Delete(path);
    public Stream OpenReadShared(string path, int bufferSize = 65536) => _inner.OpenReadShared(path, bufferSize);
    public Stream OpenAppendDurable(string path) => _inner.OpenAppendDurable(path);
    public void WriteAllTextAtomic(string path, string text) => _inner.WriteAllTextAtomic(path, text);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public IEnumerable<string> ReadLines(string path) => _inner.ReadLines(path);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => _inner.EnumerateFiles(directory, pattern);
    public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
}
