using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;
using Xunit;

namespace PhotoReview.App.Tests;

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
        Assert.NotNull(provider.GetRequiredService<IProgressiveExplorerOrderProvider>());
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
        Assert.NotNull(provider.GetRequiredService<Func<Func<string[]>, Func<long>, PreloadScheduler>>());
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

        var factory = provider.GetRequiredService<Func<Func<string[]>, Func<long>, PreloadScheduler>>();
        var scheduler = factory(() => ["image1.jpg"], () => 1024L);

        Assert.NotNull(scheduler);
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
