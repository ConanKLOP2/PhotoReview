using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Tests;

/// <summary>
/// Branch coverage of <c>App.ConfigureServices</c> (App.xaml.cs): the lazily evaluated lambdas and conditional arms that the
/// resolve-everything tests in <see cref="CompositionRootTests"/> never reach. Runs under a temp data root and a private
/// <see cref="FileLog"/>, and restores the process-wide <see cref="AppLog"/> instance.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class AppConfigureServicesBranchTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly FileLog _log;

    public AppConfigureServicesBranchTests()
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

    private static ServiceProvider Build(Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        overrides?.Invoke(services); // a later registration wins for GetRequiredService
        return services.BuildServiceProvider();
    }

    private sealed class RecordingLog : ILog
    {
        private readonly object _gate = new();
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings { get { lock (_gate) return [.. _warnings]; } }
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { lock (_gate) _warnings.Add(message); }
        public void Error(string message, Exception? ex = null) { }
    }

    [Fact]
    public void SourceBytesCachePolicy_EnabledButZeroCapacity_IsDisabledAndWarnsAboutNoRoom()
    {
        var recording = new RecordingLog();
        using var provider = Build(s => s.AddSingleton<ILog>(recording));
        var settings = provider.GetRequiredService<SettingsStore>().Current;
        settings.UseSourceBytesCache = true;
        settings.SourceBytesCapacityBytes = 0; // the share left over for the source-bytes cache is 0

        var policy = provider.GetRequiredService<SourceBytesCachePolicy>();

        Assert.Null(policy.Cache);
        Assert.Contains(recording.Warnings, w => w.Contains("Source-bytes cache disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceBytesCachePolicy_EnabledWithRoom_CreatesTheCacheAndDoesNotWarn()
    {
        var recording = new RecordingLog();
        using var provider = Build(s => s.AddSingleton<ILog>(recording));
        var settings = provider.GetRequiredService<SettingsStore>().Current;
        settings.UseSourceBytesCache = true;
        settings.SourceBytesCapacityBytes = 64L * 1024 * 1024;

        var policy = provider.GetRequiredService<SourceBytesCachePolicy>();

        Assert.NotNull(policy.Cache);
        Assert.Empty(recording.Warnings);
    }

    [Fact]
    public void PreviewImageService_WiresBackendAndDecodeBoxToTheCurrentSettingsAndViewport()
    {
        using var provider = Build();
        var store = provider.GetRequiredService<SettingsStore>();
        var viewport = provider.GetRequiredService<ViewportSizeSource>();
        _ = provider.GetRequiredService<PreviewImageService>(); // composing it installs the delegates on the context
        var ctx = provider.GetRequiredService<PreviewStateContext>();

        store.Current.DecoderBackend = DecoderBackend.TurboJpeg;
        viewport.TargetDecodeBox = new DecodeBox(1234, 567);

        Assert.Equal(DecoderBackend.TurboJpeg, ctx.CurrentBackend());
        Assert.Equal(new DecodeBox(1234, 567), ctx.TargetDecodeBox());

        // The delegates are live, not a snapshot taken at composition.
        store.Current.DecoderBackend = DecoderBackend.WicDirect;
        viewport.TargetDecodeBox = new DecodeBox(800, 600);
        Assert.Equal(DecoderBackend.WicDirect, ctx.CurrentBackend());
        Assert.Equal(new DecodeBox(800, 600), ctx.TargetDecodeBox());
    }

    [Fact]
    public void DecoderFactory_RawSupportSwitch_IsReadLiveFromTheSettings()
    {
        using var provider = Build();
        var store = provider.GetRequiredService<SettingsStore>();
        var decoder = provider.GetRequiredService<IImageDecoderFactory>().Create(DecoderBackend.Wpf);
        var missingRaw = Path.Combine(_data.Path, "no-such-file.cr3");

        store.Current.RawSupportEnabled = false;
        var disabled = Assert.Throws<NotSupportedException>(() => decoder.ReadInfo(missingRaw));
        Assert.Contains("RAW support is disabled", disabled.Message, StringComparison.Ordinal);

        // Enabled: the file is looked up by the RAW decoder instead (it does not exist), so the failure is no longer "disabled".
        store.Current.RawSupportEnabled = true;
        var enabled = Record.Exception(() => decoder.ReadInfo(missingRaw));
        Assert.NotNull(enabled);
        Assert.DoesNotContain("RAW support is disabled", enabled.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsStore_ReportsAStartupErrorThroughTheForcedLogWhileLoggingIsOff()
    {
        using var provider = Build();
        var paths = provider.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigFile)!);
        File.WriteAllText(paths.ConfigFile, "{ this is not json");
        Assert.False(AppLog.Enabled);

        provider.GetRequiredService<SettingsStore>().Load();

        // The store was composed with App.LogStartupErrorForced: the entry reached the app log although logging is off.
        Assert.Contains("Corrupt config.json detected", File.ReadAllText(AppLog.FilePath), StringComparison.Ordinal);
        Assert.False(AppLog.Enabled);
    }

    [Fact]
    public void ILog_IsTheSharedProcessLog_AndTheContainerDoesNotDisposeIt()
    {
        var provider = Build();
        var log = provider.GetRequiredService<ILog>();
        Assert.Same(FileLog.Default, log);

        provider.Dispose();

        // FileLog.Default is owned by the process (App.Dispose), not by the container: it must still be usable.
        var disposed = (bool)typeof(FileLog).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(FileLog.Default)!;
        Assert.False(disposed);
    }

    // Application.Current is a process-wide static that an earlier UI test may have left behind; hide it for the test so the
    // fallback arm (no Application) is exercised deterministically, then restore it.
    private static readonly FieldInfo AppInstanceField =
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void UiScheduler_WithoutAnApplication_UsesTheCurrentThreadsDispatcher()
    {
        var saved = AppInstanceField.GetValue(null);
        AppInstanceField.SetValue(null, null);
        try
        {
            Assert.Null(Application.Current);
            using var provider = Build();

            var scheduler = Assert.IsType<DispatcherUiScheduler>(provider.GetRequiredService<IUiScheduler>());

            Assert.Same(Dispatcher.CurrentDispatcher, SchedulerDispatcher(scheduler));
        }
        finally
        {
            AppInstanceField.SetValue(null, saved);
        }
    }

    private static Dispatcher SchedulerDispatcher(DispatcherUiScheduler scheduler) =>
        (Dispatcher)typeof(DispatcherUiScheduler).GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!;
}
