# Mutation testing (Stryker.NET)

How to measure how well the tests pin behaviour, and the 2026-10-03 baseline. Mutation score = (killed + timeout) / (killed + timeout + survived + no-coverage); a survivor is either an *equivalent* mutant (no observable change) or a *real gap* (a test should have caught it).

## Run it

Local tool `dotnet-stryker` (see `dotnet-tools.json`), shared config `stryker-config.json` (Release build, Basic level, string/regex mutations and logging ignored, **test filter `Category!=Manual&Category!=Native&Category!=Slow`**).

```powershell
dotnet tool restore
cd tests\PhotoReview.Core.Tests    # or tests\PhotoReview.Imaging.Tests
dotnet tool run dotnet-stryker -- --config-file ..\..\stryker-config.json --project PhotoReview.Core.csproj --concurrency 2 --mutate "**/FileActions/Journal*.cs" --output $env:TEMP\stryker-core
```

Rules:

- **Never drop the test filter.** `Native` tests touch the real Recycle Bin; mutation runs of that code are forbidden ([AGENTS.md](../AGENTS.md)).
- **Release, not Debug.** A Debug build makes `SourceBytesCache.GetOrRead`'s `Debug.Fail` fail ~25 tests in the initial run and skews the score.
- Mutate one area at a time with `--mutate` and `--concurrency 2` (12 logical cores shared by parallel runs; load makes timing tests flaky, which Stryker counts as kills). Basic level took 20-65 min per area; the whole of Core + Imaging at Standard level would take ~10-16 h.
- `native\x64\*.dll` must exist in the worktree (`tools/fetch-native.ps1`).
- Reports (json/html) go under `--output`; `StrykerOutput/` is git-ignored.

## Baseline 2026-10-03 (master `d19359dd`)

| Area | Mutate glob (project) | Score | Weakest files |
|---|---|---|---|
| Core: journal + recovery | `FileActions/Journal*`, `Recovery*`, `OperationJournal` (Core) | 81.7% | JournalStartupRecovery 60% |
| Core: settings, session, catalog, IO, instance | `Settings`, `Session`, `Catalog`, `IO`, `Instance` (Core) | 78.7% | ImageFileTypes 33%, ForwardedPathProtocol 61%, SessionWriter 63% |
| Core: other file actions | `FileActions/*` minus the above (Core) | 73.2% | DuplicateFinder 67%, FileActionService 70% |
| Imaging: metadata | `Metadata/*` (Imaging) | 71.0% | ExifQueryInterpreter 17% |
| Imaging: TurboJpeg | whole project | 68.9% | SafeTurboJpegHandle 20% |
| Imaging.Raw | whole project | 61.7% | RawExif 40%, container readers 54-62% |
| Imaging: preload + caching | `Preload/*`, `Caching/*` (Imaging) | 61.5% | PreviewCacheFile 50%, PreloadScheduler 51%, DiskCacheStore 52% |
| Imaging: decoding | `Decoding/**` (Imaging) | 56.6% | WpfBitmapImageDecoder 41%, WicDirectDecoder 45% |

Reading: crash/corrupt-file handling is well covered (review rounds + fuzz tests); the survivors are mostly boundaries, chosen values (size, orientation, IFD/preview choice), cache invalidation and ordering. App (WPF) and LibRaw were not measured. Classification of survivors in the reports was partly done from the mutated line only - treat "real gap" as a hypothesis until the test is written and kills the mutant.

## Highest-value gaps found (write a test, re-run Stryker on that file)

- `RecoveryRetryService` (~139-168, 224-239): permanent-delete guard when `allowPermanentDelete` is off; destination overwrite checks; post-move verification.
- `FileActionService` (~245-437, 630-708): rollback/compensation decisions, `JournalPersisted`, `PermanentlyDeleted` flags; `UndoService` (~381-421): never overwrite on undo.
- `SessionWriter` (~57, 168, 195, 224): newest state wins, no write of stale state; `SettingsStore` migration/backup boundaries; `ForwardedPathProtocol` limits and `\\?\` rejection.
- `SourceBytesCache` (~242-247, 176): invalidate on size/mtime/generation change, `_inFlight` cleanup after failure; `PreviewImageService:583` (no disk-cache write of a degraded decode); `PreloadScheduler` window (~623-636), pause/cancel, lifetime disposal; `DiskCacheStore` prune accounting.
- Decoding/Raw value assertions: output size and `needsFineScale` (`TurboJpegDecoder` ~129-217, `WicDirectDecoder` ~379), EXIF orientation range 1..8, RAW IFD/preview selection, `ExifQueryInterpreter`.