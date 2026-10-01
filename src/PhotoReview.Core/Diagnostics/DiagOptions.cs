using System;
using System.Collections.Generic;
using System.Globalization;

namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// D05: diagnostic-only environment variables (see PERF-DIAGNOSIS-TASKS (removed doc, see git history)
/// D05/D10). Values are parsed once per process, on first access, via a
/// <see cref="Lazy{T}"/> so a mid-run environment mutation (e.g. a test in the same AppDomain)
/// never causes two reads in the same process to disagree. When none of these variables are set,
/// every property below returns its "disabled" default and app behavior must be byte-for-byte the
/// same as before this class existed (PERF-DIAGNOSIS-TASKS (removed doc, see git history), quy tắc riêng #2).
///
/// <see cref="PreloadWorkers"/> and <see cref="DisableDiskCache"/> are consumed by D10
/// (PreloadScheduler / PreviewImageService disk cache); D05 only declares them and covers them with
/// tests. <see cref="PreRead"/> is consumed here in D05, by <c>PreviewImageService.DecodeFromSource</c>.
/// <see cref="ForceLog"/> is consumed by <c>App.xaml.cs</c> to enable <c>AppLog</c> at startup for a
/// single debugging run, without touching <c>AppSettings.LoggingEnabled</c> (config.json) or rebuilding.
/// </summary>
public static class DiagOptions
{
    internal const string PreReadVar = "PHOTOREVIEW_DIAG_PREREAD";
    internal const string PreloadWorkersVar = "PHOTOREVIEW_DIAG_PRELOAD_WORKERS";
    internal const string DisableDiskCacheVar = "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE";
    internal const string ForceLogVar = "PHOTOREVIEW_DIAG_FORCE_LOG";
    internal const string InstanceLabelVar = "PHOTOREVIEW_DIAG_INSTANCE_LABEL";

    private static volatile Lazy<Snapshot> _snapshot = new(ReadFromEnvironment);

    private sealed record Snapshot(bool PreRead, int? PreloadWorkers, bool DisableDiskCache, bool ForceLog, string? InstanceLabel);

    /// <summary>PHOTOREVIEW_DIAG_PREREAD=1: read the whole source file into memory before decoding,
    /// so <c>SourceRead</c> and <c>Decode</c> perf events report I/O and decode time separately.</summary>
    public static bool PreRead => _snapshot.Value.PreRead;

    /// <summary>PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0..16: overrides the preload worker count
    /// (D10). Out-of-range or unparsable values are treated as unset (null).</summary>
    public static int? PreloadWorkers => _snapshot.Value.PreloadWorkers;

    /// <summary>PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1: skip reading/writing the preview disk cache
    /// (D10).</summary>
    public static bool DisableDiskCache => _snapshot.Value.DisableDiskCache;

    /// <summary>PHOTOREVIEW_DIAG_FORCE_LOG=1: force <c>AppLog.Enabled</c> on at startup regardless of
    /// <c>AppSettings.LoggingEnabled</c>, for a single debugging run.</summary>
    public static bool ForceLog => _snapshot.Value.ForceLog;

    /// <summary>
    /// PHOTOREVIEW_DIAG_INSTANCE_LABEL=&lt;text&gt;: label shown as a "[text] " prefix on the main window's
    /// title bar (<c>MainViewModel.InstanceLabel</c>), so an instance launched directly for manual/agent
    /// verification is never mistaken for the user's everyday window. Null when unset or blank -- same
    /// "disabled by default" contract as every other flag here.
    /// </summary>
    public static string? InstanceLabel => _snapshot.Value.InstanceLabel;

    /// <summary>True when any PHOTOREVIEW_DIAG_* variable above is in effect.</summary>
    public static bool AnyEnabled => PreRead || PreloadWorkers.HasValue || DisableDiskCache || ForceLog || InstanceLabel is not null;

    /// <summary>Stable, human-readable summary of every active flag, e.g. "PREREAD=1;PRELOAD_WORKERS=2".
    /// Used for the forced startup log line.</summary>
    public static string Describe()
    {
        var snapshot = _snapshot.Value;
        var parts = new List<string>();
        if (snapshot.PreRead) parts.Add("PREREAD=1");
        if (snapshot.PreloadWorkers.HasValue) parts.Add(string.Create(CultureInfo.InvariantCulture, $"PRELOAD_WORKERS={snapshot.PreloadWorkers.Value}"));
        if (snapshot.DisableDiskCache) parts.Add("DISABLE_DISKCACHE=1");
        if (snapshot.ForceLog) parts.Add("FORCE_LOG=1");
        if (snapshot.InstanceLabel is { } label) parts.Add($"INSTANCE_LABEL={label}");
        return parts.Count == 0 ? "(none)" : string.Join(";", parts);
    }

    /// <summary>Test-only: forces the next read to re-parse environment variables. Production code
    /// never calls this; it exists so xUnit tests in the "GlobalState" collection can set an
    /// environment variable and observe it without restarting the process. Public (rather than
    /// internal) so the test projects can call it without depending on InternalsVisibleTo.</summary>
    public static void ResetForTests() => _snapshot = new Lazy<Snapshot>(ReadFromEnvironment);

    private static Snapshot ReadFromEnvironment()
    {
        var preRead = Environment.GetEnvironmentVariable(PreReadVar) == "1";
        var disableDiskCache = Environment.GetEnvironmentVariable(DisableDiskCacheVar) == "1";
        var forceLog = Environment.GetEnvironmentVariable(ForceLogVar) == "1";
        int? workers = null;
        var workersRaw = Environment.GetEnvironmentVariable(PreloadWorkersVar);
        // RV-S02: a diagnostic value is machine text, never parsed with the user's culture (CA1305 / AGENTS.md rule 4).
        if (int.TryParse(workersRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 16) workers = parsed;
        var instanceLabelRaw = Environment.GetEnvironmentVariable(InstanceLabelVar);
        var instanceLabel = string.IsNullOrWhiteSpace(instanceLabelRaw) ? null : instanceLabelRaw;
        return new Snapshot(preRead, workers, disableDiskCache, forceLog, instanceLabel);
    }
}
