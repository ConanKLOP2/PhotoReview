using System.IO;
using System.Text;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.IO;

/// <summary>
/// Triển khai <see cref="IFileSystem"/> trên hệ thống tệp tin thực tế của hệ điều hành thông qua System.IO.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Directory.Exists(path);
    }

    public FileStat? GetFileStat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        return info.Exists ? new FileStat(info.Length, info.LastWriteTimeUtc) : null;
    }

    public void Move(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        File.Move(source, destination);
    }

    public void Copy(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        File.Copy(source, destination);
    }

    public void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.Delete(path);
    }

    public Stream OpenReadShared(string path, int bufferSize = 65536)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.SequentialScan);
    }

    public Stream OpenAppendDurable(string path) => OpenAppend(path, durable: true);

    public Stream OpenAppend(string path, bool durable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            durable ? FileOptions.WriteThrough : FileOptions.None);
    }

    public void WriteAllTextAtomic(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort temp cleanup; the original failure is reported */ }
            }
        }
    }

    public string ReadAllText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadAllText(path);
    }

    public IEnumerable<string> ReadLines(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadLines(path);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Directory.EnumerateFiles(directory, pattern ?? "*");
    }

    /// <summary>
    /// DirectoryInfo.EnumerateFiles() returns FileInfo already populated with
    /// Length/LastWriteTimeUtc from the same FindFirstFile/FindNextFile directory entry used to
    /// list the file, so a folder scan doesn't need one extra GetFileStat() syscall per file.
    /// </summary>
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        foreach (var info in new DirectoryInfo(directory).EnumerateFiles(pattern ?? "*"))
        {
            yield return (info.FullName, new FileStat(info.Length, info.LastWriteTimeUtc));
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Directory.EnumerateDirectories(directory);
    }

    public void CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
    }
}
