using System.IO;

namespace PhotoReview.TestSupport;

/// <summary>A disposable temporary directory, mirroring the console suite's per-run root.</summary>
public sealed class TempRoot : IDisposable
{
    public string Path { get; }

    public TempRoot(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PhotoReview-Test-" + (name is null ? string.Empty : name + "-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string File(string relative, params byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public string Dir(string relative) => Directory.CreateDirectory(Combine(relative)).FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>
/// A temporary directory that is also installed as PHOTOREVIEW_DATA_ROOT for the
/// duration of the test, so SessionStore/OperationJournal/AppLog write into it. config.json is isolated there too
/// (PHOTOREVIEW_ISOLATE_CONFIG): a test that saves settings must never overwrite the user's real config.
/// </summary>
public sealed class DataRootFixture : IDisposable
{
    private readonly string? _previous;
    private readonly string? _previousIsolate;

    public TempRoot Root { get; } = new("data");

    public DataRootFixture()
    {
        _previous = Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT");
        _previousIsolate = Environment.GetEnvironmentVariable("PHOTOREVIEW_ISOLATE_CONFIG");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", Root.Combine("app-data"));
        Environment.SetEnvironmentVariable("PHOTOREVIEW_ISOLATE_CONFIG", "1");
    }

    public string Path => Root.Path;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", _previous);
        Environment.SetEnvironmentVariable("PHOTOREVIEW_ISOLATE_CONFIG", _previousIsolate);
        Root.Dispose();
    }
}
