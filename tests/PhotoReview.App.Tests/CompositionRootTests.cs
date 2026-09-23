using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Services;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests;

[Trait("Category", "HotPath")]
public class CompositionRootTests
{
    [Fact]
    public void ConfigureServices_RegistersAllExpectedCoreServices()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        // 1. Core Abstractions
        Assert.NotNull(provider.GetRequiredService<IAppPaths>());
        Assert.NotNull(provider.GetRequiredService<IFileSystem>());
        Assert.NotNull(provider.GetRequiredService<IClock>());
        Assert.NotNull(provider.GetRequiredService<ILog>());

        // 2. Settings & Session
        Assert.NotNull(provider.GetRequiredService<SettingsStore>());
        Assert.NotNull(provider.GetRequiredService<SessionStore>());

        // 3. Journal & File Actions
        Assert.NotNull(provider.GetRequiredService<OperationJournal>());
        Assert.NotNull(provider.GetRequiredService<RecoveryRetryService>());
        Assert.NotNull(provider.GetRequiredService<FileHashService>());

        // 4. Diagnostics & Metrics
        Assert.NotNull(provider.GetRequiredService<ReviewMetrics>());

        // 5. Platform Services
        Assert.NotNull(provider.GetRequiredService<IExplorerOrderProvider>());
        Assert.NotNull(provider.GetRequiredService<IRecycleBin>());
        Assert.NotNull(provider.GetRequiredService<IMemoryProbe>());
        Assert.NotNull(provider.GetRequiredService<INaturalComparer>());
        Assert.NotNull(provider.GetRequiredService<IKeyNameValidator>());
        Assert.NotNull(provider.GetRequiredService<IUiScheduler>());
        Assert.NotNull(provider.GetRequiredService<ViewportSizeSource>());
        Assert.NotNull(provider.GetRequiredService<IPresentationObserver>());

        // 6. Imaging & Decoding
        Assert.NotNull(provider.GetRequiredService<IImageDecoderFactory>());
        Assert.NotNull(provider.GetRequiredService<ThumbnailCache>());
        Assert.NotNull(provider.GetRequiredService<PreviewStateContext>());
        Assert.NotNull(provider.GetRequiredService<PreviewImageService>());
        Assert.NotNull(provider.GetRequiredService<Func<Func<CatalogEntry[]>, Func<long>, PreloadScheduler>>());
    }

    // AR02a step 1: AppHost is the single entry point App.App_Startup, Benchmark.Cli (AR02c) and
    // integration tests (AR02b) build the container from.
    [Fact]
    public void AppHost_BuildServices_ResolvesMainViewModel()
    {
        using var provider = AppHost.BuildServices();

        Assert.NotNull(provider.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>());
    }

    // MainViewModel.Metrics (read by Benchmark.Cli through MainWindow.Metrics) must be the
    // ReviewMetrics singleton the presenter/preview services record into, not a private empty one.
    [Fact]
    public void AppHost_BuildServices_MainViewModelMetrics_IsSharedReviewMetricsSingleton()
    {
        using var provider = AppHost.BuildServices();

        var vm = provider.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>();

        Assert.Same(provider.GetRequiredService<ReviewMetrics>(), vm.Metrics);
    }

    [Fact]
    public void AppHost_BuildServices_AppliesOverridesAfterConfigureServices()
    {
        var sentinel = NullPresentationObserver.Instance;
        using var provider = AppHost.BuildServices(s => s.AddSingleton<IPresentationObserver>(sentinel));

        Assert.Same(sentinel, provider.GetRequiredService<IPresentationObserver>());
    }

    // AR02a step 2 (F4): ThumbnailCache/PreviewImageService must read their disk directory from
    // IAppPaths instead of duplicating the hard-coded %LOCALAPPDATA%\PhotoReview\{cache,thumbnails}
    // default independently -- otherwise IAppPaths and the imaging services can silently disagree.
    [Fact]
    public void ConfigureServices_ThumbnailCacheAndPreviewImageService_UseAppPathsDiskDirectories()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var paths = provider.GetRequiredService<IAppPaths>();
        var thumbnailCache = provider.GetRequiredService<ThumbnailCache>();
        var previewImageService = provider.GetRequiredService<PreviewImageService>();

        Assert.Equal(paths.ThumbnailCacheDir, thumbnailCache.DiskDirectory);
        Assert.Equal(paths.PreviewCacheDir, previewImageService.DiskDirectory);
    }

    // Default (no PHOTOREVIEW_DATA_ROOT override) IAppPaths cache directories must equal the
    // literal defaults ThumbnailCache/PreviewImageService used to hard-code themselves, so that
    // routing them through IAppPaths does not orphan an existing user's on-disk cache.
    [Fact]
    public void AppPaths_DefaultCacheDirectories_MatchOldHardCodedDefaults()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldThumbnailDefault = Path.Combine(localAppData, "PhotoReview", "thumbnails");
        var oldPreviewDefault = Path.Combine(localAppData, "PhotoReview", "cache");

        var paths = new PhotoReview.Core.AppPaths(localAppData);

        Assert.Equal(oldThumbnailDefault, paths.ThumbnailCacheDir);
        Assert.Equal(oldPreviewDefault, paths.PreviewCacheDir);
    }

    // NOTE: AppPaths deliberately does NOT redirect PreviewCacheDir/ThumbnailCacheDir when
    // PHOTOREVIEW_DATA_ROOT is set (see AppPathsTests.WithOverrideRedirectsJournalSessionsAndLogWhileKeepingConfigAndCache) --
    // only JournalFile/SessionsDir/LogFile move. Isolating on-disk image caches under
    // PHOTOREVIEW_DATA_ROOT is therefore still open (F4 is only fixed for the *duplication*, not
    // the isolation gap) and stays out of AR02a's scope. This test proves the DI wiring reads
    // whatever IAppPaths.ThumbnailCacheDir currently resolves to, override or not.
    [Collection("GlobalState")]
    public sealed class CacheDirectoriesFollowAppPathsUnderDataRootOverride
    {
        [Fact]
        public void ConfigureServices_WithDataRootOverride_ThumbnailCacheDiskDirectory_MatchesAppPaths()
        {
            using var dataRoot = new DataRootFixture();

            var services = new ServiceCollection();
            App.ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            var paths = provider.GetRequiredService<IAppPaths>();
            var thumbnailCache = provider.GetRequiredService<ThumbnailCache>();

            Assert.Equal(paths.ThumbnailCacheDir, thumbnailCache.DiskDirectory);
        }
    }

    // AR02a step 3 (F3): default ViewportSizeSource.Get returns (0, 0), matching the old
    // hard-coded behavior, until something rewires it.
    [Fact]
    public void ConfigureServices_ViewportSizeSource_DefaultsToZeroZero()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var (w, h) = provider.GetRequiredService<ViewportSizeSource>().Get();

        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    // AR02a step 3 (F3): MainWindow's DI constructor must rewire ViewportSizeSource.Get to its
    // own GetViewportSize right after InitializeComponent(), so ApplyInitialViewMode (via
    // MainViewModelCompositionRoot) sees the real viewport instead of (0, 0).
    [Fact]
    public void ConfigureServices_ResolvingMainWindow_WiresViewportSizeSourceToWindowViewport()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                    _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                try
                {
                    System.Windows.Application.ResourceAssembly = typeof(MainWindow).Assembly;
                }
                catch
                {
                    var appField = typeof(System.Windows.Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    appField?.SetValue(null, typeof(MainWindow).Assembly);
                }

                var services = new ServiceCollection();
                App.ConfigureServices(services);
                using var provider = services.BuildServiceProvider();

                var viewport = provider.GetRequiredService<ViewportSizeSource>();
                var window = provider.GetRequiredService<MainWindow>();
                var getViewportSizeMethod = typeof(MainWindow).GetMethod("GetViewportSize", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(getViewportSizeMethod);
                Assert.Equal(getViewportSizeMethod, viewport.Get.Method);
                Assert.Same(window, viewport.Get.Target);

                // The window also publishes the preview decode width (lost in T46d, so Preview
                // decoded at full size); PreviewImageService reads it through PreviewStateContext.
                Assert.InRange(viewport.TargetDecodeWidth, PhotoReview.Imaging.AdaptivePreviewPolicy.MinimumDecodeWidth, PhotoReview.Imaging.AdaptivePreviewPolicy.MaximumDecodeWidth);
                _ = provider.GetRequiredService<PhotoReview.Imaging.Caching.PreviewImageService>();
                Assert.Equal(viewport.TargetDecodeWidth, provider.GetRequiredService<PreviewStateContext>().TargetDecodeWidth());
            }
            catch (Exception ex) { threadException = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA MainWindow resolution timed out.");
        Assert.Null(threadException);
    }

    // AR02a step 4: production wires WpfPresentationSink's onPresented to the registered
    // IPresentationObserver (replacing MainWindowTestHooks.OnPresented).
    [Fact]
    public void ConfigureServices_DefaultPresentationObserver_IsNullPresentationObserver()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(NullPresentationObserver.Instance, provider.GetRequiredService<IPresentationObserver>());
    }

    // AR02a step 5: registering an IPreloadController override must suppress the real
    // PreloadScheduler entirely -- MainViewModelCompositionRoot must not create one it never uses.
    [Fact]
    public void ConfigureServices_WithRegisteredPreloadControllerOverride_MainViewModel_UsesOverrideInstead()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        var fake = new FakePreloadController();
        services.AddSingleton<IPreloadController>(fake);
        using var provider = services.BuildServiceProvider();

        var viewModel = provider.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>();

        Assert.Same(fake, viewModel.PreloadController);
    }

    private sealed class FakePreloadController : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    // AR02a step 6: with no IMoveOverride registered, FileActionService/UndoService receive a
    // null moveOverride (production moves files for real).
    [Fact]
    public void ConfigureServices_WithNoMoveOverrideRegistered_FileActionServiceAndUndoService_HaveNullMoveOverride()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var fileActions = provider.GetRequiredService<FileActionService>();
        var undo = provider.GetRequiredService<UndoService>();

        var fileActionsOverride = typeof(FileActionService)
            .GetField("_moveOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fileActions);
        var undoOverride = typeof(UndoService)
            .GetField("_moveOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(undo);

        Assert.Null(fileActionsOverride);
        Assert.Null(undoOverride);
    }

    // A registered IMoveOverride must reach both services as their moveOverride delegate.
    [Fact]
    public void ConfigureServices_WithMoveOverrideRegistered_FileActionServiceAndUndoService_UseIt()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        var fake = new FakeMoveOverride();
        services.AddSingleton<IMoveOverride>(fake);
        using var provider = services.BuildServiceProvider();

        var fileActions = provider.GetRequiredService<FileActionService>();
        var undo = provider.GetRequiredService<UndoService>();

        var fileActionsOverride = (Func<string, string, Task>?)typeof(FileActionService)
            .GetField("_moveOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fileActions);
        var undoOverride = (Func<string, string, Task>?)typeof(UndoService)
            .GetField("_moveOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(undo);

        Assert.NotNull(fileActionsOverride);
        Assert.NotNull(undoOverride);
        Assert.Same(fake, fileActionsOverride!.Target);
        Assert.Same(fake, undoOverride!.Target);
    }

    private sealed class FakeMoveOverride : IMoveOverride
    {
        public Task MoveAsync(string source, string destination) => Task.CompletedTask;
    }

    [Fact]
    public void ConfigureServices_SourceBytesCachePolicy_DisabledWhenSettingOff()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var settingsStore = provider.GetRequiredService<SettingsStore>();
        settingsStore.Current.UseSourceBytesCache = false;

        var policy = provider.GetRequiredService<SourceBytesCachePolicy>();

        Assert.False(policy.Enabled);
        Assert.Null(policy.Cache);
    }

    [Fact]
    public void ConfigureServices_SourceBytesCachePolicy_EnabledSharesSameCacheAcrossConsumers()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var settingsStore = provider.GetRequiredService<SettingsStore>();
        settingsStore.Current.UseSourceBytesCache = true;

        var policy = provider.GetRequiredService<SourceBytesCachePolicy>();
        Assert.True(policy.Enabled);
        Assert.NotNull(policy.Cache);

        var fileHashService = provider.GetRequiredService<FileHashService>();
        var thumbnailCache = provider.GetRequiredService<ThumbnailCache>();
        var previewImageService = provider.GetRequiredService<PreviewImageService>();

        var fileHashCache = typeof(FileHashService)
            .GetField("_sourceBytesCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fileHashService);
        var thumbnailCacheField = typeof(ThumbnailCache)
            .GetField("_sourceBytesCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(thumbnailCache);
        var previewCacheField = typeof(PreviewImageService)
            .GetField("_sourceBytesCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(previewImageService);

        Assert.Same(policy.Cache, fileHashCache);
        Assert.Same(policy.Cache, thumbnailCacheField);
        Assert.Same(policy.Cache, previewCacheField);
    }

    [Fact]
    public void ConfigureServices_SingletonsReturnSameInstance()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var store1 = provider.GetRequiredService<SettingsStore>();
        var store2 = provider.GetRequiredService<SettingsStore>();
        Assert.Same(store1, store2);

        var preview1 = provider.GetRequiredService<PreviewImageService>();
        var preview2 = provider.GetRequiredService<PreviewImageService>();
        Assert.Same(preview1, preview2);

        var metrics1 = provider.GetRequiredService<ReviewMetrics>();
        var metrics2 = provider.GetRequiredService<ReviewMetrics>();
        Assert.Same(metrics1, metrics2);
    }

    [Fact]
    public void ConfigureServices_PreloadSchedulerFactory_ProducesValidScheduler()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<Func<Func<CatalogEntry[]>, Func<long>, PreloadScheduler>>();
        var scheduler = factory(() => [new CatalogEntry("image1.jpg")], () => 1024L);

        Assert.NotNull(scheduler);
        var probeField = typeof(PreloadScheduler).GetField("_memoryProbe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Same(provider.GetRequiredService<IMemoryProbe>(), probeField!.GetValue(scheduler));
        scheduler.Dispose();
    }

    [Fact]
    public void ConfigureServices_MainViewModel_UsesRealSchedulerWithRegisteredMemoryProbe()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var viewModel = provider.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>();
        var controller = Assert.IsType<PreloadControllerAdapter>(viewModel.PreloadController);
        var scheduler = GetScheduler(controller);
        var probeField = typeof(PreloadScheduler).GetField("_memoryProbe", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Same(provider.GetRequiredService<IMemoryProbe>(), probeField!.GetValue(scheduler));
        controller.Dispose();
    }

    [Fact]
    public void MainWindowClosed_DisposesProductionPreloadScheduler()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                    _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                try
                {
                    System.Windows.Application.ResourceAssembly = typeof(MainWindow).Assembly;
                }
                catch
                {
                    var appField = typeof(System.Windows.Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    appField?.SetValue(null, typeof(MainWindow).Assembly);
                }

                var services = new ServiceCollection();
                App.ConfigureServices(services);
                var provider = services.BuildServiceProvider();
                var window = provider.GetRequiredService<MainWindow>();
                var controller = Assert.IsType<PreloadControllerAdapter>(window.ViewModel.PreloadController);
                var scheduler = GetScheduler(controller);

                typeof(MainWindow).GetMethod("Window_Closed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, EventArgs.Empty]);

                var disposedField = typeof(PreloadScheduler).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True((bool)disposedField!.GetValue(scheduler)!);
                Assert.True(scheduler.PreloadAroundAsync(0).IsCompletedSuccessfully);
            }
            catch (Exception ex) { threadException = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA window-close test timed out.");
        Assert.Null(threadException);
    }

    private static PreloadScheduler GetScheduler(PreloadControllerAdapter controller)
    {
        var factoryField = typeof(PreloadControllerAdapter).GetField("_getScheduler", BindingFlags.Instance | BindingFlags.NonPublic);
        return ((Func<PreloadScheduler>)factoryField!.GetValue(controller)!)();
    }

    [Fact]
    public void ConfigureServices_ResolvesMainWindow_OnStaThread()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                }

                try
                {
                    System.Windows.Application.ResourceAssembly = typeof(MainWindow).Assembly;
                }
                catch
                {
                    var appField = typeof(System.Windows.Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    appField?.SetValue(null, typeof(MainWindow).Assembly);
                }

                var services = new ServiceCollection();
                App.ConfigureServices(services);
                using var provider = services.BuildServiceProvider();

                var window = provider.GetRequiredService<MainWindow>();
                Assert.NotNull(window);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));

        Assert.Null(threadException);
    }
}

