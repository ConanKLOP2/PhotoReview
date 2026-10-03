using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.App.Composition;

/// <summary>
/// Builds MainViewModel with all dependencies, breaking circular reference (VM↔sink) via null-initialization.
/// </summary>
internal static class MainViewModelCompositionRoot
{
    public static MainViewModel Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);

        var catalog = sp.GetRequiredService<ReviewCatalog>();
        // ADR 0005: Create runs on the UI thread; Debug builds then assert every catalog mutation
        // happens on it (no-op in Release, and unbound catalogs in unit tests stay unchecked).
        catalog.BindToCurrentThread();
        var clock = sp.GetRequiredService<GenerationClock>();
        var viewer = sp.GetRequiredService<ViewerState>();
        var compare = sp.GetRequiredService<CompareViewModel>();
        var settingsStore = sp.GetRequiredService<SettingsStore>();
        var sessionStore = sp.GetRequiredService<SessionStore>();
        var sessionWriter = sp.GetRequiredService<SessionWriter>();
        var fs = sp.GetRequiredService<IFileSystem>();
        var actions = sp.GetRequiredService<FileActionService>();
        var undo = sp.GetRequiredService<UndoService>();
        var dialog = sp.GetRequiredService<IDialogService>();
        var hash = sp.GetRequiredService<FileHashService>();
        var preview = sp.GetRequiredService<PreviewImageService>();
        var thumbs = sp.GetRequiredService<ThumbnailCache>();
        var natural = sp.GetRequiredService<INaturalComparer>();
        var explorerOrder = sp.GetRequiredService<IExplorerOrderProvider>();
        var schedulerFactory = sp.GetRequiredService<System.Func<System.Func<CatalogEntry[]>, System.Func<long>, System.Func<int>, PreloadScheduler>>();
        var viewport = sp.GetRequiredService<ViewportSizeSource>();
        var observer = sp.GetRequiredService<IPresentationObserver>();

        // AR02a step 5: production registers no IPreloadController override, so this always
        // falls through to the real PreloadScheduler below. A DI override (test-only) can
        // replace preloading entirely without a second composition root; in that case the
        // real scheduler is never created.
        var preloadController = sp.GetService<IPreloadController>();
        if (preloadController is null)
        {
            var sourceSizeTracker = new SourceSizeTracker(catalog, fs);
            var preloadScheduler = schedulerFactory(
                () =>
                {
                    // R2-F-11: taken on the UI thread; the tracker then sums this snapshot on the preload thread
                    // instead of enumerating the live catalog there.
                    var snapshot = catalog.EntriesSnapshot();
                    sourceSizeTracker.Observe(snapshot);
                    return snapshot;
                },
                sourceSizeTracker.GetTotal,
                () => catalog.StructuralVersion);
            preloadController = new PreloadControllerAdapter(() => preloadScheduler);
        }

        MainViewModel? vm = null;
        var sink = new WpfPresentationSink(
            onSetCurrentImage: (_, isFileChange) => vm?.NotifyCurrentImageChanged(isFileChange),
            onSetStatusText: _ => vm?.NotifyPresentationChanged(),
            // PR-B: default (no scroll placement) used until MainWindow wires WpfPresentationSink.ApplyInitialViewModeOverride
            // to PointerInputController.ApplyInitialViewAsync (which does Fit width/Fit height placement -- the pointer
            // controller owns the surface, built after the ViewModel/sink here). Tests that build a MainViewModel
            // without a MainWindow keep getting a correct zoom/mode change, just without the scroll placement.
            onApplyInitialViewMode: () =>
            {
                var (w, h) = viewport.Get();
                var current = settingsStore.Current;
                vm?.Viewer.ApplyInitialViewMode(current.InitialViewMode, w, h, current.ClickZoomPercent, current.KeepZoomAcrossImages);
            },
            onPresented: observer.OnPresented,
            metrics: sp.GetRequiredService<ReviewMetrics>());

        var presenter = new ImagePresenter(
            catalog, clock, preview, thumbs, preloadController, compare, hash,
            sp.GetRequiredService<ReviewMetrics>(),
            () => settingsStore.Current, sessionStore, sink, fs, getSession: () => vm?.Session,
            sessionWriter: sessionWriter, uiScheduler: sp.GetRequiredService<IUiScheduler>());

        var coordinator = new FolderLoadCoordinator(
            catalog, clock, explorerOrder, fs, sessionStore, settingsStore,
            new ForwardingFolderSink(() => vm ?? throw new InvalidOperationException("MainViewModel is not created yet.")), sessionWriter,
            sp.GetService<LatestExplorerSnapshot>());

        vm = new MainViewModel(
            catalog, clock, coordinator, presenter, viewer, compare, settingsStore, sessionStore,
            fs, actions, undo, dialog, hash, preview, thumbs, sessionWriter,
            preloadController: preloadController, naturalComparer: natural,
            metrics: sp.GetRequiredService<ReviewMetrics>(),
            uiScheduler: sp.GetRequiredService<IUiScheduler>(),
            folderPicker: sp.GetService<IFolderPicker>());

        return vm;
    }
}
