using System.Text.Json;
using System.IO;

namespace PhotoReview.App;

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
    private static string Root => Path.Combine(Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "Data"), "Sessions");

    public SessionState Load(string folder)
    {
        var path = GetPath(folder);
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { }
        return new SessionState { Folder = folder };
    }

    public void Save(SessionState state)
    {
        Directory.CreateDirectory(Root);
        var path = GetPath(state.Folder);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, path, true);
    }

    private static string GetPath(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(fullPath)!;
        var canonical = fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : root;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        return Path.Combine(Root, key + ".json");
    }
}
