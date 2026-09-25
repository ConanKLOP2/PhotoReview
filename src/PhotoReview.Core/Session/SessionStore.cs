using PhotoReview.Core.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Session;

public sealed class SessionState
{
    public string Folder { get; set; } = "";
    public string? CurrentPath { get; set; }
    public List<string> Skipped { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);
    private readonly string _sessionsDir;
    private readonly IFileSystem _fileSystem;
    private readonly ReviewMetrics? _metrics;

    public SessionStore()
        : this(PhotoReview.Core.AppPaths.FromEnvironment(), new PhysicalFileSystem())
    {
    }

    public SessionStore(IAppPaths paths, IFileSystem fileSystem, ReviewMetrics? metrics = null)
    {
        _metrics = metrics;
        ArgumentNullException.ThrowIfNull(paths);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sessionsDir = paths.SessionsDir;
        SweepStaleTempFiles(DateTime.UtcNow);
    }

    /// <summary>
    /// R2-F-34: an atomic write killed between creating <c>*.tmp</c> and the rename leaves the temp file behind for good.
    /// Removes temp files older than a day (a live writer's temp file is milliseconds old); best effort, never throws.
    /// </summary>
    internal int SweepStaleTempFiles(DateTime utcNow)
    {
        var removed = 0;
        try
        {
            foreach (var temp in _fileSystem.EnumerateFiles(_sessionsDir, "*.tmp").ToList())
            {
                if (_fileSystem.GetFileStat(temp) is { } stat && utcNow - stat.LastWriteUtc > StaleTempAge)
                {
                    _fileSystem.Delete(temp);
                    removed++;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return removed;
    }

    public SessionState Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var path = GetPath(folder);
        try
        {
            if (_fileSystem.FileExists(path))
            {
                var json = _fileSystem.ReadAllText(path);
                var state = JsonSerializer.Deserialize<SessionState>(json);
                if (state is not null)
                {
                    if (string.IsNullOrWhiteSpace(state.Folder))
                    {
                        state.Folder = folder;
                    }
                    state.Skipped ??= [];
                    return state;
                }
            }
        }
        catch (JsonException)
        {
            // ADR 0007 section 2: without fsync a power loss can leave an empty/partial session file.
            // That is "no session", not an error for the user.
            FileLog.Default.Warn($"Session file '{path}' is empty or corrupt; starting without a session.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new SessionState { Folder = folder };
    }

    public void Save(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Folder);
        _fileSystem.CreateDirectory(_sessionsDir);
        var path = GetPath(state.Folder);
        var json = JsonSerializer.Serialize(state, Options);
        _fileSystem.WriteAllTextAtomic(path, json, durable: false); // ADR 0007 section 2: atomic, no fsync
        _metrics?.RecordSessionWrite();
    }

    public string GetPath(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var canonical = CanonicalFolder(folder);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        return Path.Combine(_sessionsDir, key + ".json");
    }

    /// <summary>Full path without a trailing separator (drive roots keep theirs): the identity every spelling of one folder shares.</summary>
    internal static string CanonicalFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(fullPath)!;
        return fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : root;
    }
}
