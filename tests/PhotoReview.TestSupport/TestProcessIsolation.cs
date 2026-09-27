using System.IO;

namespace PhotoReview.TestSupport;

/// <summary>
/// Process-wide safety net: unless a test host already chose a data root, points PHOTOREVIEW_DATA_ROOT (+ the isolate
/// flag) at a throw-away temp directory, so no test that builds AppPaths.FromEnvironment() (MainWindow, FileLog,
/// SessionStore, caches, window-placement.json, config.json ...) can touch the real %LOCALAPPDATA%\PhotoReview, even
/// when it forgets <see cref="DataRootFixture"/>. Installed from a module initializer in each test assembly.
/// </summary>
public static class TestProcessIsolation
{
    private static readonly object Gate = new();
    private static string? _root;

    /// <summary>The process-wide fallback root (null until <see cref="Install"/> ran).</summary>
    public static string? Root => _root;

    public static void Install()
    {
        lock (Gate)
        {
            if (_root is not null) return;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT"))) return;
            _root = Path.Combine(Path.GetTempPath(), "PhotoReview-Test-process-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", _root);
            Environment.SetEnvironmentVariable("PHOTOREVIEW_ISOLATE_CONFIG", "1");
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            };
        }
    }
}
