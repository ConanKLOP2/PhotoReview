using System;
using System.Threading;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
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

        // 6. Imaging & Decoding
        Assert.NotNull(provider.GetRequiredService<IImageDecoderFactory>());
        Assert.NotNull(provider.GetRequiredService<ThumbnailCache>());
        Assert.NotNull(provider.GetRequiredService<PreviewStateContext>());
        Assert.NotNull(provider.GetRequiredService<PreviewImageService>());
        Assert.NotNull(provider.GetRequiredService<Func<Func<CatalogEntry[]>, Func<long>, PreloadScheduler>>());
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

