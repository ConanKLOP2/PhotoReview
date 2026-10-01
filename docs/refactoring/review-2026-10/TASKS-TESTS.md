# Review 2026-10: test-only gaps (RV-T*)

Missing tests where the review found NO code defect (tests that belong to a defect are inside that RV-C/S/A/I/R/P task).
Conventions and gates: [`PLAN.md`](PLAN.md). Every test must fail when the guarded code is broken: for each one,
record in the PR the mutation that made it fail. If a test exposes a real defect, STOP, add a new `RV-<area>` task to
the right TASKS file and the PLAN tracker, and fix it in a separate PR (do not hide a fix in a test-only PR).

Priority: **P1** risk to user files / journal / crash; **P2** wrong behaviour users can see; **P3** robustness and coverage.
Model: sonnet for all; one agent per PR (per test project); the agent may split work to haiku sub-agents per class.

## PR 11 `test/rv-core-gaps` — `tests/PhotoReview.Core.Tests/`

| ID | P | Test class (file) | Scenario → expected |
|---|---|---|---|
| RV-T01 | P1 | `OperationJournalTests` (`OperationJournalTests.cs`) | `ReadCommittedMoves` reverse path (no test references `ReadCommittedMovesReverse`/`StartupCommittedMoveLimit`): journal ≥ 1 MiB with > 200 committed Moves → newest 200 in order; one line longer than the 256 KiB window → skipped/handled, no throw; torn last line → ignored, earlier lines kept |
| RV-T02 | P1 | `OperationJournalTests` | `TryCompact`: append during the snapshot window → delta copied verbatim and a torn delta tail gets a newline; file prefix changed → `Changed`; concurrent compaction → `Busy`; injected IO failure mid-compaction → original journal byte-identical; stale `*.compact.tmp` older than the age limit removed, fresh one kept |
| RV-T03 | P1 | `JournalCompactionPlanTests` (`FileActions/`) | FA-01 stale reconcile `Failed` after `Committed` kept and not treated as latest; Id Dismissed then re-Prepared not dropped; a line containing an inner `\r` kept verbatim |
| RV-T04 | P2 | `OperationJournalTests` | `IsStaleReconcileAfterCommitted` through `ComputeLatestEntries`, `AppendIfLatestIs`, `ReadLatestEntry` (no test mentions it): Committed then stale Failed → latest is Committed |
| RV-T05 | P2 | `OperationJournalTests` | `Dismiss` of a snapshot whose latest line is `Prepared` and marked live by another process (live-marker fake) → refused/kept as documented; assert the documented behaviour |
| RV-T06 | P1 | `UndoServiceGroupTests` | Group Recycle undo with mixed Permanent and recycled members; restore fails half-way (fake bin) → partial result, Failed lines; retry closes the earlier Failed lines (`SettleFailedDeleteLine`, `PartialGroupUndo`) |
| RV-T07 | P2 | `RecoveryRetryServiceTests` | Group retry with the token cancelled mid-loop → Failed `CancelledByUser`, completed members NOT undone; Copy group retry with a partial destination → defined verdict; `confirmedFinishCancelled=false` refusal on the group path |
| RV-T08 | P2 | `ActionDestinationPolicyTests` | Source folder at a drive root (`C:\`) and a UNC root with a relative destination → resolved inside, Ok; `..` still rejected |
| RV-T09 | P2 | `FileActionServiceTests` | `ExecuteGroupAsync` with duplicate paths in the group → deduplicated or rejected (pin current); destination appears between preflight and `Move`/`TryCopyNew` → the foreign file is never deleted by compensation; Recycle bin overflow across two volumes (fake bin capacity) |
| RV-T10 | P3 | `ReviewCatalogTests` (`Catalog/`) | `TryReformGroup` direct tests: partial-undo reform with the current index on the 2nd member → current path unchanged |
| RV-T11 | P3 | `DuplicateFinderTests`, `CaptureGroupBuilderTests`, `ComparePairServiceTests` | Group where all members are numbered / all originals (survivor rule); same file with different case; hash throws `IOException` for one member → others still grouped; competing `.xmp` case variants → no sidecar claimed; `BuildIndex` equals `Find` for `a (1) (2)` |
| RV-T12 | P3 | `ForwardingCoreTests`, `UpdateCheckerTests`, new `PerfCsvListenerTests` | `ForwardedOpenCoalescer`: Dispose during the window → no open; first request without paths then one with → `_open` once with the later path. `UpdateChecker`: HTTP 403 non-rate-limit body and an `InvalidOperationException` from the handler → `Failed`, no throw. `PerfCsvListener.TryStartFromEnvironment` with an unwritable dir → null, nothing on disk |

## PR 12 `test/rv-app-gaps` — `tests/PhotoReview.App.Tests/`

| ID | P | Test class (file) | Scenario → expected |
|---|---|---|---|
| RV-T20 | P1 | `FileActionControllerGroupTests` | Service returns a failed Move with `sourceRemoved` (MoveUnverified, group == null) → status `StatusMoveUnverified`, file stays out of the catalog, no Undo registered |
| RV-T21 | P2 | `FileActionControllerLateCompletionTests` | `UndoLastAsync` partial failure when the catalog was empty (`wasEmpty` branch) → `PresentAsync` called; folder switched during that present → no stale present |
| RV-T22 | P2 | `FileActionControllerGroupTests` | `ReloadPathAfterUndo` / `RestoresOutsideFolder`: `RestoredPaths` without the Source, case-variant paths, null/empty current folder |
| RV-T23 | P2 | `DuplicateCleanupControllerTests` | `ShowBatchReview` returns true, then the catalog loses some paths (filtered remove) / all paths (`BatchCanceled`) |
| RV-T24 | P2 | `ZoomDetailTests` (`Coordinators/`) | `Reset` during an in-flight decode followed by a new target → the old `finally` does not clear the new `_cts` or indicator; a decode failure sets `_failedToken` and later zoom steps do not retry until the next navigation |
| RV-T25 | P3 | `FolderLoadCoordinatorTests` | `initialPath` not in the folder → order kept, index 0; `initialPath` already at index 0; drive-root folder; empty folder whose subfolder enumeration throws |
| RV-T26 | P3 | `PointerInputControllerTests` (`Input/`) | `OnSourceSizeSwapping` while `_zoomsAwaitingLayout > 0` → skipped; superseding viewport version drops the stale scroll; `OnImageRelease` with no tracked press → no-op |
| RV-T27 | P3 | `KineticPanTests` (`Input/`) | `AddImpulse` with NaN/Infinity → ignored; `Step` with elapsed > `MaxFrameMs` → clamped; `GlideFrameClock` after a period change |
| RV-T28 | P3 | new `PreloadControllerAdapterTests` | Each member delegates to the scheduler from the lazy `getScheduler` (today only `CompositionRootTests`) |
| RV-T29 | P1 | `FileHashServiceCancelTests`, `FileHashServiceTests` (`Services/`) | Second caller joins an in-flight entry whose Cts was just cancelled → retries and returns the real hash; file length/mtime changes during hashing (`CompleteIfUnchanged`) → localized `IOException`, nothing cached; `Clear()` during an in-flight hash → result returned, not cached |
| RV-T30 | P3 | new `ConvertersTests` | `ViewerStretchModeConverter` (Uniform→Uniform, None→Fill, non-enum→Fill, ConvertBack throws); `CompareBorderBrushConverter` (true/false/null); `ScalingQualityConverter` (Linear vs default/null) |
| RV-T31 | P3 | new `DispatcherUiSchedulerTests` (UI) | `YieldAsync` with an already-cancelled token throws; `InvokeAsync` after dispatcher shutdown → defined result |
| RV-T32 | P3 | `WindowPlacementServiceTests` (`Services/`) | Monitor-intersection check with a taskbar offset (placement partly under the taskbar stays valid) |

## PR 13 `test/rv-imaging-gaps` — `tests/PhotoReview.Imaging.Tests/`

| ID | P | Test class (file) | Scenario → expected |
|---|---|---|---|
| RV-T40 | P1 | `PreviewImageServiceTests` | Two viewer navigations join one Lazy; the creator's token cancelled before a slot is acquired → the joiner retries and gets an image, the creator gets OCE, `_previewLoads` empty afterwards |
| RV-T41 | P1 | `PreviewImageServiceTests` | `ClearCache()` (epoch bump) during an in-flight decode → image returned to the caller but not in `_cache`; disk persist skipped or the file deleted |
| RV-T42 | P2 | `SourceBytesCacheRobustnessTests` (`Caching/`) | `TryGetRange` (no test at all): hit after `GetOrReadRange`; miss; count ≤ 0 → false; offset > length → false; after `Evict` → false |
| RV-T43 | P2 | `SourceBytesCacheTests` | `GetOrReadRange` with count > `CapacityBytes` → bytes returned, no in-flight/cache entry, `CurrentSize` unchanged; count > `Array.MaxLength` → `ArgumentOutOfRangeException` |
| RV-T44 | P2 | `SourceBytesCacheRobustnessTests` | `Evict(path)` while a read of that path is blocked (`AfterReadForTests`) → bytes returned, not cached; an unrelated path still cached |
| RV-T45 | P2 | `PreloadLifetimeTrackingTests` | `Dispose` with a decode ignoring cancellation beyond `DisposeDrainTimeout` → Dispose returns, warning logged, slots/CTS disposed only after the worker finishes (no `ObjectDisposedException` in its finally) |
| RV-T46 | P2 | `DiskCacheStoreRobustnessTests` (`Caching/`) | `DeleteStaleTempFiles`: old file deleted, fresh kept, `maxFiles` cap honoured, missing dir → 0. `SchedulePrune` called during a running pass → exactly one extra pass; `_pruneScheduled` back to 0 even when `RunPrunePass` throws |
| RV-T47 | P2 | `PreviewCacheFileTests` (`Caching/`) | v6 entry (no EXIF block) → "no EXIF"; v7 with a length field past the end → `InvalidDataException`; valid header without EOI → rejected |
| RV-T48 | P3 | `RamBudgetPolicyTests` | Original (unbounded) box with PNG entries (×10 list overload vs ×3 `EstimateDecodedBytes` — pin or unify); 0 entries → 0; RAW with unknown length and unbounded box → `long.MaxValue` |
| RV-T49 | P3 | `PreviewImageServiceTests` | `EvictCachedPath` runs `alsoInvalidate` with the upper-cased full path and leaves other entries; `DecodeOriginalAsync` with a cancelled token before the gate allocates nothing and releases the gate |
| RV-T50 | P3 | `PreloadBusyBackoffTests`, `ThumbnailCacheLifecycleTests` | Concurrent `BeginPass`/`NoteBusy`; `Clear()` mid-cooldown resets; Dispose racing an in-flight `GetAsync` → no unobserved exception, no `ObjectDisposedException` from `_disposeCts.Token` |
| RV-T51 | P2 | `ExifParserTests` (`Metadata/`) | Self-pointing 0x8769 sub-IFD (to IFD0 or itself) and an IFD with count 65535 on a small block → terminates, no throw, bounded work (count reads) |
| RV-T52 | P2 | new `TurboJpegAvailabilityTests` | Probe cached; DLL absent → false with a reason; `SafeTurboJpegHandle.ReleaseHandle` with an invalid handle → no crash |
| RV-T53 | P3 | new `ExifOrientationTests`, `ExifSummaryCodecTests` | `Apply`/`CreateTransform` for 5 and 7 on a non-square asymmetric image (assert pixel positions); orientation 0/9/65535 → unchanged and frozen. Codec: 255-byte text cut mid-UTF-8 sequence; mask bit set with zero-length text |
| RV-T54 | P2 | `Cr3BmffHardeningTests` (`Raw/`) | > 256 sibling `moov` boxes and ~1M 8-byte `free` boxes → bounded work, budget `InvalidDataException` mapped to corrupt-RAW, no hang; `stsz` uniformSize 0 & count 0, `stco` count 0, `stsz.PayloadSize` 12-15 → no preview, no exception |
| RV-T55 | P2 | new `RawJpegIccProfileTests` (`Raw/`) | JPEG with an existing APP2 ICC → unchanged; non-JPEG → `InvalidDataException`; truncated segment lengths in `HasEmbeddedIcc`; output exact length and APP2 header correct |
| RV-T56 | P2 | `PreviewSelectorJpegFrameTests`, `RawHeaderHostileInputTests` (`Raw/`) | `TryExtractJpegDimensions`: truncated SOF, SOF3/SOF9, segLen past the buffer → false. `SourceRawHeaderSource`: two multi-block reads (first span invalidated — document or copy); stream EOF before declared `Length` → `InvalidDataException` |
| RV-T57 | P3 | `TiffReaderRegressionTests` (`Raw/Tiff/`) | Sony MakerNote offset pointing inside another IFD; Nikon MakerNote with `head[6] != 2` |
| RV-T58 | P3 | `LibRawManagedLogicTests` (`Raw/`) | `ReadJpegThumbnail` with a bitmap thumbnail → `InvalidDataException` routed to the full-decode fallback; `RgbBgraResampler` `BoxAverage`/`Bilinear` with channels = 1 and 1×N / N×1 sources → no out-of-range. Native (`Category=Native`): `ReadInfo` on a corrupt file, then the file can be deleted (no handle leak) |

## PR 14 `test/rv-integration-gaps` — `tests/PhotoReview.Integration.Tests/` (UI/Native, run hidden)

| ID | P | Test class (file) | Scenario → expected |
|---|---|---|---|
| RV-T60 | P2 | `SettingsWindowRoundTripTests` | `ExportSettings_Click` (0 tests) and `ImportSettings_Click`: malformed JSON, JSON `null`, `"Shortcuts": null`, read/write `IOException` → message shown, settings unchanged; valid file → round-trips normalized |
| RV-T61 | P2 | `RecoveryWindowTests` | Re-check while a check runs → old progress ignored, `RecheckButton` re-enabled exactly once; `ExecuteRetryAsync` with `Superseded` removes the row; Close cancelled while `_retrying` |
| RV-T62 | P2 | new `ClickZoomCustomDialogTests` | `"abc"`, `""`, Min-1, Max+1 → `ErrorText` visible, no `DialogResult`; Min, Max, `" 100 "` → value set, `DialogResult` true |
| RV-T63 | P2 | new `AppForwardedOpenTests` | `OpenForwarded`: minimized window → restored; null path → only Activate; faulted `OpenPathAsync` → logged, not thrown |
| RV-T64 | P2 | `RecoveryWindowTests` | After RV-C08: retry while a file action holds the gate → busy message shown, row unchanged (merge after PR 1) |
| RV-T65 | P3 | new `RecoveryPathPanelTests` | `Copy_Click` swallows `COMException`; `Show_Click` on a missing file opens the nearest existing folder; process start failure → `ExplorerFailed` raised |
| RV-T66 | P3 | new `WpfImageSurfaceTests` | `PointerPosition` null outside the viewport; `ViewportSize` subtracts `BorderThickness` |
| RV-T67 | P3 | `PlatformPrimitivesTests` | `RecycleBinCapacityGuard` with the real `WindowsRecycleBinSettingsSource` on the temp drive: `ExtractGuid` for a mount-point volume path → `{guid}` or null, never throws (read-only, never touches bin contents). `WindowsDisplayClock.GetTiming(IntPtr.Zero)` → DWM fallback (keep small, hardware-dependent) |
