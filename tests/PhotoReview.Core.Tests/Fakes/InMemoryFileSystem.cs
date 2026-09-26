using System.IO;
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

    // --- Hook chèn lỗi cho unit testing ---
    public Func<string, Exception?>? OpenReadHook { get; set; }
    public Func<string, Exception?>? OpenAppendHook { get; set; }
    public Func<string, Exception?>? WriteHook { get; set; }
    /// <summary>Runs (outside the internal lock, so it may block) at the start of every <see cref="GetFileStat"/>; a returned exception is thrown.</summary>
    public Func<string, Exception?>? StatHook { get; set; }
    public Func<string, Exception?>? DeleteHook { get; set; }
    public Func<string, string, Exception?>? MoveHook { get; set; }
    public Func<string, string, Exception?>? CopyHook { get; set; }

    /// <summary>
    /// Simulates a cross-volume MoveFileEx(MOVEFILE_COPY_ALLOWED) that copied the file but could not delete the source
    /// (read-only / open without FILE_SHARE_DELETE): Move then returns normally and leaves the source in place.
    /// </summary>
    public bool MoveLeavesSource { get; set; }

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
                throw new IOException($"Tệp tin đích đã tồn tại: '{destination}'.");
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
            _fileWriteTimes[dstNorm] = writeTime;
        }
    }

    public void Copy(string source, string destination)
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
                throw new IOException($"Tệp tin đích đã tồn tại: '{destination}'.");
            }

            var destDir = NormalizeDirectoryPath(Path.GetDirectoryName(destination) ?? string.Empty);
            if (!string.IsNullOrEmpty(destDir) && !_directories.Contains(destDir) && !IsDriveRoot(destDir))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục đích: '{destDir}'.");
            }

            _files[dstNorm] = (byte[])bytes.Clone();
            _fileWriteTimes[dstNorm] = DateTime.UtcNow;
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

    public Stream OpenAppendDurable(string path) => OpenAppend(path, durable: true);

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
