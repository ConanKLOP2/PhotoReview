using System.IO;
using System.Reflection;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>Logging opt-in/opt-out and concurrency contracts.</summary>
public sealed class AppLogTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public AppLogTests() => AppLog.Enabled = false;

    public void Dispose()
    {
        AppLog.Enabled = false;
        _data.Dispose();
    }

    /// <summary>Keeps tests compatible with both synchronous and buffered logger implementations.</summary>
    private static void FlushAppLog()
    {
        var method = typeof(AppLog).GetMethod("Flush", BindingFlags.Public | BindingFlags.Static);
        method?.Invoke(null, null);
    }

    [Fact(DisplayName = "Logging defaults off")]
    public void LoggingDefaultsOff() => Assert.True(!new AppSettings().LoggingEnabled && !AppLog.Enabled);

    [Fact(DisplayName = "Existing config without logging flag keeps logging off")]
    public void ExistingConfigWithoutLoggingFlagKeepsLoggingOff()
    {
        var legacySettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":2}");
        Assert.True(legacySettings is { LoggingEnabled: false });
    }

    [Fact(DisplayName = "Disabled logging creates no directory or file, including errors")]
    public void DisabledLoggingCreatesNoDirectoryOrFile()
    {
        AppLog.Info("disabled-info");
        AppLog.Error("disabled-error", new Exception("test"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(AppLog.FilePath)));
    }

    [Fact(DisplayName = "Explicitly enabled logging writes diagnostics")]
    public void ExplicitlyEnabledLoggingWritesDiagnostics()
    {
        AppLog.Enabled = true;
        AppLog.Info("enabled-info");
        AppLog.Error("enabled-error");
        FlushAppLog();
        var logContents = File.ReadAllText(AppLog.FilePath);
        Assert.True(logContents.Contains("enabled-info") && logContents.Contains("enabled-error"));
    }

    [Fact(DisplayName = "Turning logging off stops all diagnostic writes")]
    public void TurningLoggingOffStopsAllDiagnosticWrites()
    {
        AppLog.Enabled = true;
        AppLog.Info("enabled-info");
        AppLog.Error("enabled-error");
        FlushAppLog();
        var logContents = File.ReadAllText(AppLog.FilePath);
        AppLog.Enabled = false;
        AppLog.Info("disabled-again");
        AppLog.Error("disabled-again");
        Assert.Equal(logContents, File.ReadAllText(AppLog.FilePath));
    }

    [Fact(DisplayName = "Concurrent logging preserves every entry")]
    public void ConcurrentLoggingPreservesEveryEntry()
    {
        AppLog.Enabled = true;
        var concurrentMarkers = Enumerable.Range(0, 200).Select(i => $"concurrent-marker-{i}").ToArray();
        Parallel.ForEach(concurrentMarkers, marker => AppLog.Info(marker));
        FlushAppLog();
        var concurrentLog = File.ReadAllText(AppLog.FilePath);
        Assert.True(concurrentMarkers.All(concurrentLog.Contains));
    }

    [Fact(DisplayName = "Concurrent log entries remain line-delimited")]
    public void ConcurrentLogEntriesRemainLineDelimited()
    {
        AppLog.Enabled = true;
        var concurrentMarkers = Enumerable.Range(0, 200).Select(i => $"concurrent-marker-{i}").ToArray();
        Parallel.ForEach(concurrentMarkers, marker => AppLog.Info(marker));
        FlushAppLog();
        var concurrentLog = File.ReadAllText(AppLog.FilePath);
        Assert.True(concurrentLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("concurrent-marker-", StringComparison.Ordinal)) >= concurrentMarkers.Length);
    }

    [Fact(DisplayName = "Log file rotates to a .1 backup at the 10 MB threshold and new entries land in a fresh file")]
    public void LogRotatesAtSizeThreshold()
    {
        var path = AppLog.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Plant an oversized file directly instead of writing 10 MB through the logger:
        // NeedsRotation only checks the file's on-disk length, so this exercises the same
        // rotation trigger far faster than a real 10 MB write.
        File.WriteAllBytes(path, new byte[10 * 1024 * 1024]);
        var backupPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));

        AppLog.Enabled = true;
        AppLog.Info("post-rotation-marker");
        FlushAppLog();

        Assert.True(File.Exists(backupPath), "Expected the oversized log to be rotated to a .1 backup.");
        Assert.True(new FileInfo(backupPath).Length >= 10 * 1024 * 1024, "Backup should retain the oversized content that triggered rotation.");
        Assert.True(new FileInfo(path).Length < 10 * 1024 * 1024, "New log file should start fresh after rotation instead of continuing to grow the oversized file.");
        Assert.Contains("post-rotation-marker", File.ReadAllText(path));
    }
}

/// <summary>Session persistence contract.</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public void Dispose() => _data.Dispose();

    [Fact(DisplayName = "Session save/load")]
    public void SessionSaveAndLoad()
    {
        var root = _data.Path;
        var store = new SessionStore();
        var state = new SessionState
        {
            Folder = root,
            CurrentPath = Path.Combine(root, "one.jpg"),
            Skipped = [Path.Combine(root, "skip.jpg")]
        };
        store.Save(state);
        var loaded = store.Load(root);
        Assert.True(loaded.CurrentPath == state.CurrentPath && loaded.Skipped.Count == 1);
    }
}
