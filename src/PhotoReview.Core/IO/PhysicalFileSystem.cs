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

    // File.Copy without overwrite is CopyFile with COPY_FILE_FAIL_IF_EXISTS: the existence check and the creation are
    // one atomic step, and an already-existing destination surfaces as ERROR_FILE_EXISTS (80) / ERROR_ALREADY_EXISTS (183).
    // Ownership proof (R01): File.Copy cannot tell us whether a throw happened before or after it created the destination (a missing
    // source with a destination that someone else just created throws FileNotFoundException and creates nothing), so the proof is
    // raised only when File.Copy returned: a failed physical copy is never claimed as ours and its partial file, if any, is left for
    // Recovery rather than risking deleting a foreign file.
    public bool TryCopyNew(string source, string destination) => TryCopyNew(source, destination, new CopyCreationProof());

    public bool TryCopyNew(string source, string destination, CopyCreationProof proof)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(proof);
        try
        {
            File.Copy(source, destination);
            proof.MarkCreated();
            return true;
        }
        // A directory at the destination is not "a file already exists": some Windows builds also report it as 80 (others as
        // access denied), so it is rethrown explicitly to keep the result independent of the OS build.
        catch (Exception ex) when (IsSwallowedAsDestinationExists(ex) && !DirectoryExists(destination))
        {
            return false;
        }
    }

    /// <summary>Which failures of the no-overwrite copy mean "the destination already exists" (TryCopyNew returns false): only the
    /// Win32 file-exists/already-exists IOExceptions. Everything else (access denied, missing folder, sharing violation, disk full,
    /// an IOException without such an HResult) must be rethrown: a false means nothing of the caller's is at the destination.</summary>
    internal static bool IsSwallowedAsDestinationExists(Exception ex) => FileSystemErrors.IsDestinationExists(ex);

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

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Directory.EnumerateFiles(directory, pattern ?? "*");
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
        return EnumerateFilesWithStat(directory, include, onSkipped, static dir => new DirectoryInfo(dir).EnumerateFiles("*").GetEnumerator());
    }

    /// <summary>Test seam: the directory enumerator is injected so a test can make <c>MoveNext</c> throw part-way through a listing.</summary>
    internal static IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped, Func<string, IEnumerator<FileInfo>> openListing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(onSkipped);

        using var enumerator = openListing(directory);
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

    public bool TryDeleteEmptyDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            // Directory.Delete without recursion refuses a non-empty directory atomically (IOException), so there is no
            // check-then-delete race that could remove content.
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// SEC-01: walks <paramref name="path"/> one segment at a time from its root, following the real reparse
    /// point (symlink/junction) target whenever an existing segment is one, including the final segment (the
    /// destination file/folder itself). A segment that does not exist yet is appended as-is (nothing to
    /// resolve). This is what makes containment checks in <c>ActionDestinationPolicy</c>/<c>FileActionService</c>
    /// resistant to a junction planted inside the photo folder that points elsewhere on disk.
    /// </summary>
    public string ResolveRealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (string.IsNullOrEmpty(root)) return full;

        var relative = full[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // A bare root ("C:\") has nothing to resolve; trimming it would hand back the drive-relative "C:" instead.
        if (segments.Length == 0) return root;

        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            var candidate = current.Length == 0 ? segment : current + Path.DirectorySeparatorChar + segment;
            current = ResolveIfReparsePoint(candidate);
        }

        return current;
    }

    /// <summary>Returns the fully-resolved final target when <paramref name="candidate"/> exists and is a reparse
    /// point (symlink or junction); otherwise returns <paramref name="candidate"/> unchanged.</summary>
    private static string ResolveIfReparsePoint(string candidate)
    {
        FileSystemInfo info;
        if (Directory.Exists(candidate)) info = new DirectoryInfo(candidate);
        else if (File.Exists(candidate)) info = new FileInfo(candidate);
        else return candidate; // doesn't exist yet: nothing to resolve

        // LinkTarget is non-null only for a reparse point .NET understands (symlink or, on Windows, a junction).
        if (info.LinkTarget is null) return candidate;

        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return resolved?.FullName ?? candidate;
    }
}
