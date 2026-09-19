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
                    return state;
                }
            }
        }
        catch (JsonException) { }
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
        _fileSystem.WriteAllTextAtomic(path, json);
        _metrics?.RecordSessionWrite();
    }

    public string GetPath(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var fullPath = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(fullPath)!;
        var canonical = fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : root;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        return Path.Combine(_sessionsDir, key + ".json");
    }
}
