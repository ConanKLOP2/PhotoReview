# Mutation testing (Stryker.NET)

How to measure how well the tests pin behaviour, and the 2026-10-03/04 results (PRs #268-#275). Mutation score = (killed + timeout) / (killed + timeout + survived + no-coverage); a survivor is either an *equivalent* mutant (no observable change) or a *real gap* (a test should have caught it).

## Run it

Local tool `dotnet-stryker` (see `dotnet-tools.json`), shared config `stryker-config.json` (Release build, Basic level, string/regex mutations and logging ignored, **test filter `Category!=Manual&Category!=Native&Category!=Slow`**).

```powershell
dotnet tool restore
cd tests\PhotoReview.Core.Tests    # or Imaging.Tests, Imaging.Raw.Tests, App.Tests (see the App section)
dotnet tool run dotnet-stryker -- --config-file ..\..\stryker-config.json --project PhotoReview.Core.csproj --concurrency 2 --mutate "**/FileActions/Journal*.cs" --mutate "**/FileActions/Recovery*.cs" --output $env:TEMP\stryker-core
```

Rules:

- **One `--mutate` flag per glob** (`--mutate A --mutate "!B"`); several globs after a single flag fail.
- **Never drop the test filter.** `Native` tests touch the real Recycle Bin; mutation runs of that code are forbidden ([AGENTS.md](../AGENTS.md)).
- **Release, not Debug.** A Debug build makes `SourceBytesCache.GetOrRead`'s `Debug.Fail` fail ~25 tests in the initial run and skews the score.
- Mutate one area at a time with `--mutate` and `--concurrency 2` (12 logical cores shared by parallel runs; load makes timing tests flaky, which Stryker counts as kills). Basic level took 20-65 min per area; the whole of Core + Imaging at Standard level would take ~10-16 h.
- `native\x64\*.dll` must exist in the worktree (`tools/fetch-native.ps1`).
- Reports (json/html) go under `--output`; `StrykerOutput/` is git-ignored.

## Scores 2026-10-03/04 (master `20011b45` -> `3ab763f9`, Basic level)

"Before" is the first measurement; "after" is the re-run once the gap tests (#269, #271-#275, ~1,700 tests in all) were added. Four ACL tests that deny `ListDirectory` and the two `mklink` junction tests are `Category=Native` (a killed host could leave a Deny ACE behind), so the filter above does not count them. Values with "about" are estimates from partial re-runs.

| Area | Mutate glob (project) | Before | After | Weakest files after |
|---|---|---|---|---|
| Core: journal + recovery | `FileActions/Journal*`, `Recovery*`, `OperationJournal` (Core) | 81.7% | 94.8% | RecoveryFileCheck 89.7% |
| Core: settings, session, catalog, IO, instance | `Settings`, `Session`, `Catalog`, `IO`, `Instance` (Core) | 78.7% | 93.0% | PhysicalJournalCompactionFiles 76.5%, SessionStore 80% |
| Core: other file actions | `FileActions/*` minus the above (Core) | 73.2% | about 97% | DuplicateFinder 81% |
| Imaging: metadata | `Metadata/*` | 71.0% | 90.2% | ExifParser 82.9% |
| Imaging.Raw: containers/readers | whole project | 61.7% | 86.6% | Cr3ContainerReader 80%, BmffBoxNavigator 80% |
| Imaging.Raw: RawDecoder, RawJpegIccProfile, SourceRawHeaderSource | those files (#273) | 66.2 / 77.0 / 91.1% | 95.4 / 91.8 / 95.6% | - |
| Imaging.LibRaw | whole project (#273) | 65.3% | 83.2% | LibRawAvailability 55.3%, SafeLibRawHandle 0% (unchanged) |
| Imaging: preload + caching | `Preload/*`, `Caching/*` | 61.5% | about 85% | PreviewImageService 72.3%, PreloadScheduler 85.7%, DiskCacheStore 91.5% (was 88.1%) |
| Imaging: decoding | `Decoding/**` | 56.6% | 78.7% | WicDirectDecoder 76%, EmbeddedThumbnailReader 67% |
| Imaging: TurboJpeg + small infra (#271) | whole project | 68.9% | about 77%; SafeTurboJpegHandle, TurboJpegAvailability, TurboJpegNative 100% after #271 | PhysicalFileSystem 90%, OperationJournal 94.3% |
| App: ViewModels, Services, Composition (#274) | those folders (App) | 65.3% | 83.8% | - |
| App: Coordinators, Input (#275) | those folders (App) | 80.0% | 90.2% | - |

Reading: crash/corrupt-file handling was already well covered (review rounds + fuzz tests); the survivors were boundaries, chosen values (size, orientation, IFD/preview choice), cache invalidation and ordering, and the new tests pin those. Production changes the work caused: `ExifQueryInterpreter.AsRational` never reached its `long[]` arm because the CLR treats `long[]` as `ulong[]` in type tests (fixed in #269; the same quirk in `AsInteger`'s `short[]`/`ushort[]` and `int[]`/`uint[]` arms is left as is, no negative values are read); `JournalTransaction.Commit` stamps the clock inside the `try`, so a throwing clock after a completed mutation is a journal failure (`Succeeded=true`, `JournalPersisted=false`, Prepared line stays) instead of a spurious Failed line (#272); Settings > Import uses the shared `SettingsStore.ParseText` pipeline (#270); five low-risk internal seams (#271, #272).

## App (WPF) and Stryker

Stryker cannot analyse `PhotoReview.App.csproj` as is ("simulated build failed": the design-time build fails with NETSDK1022, duplicate `Compile` items for `*.g.cs`, and the `Clean` target wipes `obj`, so the markup compile then fails with MC2000). Workaround, no repo change:

1. Build the App and App.Tests in Release first.
2. Set env `ContinueOnError=WarnAndContinue` and `CustomAfterMicrosoftCommonTargets=<path to a targets file containing exactly <Project><Target Name="Clean" /></Project>>`, then run stryker from `tests\PhotoReview.App.Tests`.
3. The App tests include `Category=UI` tests that open real WPF windows: run Stryker on the hidden desktop. Make a private copy (not committed) of `tools\run-tests-hidden.ps1` that reuses its CreateDesktop/CreateProcess code but launches `dotnet tool run dotnet-stryker -- ...` instead of `dotnet test`.
4. In PowerShell `-V` is swallowed: use `--verbosity info`.

Stryker only analyses the test project it is run from, so `Integration.Tests` are not part of the measurement.

## Remaining survivors that need a production seam or a Native test

- `WpfDialogService` (about 18 mutants: MessageBox, OpenFolderDialog, ShowDialog, owner): needs a dialog host seam. `WpfFolderPicker` likewise.
- `WpfImageSurface` `PointerPosition`, `DisplayTiming`, `ReleaseMouseCapture` (mouse, `PresentationSource`); `DispatcherUiScheduler` and `WpfPresentationSink` `Application.Current` branches.
- `FileActionService` is a concrete class that turns exceptions into results, so the unexpected-exception catches in `FileActionController` and `DuplicateCleanupController` cannot be provoked.
- `ZoomDetailLoader` cancel observation.
- `LibRawAvailability`, `LibRawDecoder.DecodeCore`/`ReadInfo`, `SafeLibRawHandle.ReleaseHandle`: need a real `libraw.dll` Native test or a loader seam.
- `PreloadScheduler` slot/CTS disposal after a late drain: no non-throwing probe, and probing would trip the process-wide `FirstChanceException` listener in `PreloadDisposeDrainGapTests`.
- `PhysicalFileSystem.ResolveIfReparsePoint` (Native junction only).

Done and removed from this list: TurboJpeg probe/init/handle, WIC factory failure, `PhysicalFileSystem` listing interruption, `OperationJournal` flush, `DiskCacheStore` prune window, `PreviewImageService` stale persist (seams in #271, #272).

Most other survivors are equivalent (buffer sizes, log text, empty-catch bodies that return the default, `<` vs `<=` where a later check rejects the value anyway). Treat a "real gap" classification as a hypothesis until a test kills the mutant.
