using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>
/// Decorator over <see cref="InMemoryFileSystem"/> for tests that must make the file system lie or throw in places the plain
/// fake cannot: <see cref="FileExistsHook"/> and <see cref="StatThrowHook"/> may throw, <see cref="StatFilter"/> rewrites (or
/// hides) what a stat reports, and <see cref="AfterMove"/>/<see cref="AfterCopy"/> run right after the inner operation succeeded
/// (a "verification fails after the mutation" scenario). Everything else is forwarded unchanged. Never touches the real disk.
/// </summary>
public sealed class FaultableFileSystem(InMemoryFileSystem inner) : IFileSystem
{
    public InMemoryFileSystem Inner { get; } = inner;

    /// <summary>Called first by <see cref="FileExists"/>; may throw.</summary>
    public Action<string>? FileExistsHook { get; set; }

    /// <summary>Called first by <see cref="GetFileStat"/>; may throw.</summary>
    public Action<string>? StatThrowHook { get; set; }

    /// <summary>Rewrites the stat the inner file system reported (path, real stat) -> reported stat; null hides the file.</summary>
    public Func<string, FileStat?, FileStat?>? StatFilter { get; set; }

    public Action<string, string>? AfterMove { get; set; }
    public Action<string, string>? AfterCopy { get; set; }

    public bool FileExists(string path)
    {
        FileExistsHook?.Invoke(path);
        return Inner.FileExists(path);
    }

    public bool DirectoryExists(string path) => Inner.DirectoryExists(path);

    public FileStat? GetFileStat(string path)
    {
        StatThrowHook?.Invoke(path);
        var stat = Inner.GetFileStat(path);
        return StatFilter is null ? stat : StatFilter(path, stat);
    }

    public void Move(string source, string destination)
    {
        Inner.Move(source, destination);
        AfterMove?.Invoke(source, destination);
    }

    public void Copy(string source, string destination)
    {
        Inner.Copy(source, destination);
        AfterCopy?.Invoke(source, destination);
    }

    public bool TryCopyNew(string source, string destination) => TryCopyNew(source, destination, new CopyCreationProof());

    public bool TryCopyNew(string source, string destination, CopyCreationProof proof)
    {
        var created = Inner.TryCopyNew(source, destination, proof);
        if (created) AfterCopy?.Invoke(source, destination);
        return created;
    }

    public void Delete(string path) => Inner.Delete(path);
    public Stream OpenReadShared(string path, int bufferSize = 65536) => Inner.OpenReadShared(path, bufferSize);
    public Stream OpenAppend(string path, bool durable) => Inner.OpenAppend(path, durable);
    public void WriteAllTextAtomic(string path, string text, bool durable = true) => Inner.WriteAllTextAtomic(path, text, durable);
    public string ReadAllText(string path) => Inner.ReadAllText(path);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => Inner.EnumerateFiles(directory, pattern);
    public IEnumerable<string> EnumerateDirectories(string directory) => Inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => Inner.CreateDirectory(path);
    public bool TryDeleteEmptyDirectory(string path) => Inner.TryDeleteEmptyDirectory(path);
    public string ResolveRealPath(string path) => Inner.ResolveRealPath(path);
}