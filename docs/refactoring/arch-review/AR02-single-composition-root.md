# AR02 — One composition root for app, tests and benchmarks

**Findings:** F2, F3, F4, F6 · **Decision:** Q-AR3 · **Size:** 2–3 days in 5 PRs · **GUI/real machine:** AR02a (Fit check), AR02e (perf baseline)

## 1. Problem (verified at `fbdf48e`)

There are two ways to build `MainViewModel`:

| | Production | "Test" root |
|---|---|---|
| Entry | `App.App_Startup` → `App.ConfigureServices` → `MainViewModelCompositionRoot.Create` | `new MainWindow(...)` (4 non-DI ctors, `MainWindow.xaml.cs:71-78`) → `MainWindowHelpers.CreateTestViewModel` (`MainWindowHelpers.cs:113-166`) |
| Preload | `PreloadScheduler` via DI factory | `DummyPreloadController` → **no preload** |
| RAM cache | `settings.ImageCacheCapacityBytes` | 64 MiB |
| Target decode width | `PreviewStateContext.TargetDecodeWidth()` | constant 1920 |
| Decoder backend | `settings.DecoderBackend` via `PreviewStateContext` | default (Wpf) |
| Disk cache | on | off (`disableDiskCacheOverride: true`) |
| `SourceBytesCache` | per setting | never |
| `IFileSystem` | `CountingFileSystem(PhysicalFileSystem)` | `HookedFileSystem` (no StatCount) |
| `ApplyInitialViewMode` viewport | `(0, 0)` | real `GetViewportSize()` |
| `IUiScheduler` | `DispatcherUiScheduler` | `ImmediateUiScheduler` (default) |

Users of the test root: 12 call sites in `tests/PhotoReview.Integration.Tests` (`Infrastructure/StaTestHost.cs:251`, `MainWindowBehaviorTests.Actions.cs:94,181`, `.Explorer.cs:114,175`, `.Fit.cs:54,125,183,237,295`, `.FolderSwitch.cs:64,155`) **and** the benchmark CLI (`tools/PhotoReview.Benchmark.Cli/PerfSession.cs:210`, `LocalUiNextProbe.cs:17`).

Consequences:
- **F2** — `--perf-session`/`--ui-next-probe` never measured the shipped configuration (no preload, small cache). Their numbers (D06 input, parts of D12) describe a different program.
- **F3** — integration tests of Fit exercise a viewport path that production does not use; production relies on the later `UpdateFitSize` at `DispatcherPriority.Render` (`MainWindow.xaml.cs:97-99`).
- **F4** — `PreviewImageService` (`:88-89`) and `ThumbnailCache` (`:52-54`) hard-code `%LOCALAPPDATA%\PhotoReview\{cache,thumbnails}` although `IAppPaths.PreviewCacheDir/ThumbnailCacheDir` exist (`AppPaths.cs:46-47`, same default paths). `PHOTOREVIEW_DATA_ROOT` therefore does not isolate caches — which is why the test root had to switch the disk cache off.
- **F6** — `MainWindow` exposes 8 public fields (`:36-48`, `#pragma CA1051`) kept in sync by `WireViewModelEvents` (`:87-105`) and `SyncFiles` (`:131-135`). `Benchmark.Cli` reads `_files`, `_index`, `_compareSelectedPath`, `_metrics`, `_fileActionInProgress`; nobody reads `_moveHistory`, `_lastUndoAction`, `_preloadScheduler`.

## 2. Target

```text
AppHost.BuildServices(Action<IServiceCollection>? overrides = null)
   = new ServiceCollection() → App.ConfigureServices(s) → overrides?.Invoke(s) → BuildServiceProvider()
App.App_Startup           → AppHost.BuildServices()                → MainWindow (DI ctor)
Benchmark.Cli             → AppHost.BuildServices()                → MainWindow (DI ctor)   // production graph
Integration tests         → AppHost.BuildServices(testOverrides)   → MainWindow (DI ctor)
```
`MainWindow` has exactly one constructor. Test-specific behaviour is expressed only as DI overrides.

## 3. PR split

### AR02a — production seams and fixes (no test migration yet) · branch `refactor/ar02a-apphost`

1. **`AppHost`** — new `src/PhotoReview.App/Composition/AppHost.cs`, `public static class AppHost { public static ServiceProvider BuildServices(Action<IServiceCollection>? overrides = null) }`. `App.App_Startup` (`App.xaml.cs:151-153`) uses it. Public (not internal) because `Benchmark.Cli` has no `InternalsVisibleTo` and Q-ST3 already accepted Cli→App.
2. **Cache directories from `IAppPaths` (F4)** — in `App.ConfigureServices`:
   - `ThumbnailCache(diskDirectory: paths.ThumbnailCacheDir, …)`
   - `PreviewImageService(…, diskCacheDirectory: paths.PreviewCacheDir, …)` (the ctor already has `diskCacheDirectory`).
   - Unit test in `App.Tests/CompositionRootTests`: with `PHOTOREVIEW_DATA_ROOT` set (use `DataRootFixture`), resolved `ThumbnailCache.DiskDirectory` starts with the data root. Test that default `AppPaths` values equal the old hard-coded paths (no orphaned user cache).
3. **Viewport provider (F3)** — new `src/PhotoReview.App/Services/ViewportSizeSource.cs`: `public sealed class ViewportSizeSource { public Func<(double Width, double Height)> Get { get; set; } = static () => (0, 0); }`, registered singleton.
   - `MainViewModelCompositionRoot`: `onApplyInitialViewMode: () => { var (w, h) = viewport.Get(); vm?.Viewer.ApplyInitialViewMode(settingsStore.Current.InitialViewMode, w, h); }`.
   - `MainWindow` DI ctor gets `ViewportSizeSource` and sets `viewport.Get = GetViewportSize;` right after `InitializeComponent()`; update registration "8. Window" in `App.xaml.cs`.
   - **GUI check (with T89):** InitialViewMode=Fit, open folder with landscape/portrait/small images, resize window, toggle fullscreen; image must fit on the first frame (no visible jump). Keep `ApplyFitViewAsync` two-pass (T89 rule).
4. **Presentation observer** — new `IPresentationObserver { void OnPresented(string path); }` + `NullPresentationObserver`, registered singleton; composition root passes `onPresented: observer.OnPresented` to `WpfPresentationSink`. (Replaces `MainWindowTestHooks.OnPresented`.)
5. **Preload override seam** — `MainViewModelCompositionRoot`: `var preloadController = sp.GetService<IPreloadController>() ?? new PreloadControllerAdapter(() => preloadScheduler);` and create `preloadScheduler` only in the second branch. Production registers no `IPreloadController` → unchanged.
6. **Move override seam** — `FileActionService`/`UndoService` already take a move override (see `MainWindowHelpers.cs:134-135`). Register them through factories that read an optional `IMoveOverride` service (`Func<string,string,Task>`), `null` in production.
7. Verify: build, full test run, `CompositionRootTests` extended to resolve every new service.

### AR02b — migrate integration tests · branch `refactor/ar02b-tests-apphost`

1. New `tests/PhotoReview.Integration.Tests/Infrastructure/TestAppHost.cs`:
   ```csharp
   internal static MainWindow CreateMainWindow(string? initialPath, MainWindowTestHooks? hooks = null)
   {
       var sp = AppHost.BuildServices(s =>
       {
           if (hooks?.Explorer is { } e) s.AddSingleton(e);
           if (hooks?.RecycleBin is { } r) s.AddSingleton(r);
           if (hooks?.OnPresented is { } p) s.AddSingleton<IPresentationObserver>(new DelegatePresentationObserver(p));
           if (hooks?.MoveOverride is { } m) s.AddSingleton<IMoveOverride>(new DelegateMoveOverride(m));
           if (hooks?.DisablePreload == true) s.AddSingleton<IPreloadController>(new NoOpPreloadController());
       });
       var w = sp.GetRequiredService<MainWindow>();
       w.InitializeWithInitialPath(initialPath);
       return w;
   }
   ```
   Must be called on the STA thread (`StaTestHost.RunAsync`) so `DispatcherUiScheduler` binds to that thread's dispatcher.
2. Move `MainWindowTestHooks` from `src/PhotoReview.App/MainWindowTestHooks.cs` into the test project (it becomes plain test data); add `DisablePreload` (default **false** — tests exercise the real graph; set true only for tests that assert exact read counts and are destabilised by background preload).
3. Replace the 12 `new MainWindow(folder, hooks)` sites with `TestAppHost.CreateMainWindow(folder, hooks)`.
4. Every test already runs under `DataRootFixture` in the `GlobalState` collection → config/journal/session/caches are isolated once AR02a step 2 is in. Remove any leftover reliance on disk cache being off; if a test needs it, write it to the fixture's `config.json`.
5. Dispose the `ServiceProvider` when the window closes (`window.Closed += (_, _) => sp.Dispose()`), which also exercises `MainWindowClosed_DisposesProductionPreloadScheduler` semantics.
6. Run `Integration.Tests` 3× (flake check). Compare durations with `verify-all.ps1 -TestReport` before/after; > 20 % slower → investigate preload contention and use `DisablePreload` only where justified (comment why).

### AR02c — migrate Benchmark.Cli · branch `refactor/ar02c-cli-apphost`

1. `PerfSession.cs:210` and `LocalUiNextProbe.cs:17`: `using var sp = AppHost.BuildServices(); var window = sp.GetRequiredService<MainWindow>();` then set `Width/Height/Left/Top/WindowState/ShowActivated` as today. Must stay inside `WpfTestHost.RunAsync`.
2. `--mode` override (`PerfSession.cs:222`): mutates `window.Settings.LoadingMode`. After AR02d `window.Settings` returns `settingsStore.Current` (same object the presenter reads through `() => settingsStore.Current`) → still in-memory only. Add an assertion in PerfSession that `ReferenceEquals(window.Settings, sp.GetRequiredService<SettingsStore>().Current)`.
3. Write the effective configuration into the session report (`PerfSession.cs:~405`, next to `appVersion`): `ImageCacheCapacityBytes`, `PreloadWorkerCount`, `DecoderBackend`, `UseSourceBytesCache`, `LoadingMode`, and `graph = "production"`. This makes F2 impossible to repeat silently.
4. Field accesses → public properties added in AR02d (do AR02d's property step first inside this PR, keep the old fields until AR02d deletes them): `_files`→`Files`, `_index`→`CurrentIndex`, `_compareSelectedPath`→`CompareSelectedPath`, `_metrics`→`Metrics`, `_fileActionInProgress == 0`→`!IsFileActionInProgress`.

### AR02e — re-baseline (real machine, user) · no code

On the user's 32 GB machine, same fixture as T66/D06, same viewport 1920×1080, cold and warm cache:
```powershell
dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release -- --perf-session <scenario args as in D06>
dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release -- --perf-analyze <out-dir>
```
Record P50/P95 `key → present`, hit rate, peak working set in `docs/refactoring/PERF-STATUS.md` under "Baseline AR02e (production graph)". Mark older harness numbers as "legacy graph (no preload)". This baseline is the gate for AR04.

### AR02d — delete the second root · branch `refactor/ar02d-mainwindow-cleanup`

1. `MainWindow.xaml.cs`:
   - Delete ctors at `:71-78` and `InitializeWithInitialPath` call inside them; keep the DI ctor (drop `[ActivatorUtilitiesConstructor]` — only one ctor remains).
   - Replace fields `:40-48` with properties: `public IReadOnlyList<string> Files => _viewModel.Catalog.Paths;` `public int CurrentIndex => _viewModel.CurrentIndex;` `public string? CompareSelectedPath => _viewModel.Compare.SelectedPath;` `public ReviewMetrics Metrics => _viewModel.Metrics;` `public bool IsFileActionInProgress => Volatile.Read(ref _fileActionInProgress) != 0;`
   - `_fileActionInProgress` becomes `private int`; **all** `Interlocked.Exchange`/`Volatile.Write` sites (`:114-116, 329-342, 423-425`) unchanged — OC14 semantics untouched.
   - Delete `_moveHistory`, `_lastUndoAction`, `_preloadScheduler`, `SyncFiles`, the field-sync lines in `WireViewModelEvents`, the `#pragma warning disable/restore CA1051`.
   - Ctor: `_settings = _settingsStore.Current;` instead of `_settingsStore.Load()` (App_Startup already loaded config → one disk read less at startup; AGENTS priority 1).
   - `UndoLastActionAsync`: keep (used by tests), remove the `_lastUndoAction` write.
2. `MainWindowHelpers.cs`: delete `CreateTestViewModel`, `DummyPreloadController`; move `HookedFileSystem` to the test project (if still needed) — `ForwardingFolderSink` stays (production uses it).
3. Architecture tests (`tests/PhotoReview.Architecture.Tests/AppCompositionTests.cs`, new):
   - `MainWindow_HasSingleConstructor`.
   - `AppAssembly_HasNoPublicInstanceFields` (reflection over exported types of `PhotoReview.App`; XAML-generated `internal` fields are not exported).
   - `NoCallToMainViewModelCtorOutsideCompositionRoot` (source scan for `new MainViewModel(` outside `Composition/`).
4. Docs: AGENTS.md "Coding Conventions" rule 1 — remove the `MainWindow` exception sentence; `STRUCTURE-OPTIMIZE-STATUS.md` ST06 row: "superseded by AR02d (read-only properties)"; `docs/architecture.md` composition section.

## 4. Verification per PR

```powershell
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
.\tools\verify-all.ps1
```
Plus: AR02a GUI Fit check; AR02c `--perf-session` runs to completion and the report shows `graph=production`; AR02e baseline recorded.

## 5. Acceptance criteria
- `grep -rn "new MainWindow(" src tools tests` → only `AppHost`-based factory usage (0 direct `new MainWindow(` outside DI).
- `CreateTestViewModel`, `DummyPreloadController`, CA1051 pragma: gone.
- Setting `PHOTOREVIEW_DATA_ROOT` moves preview and thumbnail disk caches.
- Perf report carries its effective configuration; new baseline documented.

## 6. Risks
- **Test time/flakiness** from real preload in integration tests → `DisablePreload` hook, measured, per-test justification.
- **T89 interplay** — viewport change touches Fit. Do AR02a step 3 as its own commit so it can be reverted alone.
- **STA/dispatcher binding** — `IUiScheduler` factory captures `Current?.Dispatcher ?? Dispatcher.CurrentDispatcher`; resolve the window on the STA thread only (documented in `TestAppHost`).
- Rollback: each PR is independent after AR02a; AR02d is the only deleting PR and comes last.
