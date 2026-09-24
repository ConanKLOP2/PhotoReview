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

    public void WriteAllTextAtomic(string path, string text, bool durable = true)
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
                durable ? FileOptions.WriteThrough : FileOptions.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: durable);
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

    /// <summary>
    /// ADR 0007 section 3: no silent <c>IgnoreInaccessible</c>. Each included file is probed with a
    /// read-open (no bytes read); one that cannot be opened, or an error in the middle of the
    /// directory listing, is reported through <paramref name="onSkipped"/> and skipped.
    /// </summary>
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(onSkipped);

        using var enumerator = new DirectoryInfo(directory).EnumerateFiles("*").GetEnumerator();
        var listed = 0;
        while (true)
        {
            FileInfo info;
            try
            {
                if (!enumerator.MoveNext()) yield break;
                info = enumerator.Current;
                listed++;
            }
            catch (Exception ex) when (listed > 0 && ex is IOException or UnauthorizedAccessException)
            {
                // (A failure before the first entry means the folder itself is unreadable: that
                // propagates, so the load fails loudly instead of showing an empty catalog.)
                // The listing itself broke part-way: keep what was read, report the rest as skipped.
                onSkipped(new SkippedEntry(directory, ex.Message));
                yield break;
            }

            if (!include(info.FullName)) continue;

            string? failure = null;
            try
            {
                using var probe = new FileStream(
                    info.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex.Message;
            }

            if (failure is not null)
            {
                onSkipped(new SkippedEntry(info.FullName, failure));
                continue;
            }

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
