using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Integration.Tests.Infrastructure;

/// <summary>
/// AR02b: builds <see cref="MainWindow"/> for integration tests through the same
/// <see cref="AppHost.BuildServices"/> composition root production uses, layering the AR02a DI
/// seams (<see cref="IExplorerOrderProvider"/>, <see cref="IRecycleBin"/>,
/// <see cref="IPresentationObserver"/>, <see cref="IMoveOverride"/>, <see cref="IPreloadController"/>)
/// on top instead of building a second, non-DI view-model graph.
/// </summary>
/// <remarks>
/// Must be called on the STA thread (<see cref="StaTestHost.RunAsync(System.Func{Task})"/>) so the
/// <c>DispatcherUiScheduler</c> registered by <see cref="PhotoReview.App.App.ConfigureServices"/>
/// binds to that thread's dispatcher.
/// </remarks>
internal static class TestAppHost
{
    /// <summary>
    /// Fixed, unconditional title-bar label for every <see cref="MainWindow"/> this factory creates (see
    /// <see cref="PhotoReview.App.ViewModels.MainViewModel.InstanceLabel"/>): a Category=UI integration test
    /// spins up a real WPF window that can occasionally flash visibly on screen (e.g. an
    /// <c>Application</c>-singleton race), and this makes it unmistakable from the user's own everyday
    /// window even then -- no environment variable, no opt-in, so it can never be forgotten. Separate from
    /// (and set unconditionally after) the <c>PHOTOREVIEW_DIAG_INSTANCE_LABEL</c> seam production reads at
    /// startup, which stays purely opt-in there.
    /// </summary>
    internal const string TestInstanceLabel = "TEST";

    /// <summary>
    /// Builds the production service graph (with the requested test overrides), resolves
    /// <see cref="MainWindow"/> from it, and starts loading <paramref name="initialPath"/> if given.
    /// The window disposes its own <see cref="ServiceProvider"/> when closed.
    /// </summary>
    internal static MainWindow CreateMainWindow(string? initialPath, TestHostHooks? hooks = null, string? placementFile = null)
    {
        var sp = AppHost.BuildServices(services =>
        {
            // Factory registration, as production's type registration: the container disposes what it created, but
            // never an instance handed to AddSingleton(instance), so the window-close dispose would go unobserved.
            if (hooks?.Explorer is { } explorerOrder)
                services.AddSingleton<IExplorerOrderProvider>(_ => explorerOrder);
            // Never the real WindowsRecycleBin: a test that deletes must pass its own bin (TestHostHooks.RecycleBin).
            services.AddSingleton(hooks?.RecycleBin ?? ThrowingRecycleBin.Instance);
            if (hooks?.OnPresented is { } onPresented)
                services.AddSingleton<IPresentationObserver>(new DelegatePresentationObserver(onPresented));
            if (hooks?.MoveOverride is { } moveOverride)
                services.AddSingleton<IMoveOverride>(new DelegateMoveOverride(moveOverride));
            // A test that exercises window placement gets its own file; every other test is suppressed below.
            if (placementFile is not null)
                services.AddSingleton<PhotoReview.Core.Abstractions.IAppPaths>(_ => new PlacementFileOverride(PhotoReview.Core.AppPaths.FromEnvironment(), placementFile));
            if (hooks?.DisablePreload == true)
                services.AddSingleton<IPreloadController>(new NoOpPreloadController());
        });

        var window = sp.GetRequiredService<MainWindow>();
        // Unconditional, regardless of any environment state: this is the single production-composition-root
        // path every Category=UI integration test's MainWindow goes through, so tagging it here (rather than
        // via an env var a test could forget to set) is foolproof.
        window.ViewModel.InstanceLabel = TestInstanceLabel;
        // R7-11: never restore or overwrite the user's real window-placement.json from a test window.
        if (placementFile is null) window.SuppressWindowPlacement();
        window.Closed += (_, _) => sp.Dispose();
        window.InitializeWithInitialPath(initialPath);
        return window;
    }
}

/// <summary>
/// T14a test seam, migrated by AR02b (AR02-single-composition-root.md, AR02b step 2) from the
/// App project's now-deleted <c>MainWindowTestHooks.cs</c> (removed by AR02d once the last
/// non-DI <c>MainWindow</c> constructors and <c>MainWindowHelpers.CreateTestViewModel</c> were
/// deleted): plain data describing which AR02a DI seams a given test wants overridden, consumed
/// only by <see cref="TestAppHost.CreateMainWindow"/>.
/// </summary>
internal sealed class TestHostHooks
{
    /// <summary>Alternate Explorer order provider (INV-7, INV-9).</summary>
    public IExplorerOrderProvider? Explorer { get; init; }

    /// <summary>Alternate recycle-bin implementation.</summary>
    public IRecycleBin? RecycleBin { get; init; }

    /// <summary>Raised once an image has finished presenting on screen.</summary>
    public Action<string>? OnPresented { get; init; }

    /// <summary>Overrides the physical file-move step; (source, destination).</summary>
    public Func<string, string, Task>? MoveOverride { get; init; }

    /// <summary>
    /// AR02b: tests now run the real production preload graph by default (no override
    /// registered). Set true only for a test that asserts exact read/decode counts and is
    /// destabilised by background preload racing those counts; explain why at the call site.
    /// </summary>
    public bool DisablePreload { get; init; }
}

internal sealed class DelegatePresentationObserver(Action<string> onPresented) : IPresentationObserver
{
    public void OnPresented(string path) => onPresented(path);
}

internal sealed class DelegateMoveOverride(Func<string, string, Task> moveAsync) : IMoveOverride
{
    public Task MoveAsync(string source, string destination) => moveAsync(source, destination);
}

/// <summary>
/// Default <see cref="IRecycleBin"/> of <see cref="TestAppHost.CreateMainWindow"/>: fails loudly instead of letting a test
/// send files to (or restore from) the user's real Recycle Bin.
/// </summary>
internal sealed class ThrowingRecycleBin : IRecycleBin
{
    public static readonly IRecycleBin Instance = new ThrowingRecycleBin();

    public void SendToRecycleBin(string path) =>
        throw new InvalidOperationException($"Test used the Recycle Bin without passing TestHostHooks.RecycleBin: {path}");

    public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) =>
        throw new InvalidOperationException($"Test used the Recycle Bin without passing TestHostHooks.RecycleBin: {originalPath}");
}

/// <summary>Used only when a test opts out of preload via <see cref="TestHostHooks.DisablePreload"/>.</summary>
internal sealed class NoOpPreloadController : IPreloadController
{
    public Task PreloadAroundAsync(int center) => Task.CompletedTask;
    public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
    public void Cancel() { }
    public void RemovePreloadedKeysForPath(string normalizedPath) { }
    public void ClearPreloadedKeys() { }
}

/// <summary>The environment's paths with only <c>WindowPlacementFile</c> redirected (it is not covered by the data-root override).</summary>
internal sealed class PlacementFileOverride(PhotoReview.Core.Abstractions.IAppPaths inner, string placementFile) : PhotoReview.Core.Abstractions.IAppPaths
{
    public string ConfigFile => inner.ConfigFile;
    public string JournalFile => inner.JournalFile;
    public string SessionsDir => inner.SessionsDir;
    public string LogFile => inner.LogFile;
    public string PreviewCacheDir => inner.PreviewCacheDir;
    public string ThumbnailCacheDir => inner.ThumbnailCacheDir;
    public string WindowPlacementFile => placementFile;
}
