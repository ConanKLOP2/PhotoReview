using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Tests;

/// <summary>
/// NOWPF-AOT-SETTINGS-FALLBACK: khi cổng cửa sổ phụ vắng (Native AOT / nạp cầu lỗi) lệnh Settings mở config.json bằng
/// launcher (không ghi tệp), các cửa sổ khác báo "không có trong bản này"; không ném, không chặn.
/// </summary>
public sealed class SecondaryWindowFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aotgate-fallback-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Paths(string configFile) : IAppPaths
    {
        public string ConfigFile => configFile;
        public string JournalFile => throw new NotSupportedException();
        public string SessionsDir => throw new NotSupportedException();
        public string LogFile => throw new NotSupportedException();
        public string PreviewCacheDir => throw new NotSupportedException();
        public string ThumbnailCacheDir => throw new NotSupportedException();
        public string WindowPlacementFile => throw new NotSupportedException();
    }

    private sealed class FakeLauncher : IShellLauncher
    {
        public bool OpenResult = true;
        public bool RunResult = true;
        public bool ThrowOnOpen;
        public List<(string Path, string Verb)> Opens { get; } = [];
        public List<(string File, string Args)> Runs { get; } = [];

        public bool TryOpenFile(string path, string verb)
        {
            Opens.Add((path, verb));
            if (ThrowOnOpen)
            {
                throw new InvalidOperationException("boom");
            }

            return OpenResult;
        }

        public bool TryRun(string fileName, string arguments)
        {
            Runs.Add((fileName, arguments));
            return RunResult;
        }
    }

    private sealed class Spy : ISecondaryWindowPort
    {
        public int Calls;

        public bool ShowSettings(nint ownerHwnd, SettingsTarget target) { Calls++; return true; }

        public void ShowRecovery(nint ownerHwnd) => Calls++;

        public void ShowDiagnostics(nint ownerHwnd) => Calls++;

        public void ShowBenchmark(nint ownerHwnd, string? folder) => Calls++;

        public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries) => Calls++;

        public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items) { Calls++; return true; }

        public int? PromptCustomZoom(nint ownerHwnd, int currentPercent) { Calls++; return 1; }
    }

    private sealed class NullDialogs : ITaskDialogPort
    {
        public int Shown;

        public int Show(nint owner, string title, string message, TaskDialogKind kind)
        {
            Shown++;
            return 1;
        }
    }

    private (Win32DialogService Service, FakeLauncher Launcher, List<string> Notices, string Config, NullDialogs Dialogs) Create(
        ISecondaryWindowPort? port = null, Func<ISecondaryWindowPort?>? factory = null)
    {
        Directory.CreateDirectory(_dir);
        var config = Path.Combine(_dir, "config.json");
        var launcher = new FakeLauncher();
        var notices = new List<string>();
        var dialogs = new NullDialogs();
        var fallback = new SecondaryWindowFallback(new Paths(config), launcher, notices.Add);
        var lazy = new Lazy<ISecondaryWindowPort?>(factory ?? (() => port));
        var service = new Win32DialogService(() => 0, dialogs, new FolderPicker(UnwiredShellFolderDialog.Instance, () => 0), lazy, fallback);
        return (service, launcher, notices, config, dialogs);
    }

    [Fact]
    public void ShowSettings_NullPort_OpensConfigJsonWithOpenVerbAndNotifies()
    {
        var (service, launcher, notices, config, dialogs) = Create();

        var saved = service.ShowSettings();

        Assert.False(saved);
        Assert.Equal([(config, "open")], launcher.Opens);
        Assert.Empty(launcher.Runs);
        Assert.Equal([Tr.StatusSecondaryUnavailableSettings], notices);
        Assert.Equal(0, dialogs.Shown); // không chặn: không hộp thoại modal
        Assert.False(File.Exists(config)); // chỉ mở, không bao giờ tạo/ghi tệp
    }

    [Fact]
    public void ShowSettings_NoAssociatedProgram_FallsBackToNotepadWithQuotedPath()
    {
        var (service, launcher, notices, config, _) = Create();
        launcher.OpenResult = false;

        service.ShowSettings(SettingsTarget.Default);

        Assert.Single(launcher.Opens);
        Assert.Equal([("notepad.exe", "\"" + config + "\"")], launcher.Runs);
        Assert.Equal([Tr.StatusSecondaryUnavailableSettings], notices);
    }

    [Fact]
    public void ShowSettings_LauncherThrows_FallsBackToNotepadWithoutThrowing()
    {
        var (service, launcher, notices, _, _) = Create();
        launcher.ThrowOnOpen = true;

        var saved = service.ShowSettings();

        Assert.False(saved);
        Assert.Single(launcher.Runs);
        Assert.Equal([Tr.StatusSecondaryUnavailableSettings], notices);
    }

    [Fact]
    public void ShowSettings_NothingCanOpenTheFile_ReportsThePathWithoutThrowing()
    {
        var (service, launcher, notices, config, _) = Create();
        launcher.OpenResult = false;
        launcher.RunResult = false;

        var saved = service.ShowSettings();

        Assert.False(saved);
        Assert.Equal([Tr.StatusSecondaryUnavailableSettingsFailed(config)], notices);
        Assert.Contains(config, notices[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OtherWindows_NullPort_NotifyAndNeverLaunchAnything()
    {
        var (service, launcher, notices, _, dialogs) = Create();

        service.ShowDiagnostics();
        service.ShowRecovery();
        service.ShowSkippedFiles([]);
        service.ShowBenchmark(@"C:\photos");
        var batch = service.ShowBatchReview([new BatchReviewItem(@"C:\a.jpg", null)]);

        Assert.False(batch);
        Assert.Equal(
            [
                Tr.StatusSecondaryUnavailableDiagnostics,
                Tr.StatusSecondaryUnavailableRecovery,
                Tr.StatusSecondaryUnavailableSkippedFiles,
                Tr.StatusSecondaryUnavailableBenchmark,
                Tr.StatusSecondaryUnavailableBatchReview,
            ],
            notices);
        Assert.Empty(launcher.Opens);
        Assert.Empty(launcher.Runs);
        Assert.Equal(0, dialogs.Shown);
    }

    [Fact]
    public void Recovery_Message_SaysItRunsInTheFullBuild()
    {
        Assert.Contains("full build", Tr.StatusSecondaryUnavailableRecovery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not available in this build", Tr.StatusSecondaryUnavailableSettings, StringComparison.Ordinal);
        Assert.Contains("config.json", Tr.StatusSecondaryUnavailableSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void PortLoadThrows_BehavesAsNullPort()
    {
        var (service, launcher, notices, config, _) = Create(factory: () => throw new FileLoadException("bridge"));

        service.ShowDiagnostics();
        var saved = service.ShowSettings();

        Assert.False(saved);
        Assert.Equal([config], launcher.Opens.Select(o => o.Path));
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void PortPresent_DelegatesAndNeverUsesTheFallback()
    {
        var spy = new Spy();
        var (service, launcher, notices, _, _) = Create(spy);

        Assert.True(service.ShowSettings());
        service.ShowDiagnostics();
        service.ShowRecovery();

        Assert.Equal(3, spy.Calls);
        Assert.Empty(launcher.Opens);
        Assert.Empty(notices);
    }

    [Fact]
    public void NotifyThrows_DoesNotBreakTheCommand()
    {
        var launcher = new FakeLauncher();
        var fallback = new SecondaryWindowFallback(new Paths(Path.Combine(_dir, "config.json")), launcher,
            _ => throw new InvalidOperationException("ui gone"));

        var result = fallback.OpenSettings();

        Assert.False(result);
        Assert.Single(launcher.Opens);
    }

    [Fact]
    public void NoFallbackWired_KeepsTheOldNoOpBehaviour()
    {
        var lazy = new Lazy<ISecondaryWindowPort?>(() => null);
        var service = new Win32DialogService(() => 0, new NullDialogs(), new FolderPicker(UnwiredShellFolderDialog.Instance, () => 0), lazy);

        service.ShowDiagnostics();
        Assert.False(service.ShowSettings());
    }
}
