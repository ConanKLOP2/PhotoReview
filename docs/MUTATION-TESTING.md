# Mutation testing (Stryker.NET)

How to measure how well the tests pin behaviour, and the 2026-10-03 baseline. Mutation score = (killed + timeout) / (killed + timeout + survived + no-coverage); a survivor is either an *equivalent* mutant (no observable change) or a *real gap* (a test should have caught it).

## Run it

Local tool `dotnet-stryker` (see `dotnet-tools.json`), shared config `stryker-config.json` (Release build, Basic level, string/regex mutations and logging ignored, **test filter `Category!=Manual&Category!=Native&Category!=Slow`**).

```powershell
dotnet tool restore
cd tests\PhotoReview.Core.Tests    # or tests\PhotoReview.Imaging.Tests
dotnet tool run dotnet-stryker -- --config-file ..\..\stryker-config.json --project PhotoReview.Core.csproj --concurrency 2 --mutate "**/FileActions/Journal*.cs" --mutate "**/FileActions/Recovery*.cs" --output $env:TEMP\stryker-core
```

Rules:

- **One `--mutate` flag per glob** (`--mutate A --mutate "!B"`); several globs after a single flag fail.
- **Never drop the test filter.** `Native` tests touch the real Recycle Bin; mutation runs of that code are forbidden ([AGENTS.md](../AGENTS.md)).
- **Release, not Debug.** A Debug build makes `SourceBytesCache.GetOrRead`'s `Debug.Fail` fail ~25 tests in the initial run and skews the score.
- Mutate one area at a time with `--mutate` and `--concurrency 2` (12 logical cores shared by parallel runs; load makes timing tests flaky, which Stryker counts as kills). Basic level took 20-65 min per area; the whole of Core + Imaging at Standard level would take ~10-16 h.
- `native\x64\*.dll` must exist in the worktree (`tools/fetch-native.ps1`).
- Reports (json/html) go under `--output`; `StrykerOutput/` is git-ignored.

## Scores 2026-10-03 (master `20011b45`, Basic level)

"Before" is the first measurement; "after" is the re-run once the mutation-gap tests (PR "test: kill surviving mutants") were added. Four ACL tests that deny `ListDirectory` and the two `mklink` junction tests are `Category=Native` (repo convention: a killed host could leave a Deny ACE behind), so they are not counted by the filter above.

| Area | Mutate glob (project) | Before | After | Weakest files after |
|---|---|---|---|---|
| Core: journal + recovery | `FileActions/Journal*`, `Recovery*`, `OperationJournal` (Core) | 81.7% | 94.8% | RecoveryFileCheck 89.7% |
| Core: settings, session, catalog, IO, instance | `Settings`, `Session`, `Catalog`, `IO`, `Instance` (Core) | 78.7% | 93.0% | PhysicalJournalCompactionFiles 76.5%, SessionStore 80% |
| Core: other file actions | `FileActions/*` minus the above (Core) | 73.2% | ~97% | DuplicateFinder 81% |
| Imaging: metadata | `Metadata/*` (Imaging) | 71.0% | 90.2% | ExifParser 82.9% |
| Imaging.Raw | whole project | 61.7% | 86.6% | Cr3ContainerReader 80%, BmffBoxNavigator 80%, RawDecoder 66% (not touched) |
| Imaging: preload + caching | `Preload/*`, `Caching/*` (Imaging) | 61.5% | ~85% | PreviewImageService 75.5%, PreloadScheduler 82% |
| Imaging: decoding | `Decoding/**` (Imaging) | 56.6% | 78.7% | EmbeddedThumbnailReader 67%, WicDirectDecoder 72%, WpfBitmapImageDecoder 73% |
| Imaging: TurboJpeg | whole project | 68.9% | ~77% | SafeTurboJpegHandle 20%, TurboJpegAvailability 33% |

Reading: crash/corrupt-file handling was already well covered (review rounds + fuzz tests); the survivors were boundaries, chosen values (size, orientation, IFD/preview choice), cache invalidation and ordering, and the new tests pin those. One real bug surfaced: `ExifQueryInterpreter.AsRational` never reached its `long[]` arm because the CLR treats `long[]` as `ulong[]` in type tests (fixed; only negative SRATIONAL components behave differently, none are read today; the same quirk exists in `AsInteger`'s `short[]`/`ushort[]` and `int[]`/`uint[]` arms and is left as is). App (WPF), LibRaw, `RawDecoder` (66%), `RawJpegIccProfile` and `SourceRawHeaderSource` were not worked on. The ~ values are estimates from partial re-runs.

## Survivors that need a production seam (not testable today)

- `DiskCacheStore` ~171-172: a prune requested between the last loop check and clearing `_pruneScheduled`; `PreviewImageService` ~280: a persist going stale mid-write; `PreloadScheduler` ~808 (late-drain dispose) and the `PHOTOREVIEW_DIAG_PRELOAD_WORKERS` env var branches (process-wide).
- `TurboJpegAvailability` probe failure/OOM paths, `TurboJpegNative` `tj3Init` failure, `WicDirectDecoder` `CreateFactory` failure, `SafeTurboJpegHandle` release: need an injectable loader/factory.
- `PhysicalFileSystem` ~259: an `IOException` part-way through a real directory listing.
- `OperationJournal` ~249: durable `FileStream.Flush(true)` only runs on a real `FileStream` (Native/Slow).

Most other survivors are equivalent (buffer sizes, log text, empty-catch bodies that return the default, `<` vs `<=` where a later check rejects the value anyway). Treat a "real gap" classification as a hypothesis until a test kills the mutant.