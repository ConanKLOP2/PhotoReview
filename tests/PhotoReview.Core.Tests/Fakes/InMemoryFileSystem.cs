using System.IO.Enumeration;
using System.Text;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>
/// Triển khai in-memory của <see cref="IFileSystem"/> dùng cho unit test.
/// - Phân biệt hoa thường giống Windows (OrdinalIgnoreCase).
/// - Hỗ trợ các hook chèn lỗi (fault-injection hooks).
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _fileWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    // SEC-01: normalized path -> real target it "points to", simulating a symlink/junction without touching the
    // real OS filesystem. Populated only by AddReparsePoint; ResolveRealPath walks segment by segment and
    // substitutes the target whenever a segment is registered here, exactly like PhysicalFileSystem does for a
    // real reparse point.
    private readonly Dictionary<string, string> _reparsePoints = new(StringComparer.OrdinalIgnoreCase);

    // --- Hook chèn lỗi cho unit testing ---
    public Func<string, Exception?>? OpenReadHook { get; set; }
    public Func<string, Exception?>? OpenAppendHook { get; set; }
    public Func<string, Exception?>? WriteHook { get; set; }
    /// <summary>Runs (outside the internal lock, so it may block) at the start of every <see cref="GetFileStat"/>; a returned exception is thrown.</summary>
    public Func<string, Exception?>? StatHook { get; set; }
    public Func<string, Exception?>? DeleteHook { get; set; }
    /// <summary>Runs at the start of every <see cref="EnumerateFiles"/>; a returned exception is thrown (e.g. access denied on a directory listing).</summary>
    public Func<string, Exception?>? EnumerateFilesHook { get; set; }
    public Func<string, string, Exception?>? MoveHook { get; set; }
    public Func<string, string, Exception?>? CopyHook { get; set; }

    /// <summary>
    /// R01: a copy that really creates its destination and then fails: when it returns a value for (source, destination), the first
    /// <c>Bytes</c> bytes are written (the creation is proven to the caller) and <c>Error</c> is thrown. Unlike writing the destination
    /// inside <see cref="CopyHook"/> (indistinguishable from a foreign file that appeared), this is a destination THIS copy created.
    /// </summary>
    public Func<string, string, (int Bytes, Exception Error)?>? CopyPartialHook { get; set; }

    /// <summary>
    /// Simulates a cross-volume MoveFileEx(MOVEFILE_COPY_ALLOWED) that copied the file but could not delete the source
    /// (read-only / open without FILE_SHARE_DELETE): Move then returns normally and leaves the source in place.
    /// </summary>
    public bool MoveLeavesSource { get; set; }

    /// <summary>
    /// RV-C01: maps (destination path, source write time) to the write time the destination volume stores after a Move,
    /// e.g. a FAT volume that rounds it to 2 s. Null = the stamp is kept exactly (NTFS).
    /// </summary>
    public Func<string, DateTime, DateTime>? StampOnMove { get; set; }

    /// <summary>
    /// D-01 (decision c): like the real <c>PhysicalFileSystem.TryCopyNew</c>, a copy that throws after writing a partial destination
    /// does NOT raise <see cref="CopyCreationProof"/>; only a copy that returned does. The partial file stays for Recovery.
    /// RV-C03: when set, <see cref="Copy"/>/<see cref="TryCopyNew"/> write only the first N bytes of the source to the
    /// (new) destination and then throw an <see cref="IOException"/>, like a copy cut short by a full disk.
    /// </summary>
    public int? CopyFailsAfterBytes { get; set; }

    /// <summary>Ordered trace of journal stream opens and file mutations (IO03 ordering/thread tests).</summary>
    public sealed record FsEvent(string Kind, bool Durable, int ThreadId, string Path);
    private readonly List<FsEvent> _events = [];
    public IReadOnlyList<FsEvent> Events { get { lock (_events) return _events.ToList(); } }
    private void Record(string kind, string path, bool durable = false)
    {
        lock (_events) _events.Add(new FsEvent(kind, durable, Environment.CurrentManagedThreadId, path));
    }

    public InMemoryFileSystem()
    {
    }

    public void AddFile(string path, string text, DateTime? lastWriteUtc = null)
    {
        AddFile(path, Encoding.UTF8.GetBytes(text), lastWriteUtc);
    }

    public void AddFile(string path, byte[] bytes, DateTime? lastWriteUtc = null)
    {
        lock (_lock)
        {
            var normalized = NormalizePath(path);
            var dir = NormalizeDirectoryPath(Path.GetDirectoryName(path) ?? string.Empty);
            if (!string.IsNullOrEmpty(dir)) InternalCreateDirectory(dir);
            _files[normalized] = bytes;
            _fileWriteTimes[normalized] = lastWriteUtc ?? DateTime.UtcNow;
        }
    }

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            var normalized = NormalizePath(path);
            return _files.ContainsKey(normalized);
        }
    }

    public bool DirectoryExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            var normalized = NormalizeDirectoryPath(path);
            return _directories.Contains(normalized) || IsDriveRoot(normalized);
        }
    }

    public FileStat? GetFileStat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (StatHook?.Invoke(path) is { } statEx)
        {
            throw statEx;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            if (_files.TryGetValue(normalized, out var bytes) &&
                _fileWriteTimes.TryGetValue(normalized, out var lastWrite))
            {
                return new FileStat(bytes.LongLength, lastWrite);
            }

            return null;
        }
    }

    public void Move(string source, string destination)
    {
        Record("move", source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (MoveHook?.Invoke(source, destination) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var srcNorm = NormalizePath(source);
            var dstNorm = NormalizePath(destination);

            if (!_files.TryGetValue(srcNorm, out var bytes))
            {
                throw new FileNotFoundException("Không tìm thấy tệp tin nguồn.", source);
            }

            if (_files.ContainsKey(dstNorm))
            {
                throw DestinationExistsException(destination);
            }

            var destDir = NormalizeDirectoryPath(Path.GetDirectoryName(destination) ?? string.Empty);
            if (!string.IsNullOrEmpty(destDir) && !_directories.Contains(destDir) && !IsDriveRoot(destDir))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục đích: '{destDir}'.");
            }

            var writeTime = _fileWriteTimes[srcNorm];

            if (!MoveLeavesSource)
            {
                _files.Remove(srcNorm);
                _fileWriteTimes.Remove(srcNorm);
            }

            _files[dstNorm] = bytes;
            _fileWriteTimes[dstNorm] = StampOnMove?.Invoke(destination, writeTime) ?? writeTime;
        }
    }

    /// <summary>The IOException a real no-overwrite Move/Copy raises when the destination exists (Win32 ERROR_ALREADY_EXISTS, 183).</summary>
    public static IOException DestinationExistsException(string destination) =>
        new($"Tệp tin đích đã tồn tại: '{destination}'.", unchecked((int)0x800700B7));

    public void Copy(string source, string destination)
    {
        CopyCore(source, destination, failIfExists: false, proof: null);
    }

    /// <summary>Atomic create-new copy (existence check and creation under one lock), like the physical implementation.</summary>
    public bool TryCopyNew(string source, string destination) => CopyCore(source, destination, failIfExists: true, proof: null);

    /// <summary>Raises <paramref name="proof"/> only after the copy completed (never for a partial write that then throws), like the real file system.</summary>
    public bool TryCopyNew(string source, string destination, CopyCreationProof proof) =>
        CopyCore(source, destination, failIfExists: true, proof ?? throw new ArgumentNullException(nameof(proof)));

    private bool CopyCore(string source, string destination, bool failIfExists, CopyCreationProof? proof = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (CopyHook?.Invoke(source, destination) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var srcNorm = NormalizePath(source);
            var dstNorm = NormalizePath(destination);

            if (!_files.TryGetValue(srcNorm, out var bytes))
            {
                throw new FileNotFoundException("Không tìm thấy tệp tin nguồn.", source);
            }

            if (_files.ContainsKey(dstNorm))
            {
                if (failIfExists) return false;
                throw new IOException($"Tệp tin đích đã tồn tại: '{destination}'.");
            }

            var destDir = NormalizeDirectoryPath(Path.GetDirectoryName(destination) ?? string.Empty);
            if (!string.IsNullOrEmpty(destDir) && !_directories.Contains(destDir) && !IsDriveRoot(destDir))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục đích: '{destDir}'.");
            }

            if (CopyPartialHook?.Invoke(source, destination) is { } partial)
            {
                _files[dstNorm] = bytes.AsSpan(0, Math.Min(partial.Bytes, bytes.Length)).ToArray();
                _fileWriteTimes[dstNorm] = DateTime.UtcNow;
                // No proof here, like PhysicalFileSystem: File.Copy cannot say whether a throw came after it created the destination, so a
                // copy that fails leaves its partial file unclaimed (D-01, decision (c)); cleanup must never delete it.
                throw partial.Error;
            }

            if (CopyFailsAfterBytes is { } written)
            {
                _files[dstNorm] = bytes.AsSpan(0, Math.Min(written, bytes.Length)).ToArray(); // unclaimed, see CopyPartialHook above
                _fileWriteTimes[dstNorm] = DateTime.UtcNow;
                throw new IOException("Simulated disk full during copy.");
            }

            _files[dstNorm] = (byte[])bytes.Clone();
            // Like File.Copy / CopyFile: the copy keeps the source's last-write time (the journal's identity checks rely on it).
            _fileWriteTimes[dstNorm] = _fileWriteTimes[srcNorm];
            proof?.MarkCreated();
            return true;
        }
    }

    public void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (DeleteHook?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            _files.Remove(normalized);
            _fileWriteTimes.Remove(normalized);
        }
    }

    public Stream OpenReadShared(string path, int bufferSize = 65536)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OpenReadHook?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            if (!_files.TryGetValue(normalized, out var bytes))
            {
                throw new FileNotFoundException("Không tìm thấy tệp tin.", path);
            }

            return new MemoryStream((byte[])bytes.Clone(), writable: false);
        }
    }

    /// <summary>AR16 probe: same outcome as <see cref="OpenReadShared"/> (hook or missing file = unreadable), no bytes copied.</summary>
    public bool TryProbeReadable(string path, out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OpenReadHook?.Invoke(path) is { } ex and (IOException or UnauthorizedAccessException))
        {
            failure = ex.Message;
            return false;
        }

        lock (_lock)
        {
            if (!_files.ContainsKey(NormalizePath(path)))
            {
                failure = "Không tìm thấy tệp tin.";
                return false;
            }
        }

        failure = null;
        return true;
    }

    public Stream OpenAppend(string path, bool durable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Record("append", path, durable);

        if (OpenAppendHook?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            var dir = NormalizeDirectoryPath(Path.GetDirectoryName(path) ?? string.Empty);
            if (!string.IsNullOrEmpty(dir))
            {
                InternalCreateDirectory(dir);
            }

            var initialBytes = _files.TryGetValue(normalized, out var existing) ? existing : [];
            return new TrackedAppendStream(initialBytes, committedBytes =>
            {
                lock (_lock)
                {
                    _files[normalized] = committedBytes;
                    _fileWriteTimes[normalized] = DateTime.UtcNow;
                }
            });
        }
    }

    public void WriteAllTextAtomic(string path, string text, bool durable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        if (WriteHook?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            var dir = NormalizeDirectoryPath(Path.GetDirectoryName(path) ?? string.Empty);
            if (!string.IsNullOrEmpty(dir))
            {
                InternalCreateDirectory(dir);
            }

            var bytes = new UTF8Encoding(false).GetBytes(text);
            _files[normalized] = bytes;
            _fileWriteTimes[normalized] = DateTime.UtcNow;
        }
    }

    public string ReadAllText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OpenReadHook?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        lock (_lock)
        {
            var normalized = NormalizePath(path);
            if (!_files.TryGetValue(normalized, out var bytes))
            {
                throw new FileNotFoundException("Không tìm thấy tệp tin.", path);
            }

            return new UTF8Encoding(false).GetString(bytes);
        }
    }

    public IEnumerable<string> ReadLines(string path)
    {
        var text = ReadAllText(path);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (EnumerateFilesHook?.Invoke(directory) is { } hookEx)
        {
            throw hookEx;
        }

        lock (_lock)
        {
            var normalizedDir = NormalizeDirectoryPath(directory);
            if (!_directories.Contains(normalizedDir) && !IsDriveRoot(normalizedDir))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục: '{directory}'.");
            }

            var result = new List<string>();
            var effectivePattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;

            foreach (var filePath in _files.Keys)
            {
                var parentDir = NormalizeDirectoryPath(Path.GetDirectoryName(filePath) ?? string.Empty);
                if (string.Equals(parentDir, normalizedDir, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(filePath);
                    if (FileSystemName.MatchesSimpleExpression(effectivePattern, fileName, ignoreCase: true))
                    {
                        result.Add(filePath);
                    }
                }
            }

            return result;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (_lock)
        {
            var normalizedDir = NormalizeDirectoryPath(directory);
            if (!_directories.Contains(normalizedDir) && !IsDriveRoot(normalizedDir))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục: '{directory}'.");
            }

            var result = new List<string>();
            foreach (var dir in _directories)
            {
                var parentDir = NormalizeDirectoryPath(Path.GetDirectoryName(dir) ?? string.Empty);
                if (string.Equals(parentDir, normalizedDir, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dir, normalizedDir, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(dir);
                }
            }

            return result;
        }
    }

    public void CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            InternalCreateDirectory(NormalizeDirectoryPath(path));
        }
    }

    /// <summary>
    /// SEC-01 test seam: registers <paramref name="path"/> as an existing symlink/junction whose real target is
    /// <paramref name="target"/> (both simulated — no real OS reparse point is created). <paramref name="path"/>
    /// is also created as an ordinary directory entry so <see cref="DirectoryExists"/>/enumeration see it exist,
    /// matching a real junction which is itself a directory entry.
    /// </summary>
    public void AddReparsePoint(string path, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        lock (_lock)
        {
            var normalized = NormalizeDirectoryPath(path);
            var parent = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrEmpty(parent)) InternalCreateDirectory(NormalizeDirectoryPath(parent));
            _directories.Add(normalized);
            _reparsePoints[normalized] = NormalizeDirectoryPath(target);
        }
    }

    /// <summary>RV-S06 test seam: an exception to throw from <see cref="ResolveRealPath"/> for a path (null = resolve normally),
    /// like <c>PhysicalFileSystem</c> does for a reparse-point loop it cannot resolve.</summary>
    public Func<string, Exception?>? ResolveRealPathHook { get; set; }

    /// <summary>SEC-01: walks <paramref name="path"/> segment by segment, substituting any segment registered via
    /// <see cref="AddReparsePoint"/> with its simulated target — mirrors what <c>PhysicalFileSystem.ResolveRealPath</c>
    /// does for a real symlink/junction, so tests can exercise the escape check without touching the real OS.</summary>
    public string ResolveRealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ResolveRealPathHook?.Invoke(path) is { } ex) throw ex;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (string.IsNullOrEmpty(root)) return full;

        var relative = full[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // Mirrors PhysicalFileSystem (F02): a bare root ("C:\") comes back WITH its separator, not as the drive-relative "C:".
        if (segments.Length == 0) return root;

        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (_lock)
        {
            foreach (var segment in segments)
            {
                var candidate = current.Length == 0 ? segment : current + Path.DirectorySeparatorChar + segment;
                current = _reparsePoints.TryGetValue(NormalizeDirectoryPath(candidate), out var target) ? target : candidate;
            }
        }

        return current;
    }

    private void InternalCreateDirectory(string normalizedDir)
    {
        if (string.IsNullOrEmpty(normalizedDir) || IsDriveRoot(normalizedDir))
        {
            return;
        }

        if (_directories.Add(normalizedDir))
        {
            var parent = Path.GetDirectoryName(normalizedDir);
            if (!string.IsNullOrEmpty(parent))
            {
                InternalCreateDirectory(NormalizeDirectoryPath(parent));
            }
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TrackedAppendStream : MemoryStream
    {
        private readonly Action<byte[]> _onCommit;
        private bool _disposed;

        public TrackedAppendStream(byte[] initialData, Action<byte[]> onCommit)
        {
            _onCommit = onCommit;
            if (initialData.Length > 0)
            {
                Write(initialData, 0, initialData.Length);
            }
        }

        public override void Flush()
        {
            base.Flush();
            _onCommit(ToArray());
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _onCommit(ToArray());
                }

                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
