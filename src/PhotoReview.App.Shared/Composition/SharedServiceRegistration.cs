using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.App.Composition;

/// <summary>
/// C-16 (NO-WPF-EXEC-PLAN mục 5, WP-09): the WPF-free part of the production service graph, shared by the WPF app and the Win32 shell.
/// </summary>
public static class SharedServiceRegistration
{
    /// <summary>
    /// Registers everything that does not depend on WPF: paths, file system, settings, session, journal, file actions, recycle bin,
    /// decoders, caches, preload, localization, view models. The host (App WPF, Shell Win32) registers, before or after this call,
    /// the services it must supply itself, which are resolved lazily and fail loudly when missing:
    /// <see cref="IUiScheduler"/>, <see cref="IDialogService"/>, <see cref="IFolderPicker"/>, <see cref="IClipboardService"/> and
    /// <see cref="IPresentationSinkFactory"/>.
    /// </summary>
    public static IServiceCollection AddPhotoReviewShared(this IServiceCollection services, SharedServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Codec);
        ArgumentNullException.ThrowIfNull(options.ThumbnailDecoder);
        var codec = options.Codec;

        // 1. Core Abstractions
        services.AddSingleton<IAppPaths>(_ => PhotoReview.Core.AppPaths.FromEnvironment());
        // Metadata queries are counted into ReviewMetrics (StatCount) for the diagnostics/benchmark reports.
        services.AddSingleton<IFileSystem>(sp => new CountingFileSystem(new PhysicalFileSystem(), sp.GetRequiredService<ReviewMetrics>()));
        // Q-R29 option C-2: the one choke point every source-image byte read (SourceBytesCache, the
        // decoders, PreviewImageService's diag pre-read) goes through. Production default is a pure
        // pass-through, byte-for-byte the direct FileStream each of those opened before this seam
        // existed -- only tools/PhotoReview.Benchmark.Cli overrides it (with a throttling decorator)
        // for perf-session measurement, never a production default.
        services.AddSingleton<ISourceReader>(_ => PhysicalSourceReader.Instance);
        services.AddSingleton<IClock, SystemClock>();
        // AR02c/AR02b: FileLog.Default is a process-wide static singleton whose Shutdown/Dispose
        // lifecycle is owned by the process (App.Dispose / PerfSession's own AppLog.Shutdown()), not by
        // any one composition-root's ServiceProvider. A container that disposes itself (Benchmark.Cli
        // building/disposing a fresh graph per iteration, and AR02b's per-window ServiceProvider) must
        // not also dispose this shared instance -- registering the already-constructed instance (instead
        // of a factory returning it) opts it out of container-owned disposal.
        services.AddSingleton<ILog>(FileLog.Default);

        // 2. Settings & Session
        services.AddSingleton<SettingsStore>(sp => new SettingsStore(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<ILog>(),
            AppLog.ErrorForced,
            new WpfKeyNameValidator())); // AR11a: was the static AppSettings.Validator, now an instance dependency
        services.AddSingleton<SessionStore>(sp => new SessionStore(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<ReviewMetrics>()));
        services.AddSingleton<SessionWriter>(sp => new SessionWriter(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<ILog>()));

        // I18N (ADR 0006): a plain file system on purpose -- catalog reads must not count in ReviewMetrics read budgets.
        services.AddSingleton<PhotoReview.App.Localization.LocalizationService>(sp => new PhotoReview.App.Localization.LocalizationService(
            sp.GetRequiredService<IAppPaths>(),
            new PhysicalFileSystem(),
            sp.GetRequiredService<ILog>()));

        // 3. Journal & File Actions
        services.AddSingleton<OperationJournal>(sp => new OperationJournal(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            () => sp.GetRequiredService<SettingsStore>().Current.JournalDurability,
            liveOperations: sp.GetRequiredService<ILiveOperationRegistry>(),
            compactionFiles: new PhysicalJournalCompactionFiles()));
        services.AddSingleton<RecoveryRetryService>(sp => ServiceFactories.CreateRecoveryRetryService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IRecycleBin>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<FileActionService>()));
        services.AddSingleton<FileHashService>(sp => new FileHashService(
            sp.GetRequiredService<SourceBytesCachePolicy>().Cache));
        services.AddSingleton<FileActionService>(sp => new FileActionService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IRecycleBin>(),
            moveOverride: GetMoveOverride(sp)));
        services.AddSingleton<UndoService>(sp => new UndoService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IRecycleBin>(),
            sp.GetRequiredService<FileActionService>(),
            moveOverride: GetMoveOverride(sp)));

        // 4. Diagnostics & Metrics
        services.AddSingleton<ReviewMetrics>();
        services.AddSingleton<LatestExplorerSnapshot>();

        // 5. Platform Services (Win32 only, no WPF)
        services.AddSingleton<IExplorerOrderProvider, ExplorerOrderService>();
        services.AddSingleton<IRecycleBin>(_ => WindowsRecycleBin.Instance);
        // Q-R27: named per-operation markers so another PhotoReview's startup reconcile skips operations still running here.
        services.AddSingleton<ILiveOperationRegistry>(sp => new WindowsLiveOperationRegistry(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IMemoryProbe>(sp => new WindowsMemoryProbe(sp.GetRequiredService<ILog>()));
        services.AddSingleton<INaturalComparer>(_ => WindowsNaturalComparer.Instance);
        services.AddSingleton<IDisplayClock>(_ => WindowsDisplayClock.Instance);
        services.AddSingleton<IKeyNameValidator, WpfKeyNameValidator>();
        services.AddSingleton<ViewportSizeSource>();
        services.AddSingleton<IPresentationObserver>(_ => NullPresentationObserver.Instance);

        // 6. Imaging & Decoding
        services.AddSingleton<IImageDecoderFactory>(sp =>
        {
            var sourceReader = sp.GetRequiredService<ISourceReader>();
            var settingsStore = sp.GetRequiredService<SettingsStore>();
            var sourceBytesCache = sp.GetRequiredService<SourceBytesCachePolicy>().Cache;
            var log = sp.GetService<ILog>();
            var metrics = sp.GetService<ReviewMetrics>();
            Func<IImageDecoder>? wpfDecoder = options.WpfFallbackDecoder is { } wpfFactory ? () => wpfFactory(sp) : null;
            // Q-FMT-WEBP-HEIC: WebP/HEIC always decode through WIC (WicDirect + the usual WPF fallback), whatever backend is picked.
            var webpHeicDecoder = ServiceFactories.CreateWebpHeicDecoder(sourceReader, codec, wpfDecoder?.Invoke(), log, metrics);
            return new ImageDecoderFactory(
                DecoderProviders.Create(sourceReader, codec, wpfDecoder,
                    () => settingsStore.Current.RawSupportEnabled || settingsStore.Current.DecoderBackend == DecoderBackend.LibRaw),
                log,
                metrics,
                (_, standardDecoder) => new FormatRoutingDecoder(
                    new WebpHeicRoutingDecoder(standardDecoder, webpHeicDecoder,
                        () => settingsStore.Current.WebpHeicSupportEnabled, () => WicCodecAvailability.Current, log),
                    ServiceFactories.CreateRawDecoder(standardDecoder, sourceReader, sourceBytesCache, codec),
                    () => settingsStore.Current.RawSupportEnabled),
                wpfDecoder);
        });
        services.AddSingleton<ThumbnailCache>(sp => new ThumbnailCache(
            codec,
            diskDirectory: sp.GetRequiredService<IAppPaths>().ThumbnailCacheDir,
            log: sp.GetService<ILog>()));
        services.AddSingleton<SourceBytesCachePolicy>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>().Current;
            if (!settings.UseSourceBytesCache) return new SourceBytesCachePolicy(null);
            // RAM%: the source-bytes cache must leave the preload window to the preview cache inside the user's share.
            var sourceBytes = RamBudgetPolicy.SourceBytesForPercent(
                settings.SourceBytesCapacityBytes, settings.ImageCacheRamPercent, RamBudgetPolicy.GetPhysicalMemoryBytes(),
                PreloadWindow.FromSettings(settings));
            if (sourceBytes <= 0)
            {
                sp.GetService<ILog>()?.Warn("Source-bytes cache disabled: the RAM cache share leaves no room beyond the preview preload window.");
                return new SourceBytesCachePolicy(null);
            }
            return new SourceBytesCachePolicy(new SourceBytesCache(sourceBytes, sp.GetRequiredService<ISourceReader>()));
        });

        services.AddSingleton<PreviewStateContext>();
        services.AddSingleton<PreviewImageService>(sp =>
        {
            var ctx = sp.GetRequiredService<PreviewStateContext>();
            var settingsStore = sp.GetRequiredService<SettingsStore>();
            // Also lost in T46d: without this the "Original" loading mode still decoded previews.
            ctx.IsOriginalLoadingMode = () => settingsStore.Current.LoadingMode == LoadingMode.Original;
            ctx.CurrentBackend = () => settingsStore.Current.DecoderBackend;
            // T46d dropped the viewport-based decode width, so Preview decoded every image at full size.
            // perf(decode): the target is now a width x height box (see AdaptivePreviewPolicy).
            var viewport = sp.GetRequiredService<ViewportSizeSource>();
            ctx.TargetDecodeBox = () => viewport.TargetDecodeBox;
            return new PreviewImageService(
                sp.GetRequiredService<ReviewMetrics>(),
                () => ctx.IsOriginalLoadingMode(),
                () => ctx.TargetDecodeBox(),
                codec,
                capacityBytes: settingsStore.Current.ImageCacheCapacityBytes,
                diskCacheDirectory: sp.GetRequiredService<IAppPaths>().PreviewCacheDir,
                decoderFactory: sp.GetRequiredService<IImageDecoderFactory>(),
                currentBackend: () => ctx.CurrentBackend(),
                log: sp.GetService<ILog>(),
                sourceBytesCache: sp.GetRequiredService<SourceBytesCachePolicy>().Cache,
                cacheRamPercent: settingsStore.Current.ImageCacheRamPercent,
                // feat/preload-window-setting: captured once (applies after restart, like PreloadWorkerCount/Q-AR6/Q-R19);
                // only affects the "allowed X-90%" text logged when the requested percent is clamped.
                preloadWindow: PreloadWindow.FromSettings(settingsStore.Current),
                sourceReader: sp.GetRequiredService<ISourceReader>(),
                rawFullDecoder: LibRawAvailability.Probe(out _) ? new LibRawDecoder(codec) : null,
                isRawFullDecodeEnabled: () => settingsStore.Current.RawSupportEnabled
                    && settingsStore.Current.RawFullDecode == RawFullDecode.OnZoom);
        });

        // RV-I13: getSnapshotVersion (ReviewCatalog.StructuralVersion) lets a running preload lifetime notice a catalog
        // change that did not go through Cancel() and restart on the new snapshot.
        services.AddSingleton<Func<Func<CatalogEntry[]>, Func<int>, PreloadScheduler>>(sp =>
            (getEntries, getSnapshotVersion) =>
            {
                var settingsStore = sp.GetRequiredService<SettingsStore>();
                var sourceBytesCache = sp.GetRequiredService<SourceBytesCachePolicy>().Cache;
                var previewService = sp.GetRequiredService<PreviewImageService>();
                return new PreloadScheduler(
                previewService,
                sp.GetRequiredService<ReviewMetrics>(),
                getEntries,
                fullFolderRamThresholdBytes: previewService.CapacityBytes, // effective (clamped) budget, R2-A-05
                memoryLoadLimit: settingsStore.Current.PreloadMemoryLoadLimit,
                memoryProbe: sp.GetRequiredService<IMemoryProbe>(),
                workerCountOverride: settingsStore.Current.PreloadWorkerCount,
                log: sp.GetService<ILog>(),
                prefetchSourceBytes: sourceBytesCache is not null
                    ? ServiceFactories.CreateSourcePrefetch(sourceBytesCache,
                        () => settingsStore.Current.WebpHeicSupportEnabled, () => WicCodecAvailability.Current)
                    : null,
                // feat/preload-window-setting: captured once at composition (applies after restart, Q-AR6/Q-R19).
                window: PreloadWindow.FromSettings(settingsStore.Current),
                snapshotVersion: getSnapshotVersion);
            });

        // 7. ViewModels & Coordinators
        services.AddTransient<ViewerState>();
        services.AddTransient<CompareViewModel>();
        services.AddTransient<ReviewCatalog>();
        services.AddTransient<GenerationClock>();
        services.AddTransient<MainViewModel>(sp => MainViewModelCompositionRoot.Create(sp));

        return services;
    }

    /// <summary>
    /// AR02a step 6 (F-move-seam): production registers no <see cref="IMoveOverride"/>,
    /// so <see cref="FileActionService"/>/<see cref="UndoService"/> get a <c>null</c> override and move files for
    /// real. A DI override (test-only, from AR02b onward) can register one to intercept the move step.
    /// </summary>
    private static Func<string, string, Task>? GetMoveOverride(IServiceProvider sp)
    {
        var moveOverride = sp.GetService<IMoveOverride>();
        return moveOverride is null ? null : moveOverride.MoveAsync;
    }
}
