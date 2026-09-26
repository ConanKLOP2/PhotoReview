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

            ReplaceWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort temp cleanup; the original failure is reported */ }
            }
        }
    }

    /// <summary>
    /// The final rename over an existing file fails sporadically with "access denied"/"sharing violation" while another
    /// writer (a second PhotoReview process saving the same settings/session file) or a reader/AV scanner briefly holds the
    /// target. The temp file is complete and durable at this point, so retry a few times with a short backoff (worst case
    /// ~100 ms in total) before giving up with the original exception.
    /// </summary>
    private static void ReplaceWithRetry(string tempPath, string path) =>
        ReplaceWithRetry(() => File.Move(tempPath, path, overwrite: true), static delay => Thread.Sleep(delay));

    // Only transient contention is retried (sharing/lock violation, access denied); a missing directory, too-long path
    // etc. cannot heal in ~100 ms, so those surface at once instead of stalling the caller (settings save runs on the UI thread).
    internal static void ReplaceWithRetry(Action move, Action<int> sleep)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && IsTransientReplaceFailure(ex))
            {
                sleep(attempt * 3);
            }
        }
    }

    private static bool IsTransientReplaceFailure(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) };

    private const int ReplaceAttempts = 8;

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
    /// ADR 0007 section 3: no silent <c>IgnoreInaccessible</c>. Each included file is probed with
    /// <see cref="TryProbeReadable"/>; one that cannot be opened, or an error in the middle of the
    /// directory listing, is reported through <paramref name="onSkipped"/> and skipped.
    /// </summary>
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
    {
        ArgumentNullException.ThrowIfNull(onSkipped);
        foreach (var file in EnumerateFilesWithStat(directory, include, onSkipped))
        {
            if (TryProbeReadable(file.Path, out var failure))
            {
                yield return file;
            }
            else
            {
                onSkipped(new SkippedEntry(file.Path, failure ?? string.Empty));
            }
        }
    }

    /// <summary>
    /// AR16: one read-open (no bytes read, shares ReadWrite|Delete so it never blocks another app);
    /// a locked, access-denied or otherwise unopenable file returns false with the OS message.
    /// </summary>
    public bool TryProbeReadable(string path, out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var probe = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// AR16 fast listing: the included files with the stat of their directory entry, no per-file
    /// open. An error in the middle of the listing is reported through <paramref name="onSkipped"/>
    /// (<see cref="SkippedKind.ListingInterrupted"/>) and keeps what was read; an error before the
    /// first entry (the folder itself is unreadable) propagates.
    /// </summary>
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(
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
                onSkipped(new SkippedEntry(directory, ex.Message, SkippedKind.ListingInterrupted));
                yield break;
            }

            if (!include(info.FullName)) continue;

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
