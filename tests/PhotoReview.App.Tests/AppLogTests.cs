using PhotoReview.Core.Diagnostics;
using System.IO;
using System.Reflection;
using PhotoReview.App;

namespace PhotoReview.App.Tests;

/// <summary>Logging opt-in/opt-out and concurrency contracts.</summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class AppLogTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    private readonly FileLog _log;

    // FileLog.Default is a process-wide lazy singleton whose path is fixed on first use, which
    // may happen before this fixture sets PHOTOREVIEW_DATA_ROOT; give these tests their own instance.
    public AppLogTests()
    {
        _log = new FileLog(PhotoReview.Core.AppPaths.FromEnvironment());
        AppLog.Instance = _log;
        AppLog.Enabled = false;
    }

    public void Dispose()
    {
        AppLog.Enabled = false;
        AppLog.Instance = null!;
        _log.Dispose();
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
        AppLog.Error("disabled-error", new InvalidOperationException("test"));
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

    [Fact(DisplayName = "Unhandled exceptions are recorded and flushed even while logging is off")]
    public void UnhandledExceptionsAreRecordedWhileLoggingIsOff()
    {
        Assert.False(AppLog.Enabled);
        App.LogUnhandledForced("crash-marker", new InvalidOperationException("boom-detail"));
        // No FlushAppLog(): the helper itself must have flushed.
        var text = File.ReadAllText(AppLog.FilePath);
        Assert.Contains("crash-marker", text, StringComparison.Ordinal);
        Assert.Contains("boom-detail", text, StringComparison.Ordinal);
        Assert.False(AppLog.Enabled);
    }

    [Fact(DisplayName = "Concurrent forced logging leaves the enabled flag as it found it")]
    public void ConcurrentForcedLoggingRestoresTheEnabledFlag()
    {
        Assert.False(AppLog.Enabled);
        var ex = new InvalidOperationException("boom");
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => App.LogStartupErrorForced("forced-" + i, ex));
        Assert.False(AppLog.Enabled);
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
}
