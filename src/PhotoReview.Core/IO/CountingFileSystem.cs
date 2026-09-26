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
    public Stream OpenAppend(string path, bool durable) => _inner.OpenAppend(path, durable);
    public void WriteAllTextAtomic(string path, string text, bool durable = true) => _inner.WriteAllTextAtomic(path, text, durable);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public IEnumerable<string> ReadLines(string path) => _inner.ReadLines(path);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => _inner.EnumerateFiles(directory, pattern);
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, string pattern = "*") => _inner.EnumerateFilesWithStat(directory, pattern);
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
        _inner.EnumerateFilesWithStat(directory, include, onSkipped);
    public bool TryProbeReadable(string path, out string? failure) => _inner.TryProbeReadable(path, out failure);
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
        _inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
    public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
}
