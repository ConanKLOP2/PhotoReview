# Review 2026-10: RAW and Platform tasks (RV-R*, RV-P*)

Conventions, gates and PR mapping: [`PLAN.md`](PLAN.md). Line numbers at `151f4964`. RAW design: ADR 0009 and
[`../raw/PROGRESS.md`](../raw/PROGRESS.md) (do not reopen its "decisions taken with the user" table).

---

## PR 3 `fix/rv-raw-containers` (sonnet) — RV-R01..R04

Paths under `src/PhotoReview.Imaging.Raw/`; tests under `tests/PhotoReview.Imaging.Tests/Raw/`.

### RV-R01 — CR3 `trak` sample accepted as a JPEG preview on SOI only · MED · CONFIRMED
- **Where:** `Bmff/Cr3ContainerReader.cs:363-367` (`ParseTrackForJpegPreview`: `HasSoi(source, chunkOffset)` then
  `previews.Track.Add(NewJpeg(chunkOffset, sampleSize, 0, 0))`).
- **Problem:** the raw track (lossless JPEG, `FFD8 FFC3`, tens of MB) passes the SOI check. With width/height 0 it is
  an "unknown size" candidate; if PRVW and the full-size JPEG fail, `RawDecoder` reads up to 128 MB, WIC throws
  NotSupported, and one of `MaxPreviewAttempts` (4) is wasted. DNG/NEF already use `JpegMarkerProbe`.
- **Step 1:** `Cr3BmffHardeningTests.cs`: synthetic CR3 with a `trak` whose `stco` points at `FFD8 FFC3 …` plus a
  valid PRVW → `Track` is empty; and a variant where PRVW is corrupt → `RawDecoder` never reads the raw-track range
  (count reads via the header-source seam).
- **Step 2:** before adding: `if (!JpegMarkerProbe.TryReadLossyFrame(source, chunkOffset, sampleSize, out var w, out var h)) return;`
  and pass `w, h` to `NewJpeg` (use the exact probe signature in `Tiff/JpegMarkerProbe.cs`).
- **Step 3:** strict corpus run (PLAN gate 5) — every CR3 in the corpus still selects the same preview as before
  (compare `PreviewSelector` choice before/after in the PR description).

### RV-R02 — Header-budget `InvalidDataException` skips the LibRaw fallback · LOW · CONFIRMED
- **Where:** `Tiff/TiffHeaderNavigator.cs:385` (`ReconcileJpegSize`), `Tiff/NefContainerReader.cs:189`
  (`TryReadFullSizeJpegFrame`), `Tiff/JpegMarkerProbe.cs:31`.
- **Problem:** a preview JPEG with ≥ 128 APP segments ≥ 64 KB apart exhausts the 8 MiB header budget; the walker throws,
  `GetContainerInfo` reports "RAW corrupt", and the LibRaw full-decode fallback (which runs only after container parsing)
  is never tried. `ResolveDimensions`/`ResolveColorSpace` already catch it.
- **Step 1:** `Tiff/TiffPreviewHardeningTests.cs`: theory over ARW/CR2/ORF/DNG/NEF synthetic files with that preview →
  container parses, preview keeps the IFD-declared size (or 0×0), no exception. Same for
  `NefContainerReader.TryReadFullSizeJpegFrame` and `JpegMarkerProbe` (returns false).
- **Step 2:** catch `InvalidDataException` at the three call sites (keep declared size / return false).
- **Step 3:** `RawDecoderPreviewHardeningTests.cs`: a container reader that throws `InvalidDataException` with a
  `noPreviewDecoder` configured → pin the intended result (LibRaw fallback attempted, or a localized corrupt error) —
  read `RawDecoder` first and write the test for the CURRENT intended contract; if the contract is unclear, ask (new RV-D).

### RV-R03 — DNG stores negative width/height and truncates compression · LOW · CONFIRMED
- **Where:** `Tiff/DngContainerReader.cs:109`, `:114`, `:124` (`(int)Math.Min(w, int.MaxValue)`, `(ushort)comp`).
- **Step 1:** `Tiff/DngCropSizeTests.cs` / `Tiff/TiffSubIfdTypeTests.cs`: ImageWidth as SSHORT/SLONG -5 → no preview
  with negative size and `RawContainerInfo` sizes ≥ 0; Compression 65542 → not treated as 6.
- **Step 2:** use `TiffHeaderNavigator.ClampToInt` (as ORF/CR2 do) and reject `comp > ushort.MaxValue`.
- **Note:** `RawContainerInfo` is persisted in the preview cache: bump nothing unless the cache format changes (it does not).

### RV-R04 — `PreviewSelector` resolves every preview although the doc says it stops early · LOW · CONFIRMED
- **Where:** `PreviewSelector.cs:95-99` vs doc comments at `:11`, `:93`.
- **Decide in the PR (no user decision needed):** implement the documented "largest-first, stop when certain" if it is
  a small change and the corpus selection is identical; otherwise fix the doc comment. Prefer implementing (saves
  header I/O, AGENTS.md rule 1). Test: count walker invocations on a file with 3 previews where the first resolved one is
  certain → 1 walk.

---

## PR 10 `fix/rv-platform` (sonnet; P08 haiku) — RV-P01..P08

Paths under `src/PhotoReview.Platform.Windows/` unless stated; tests in `tests/PhotoReview.Integration.Tests/`.
**Never touch the user's real Recycle Bin** (AGENTS.md); bin tests use fakes or only their own items.

### RV-P01 — `InstanceForwardServer.Dispose` can block the UI thread up to 2 s · LOW · PLAUSIBLE
- **Where:** `InstanceForwardServer.cs:146` (`_loop.Wait(2s)`).
- **Step 0:** check whether the `onPaths` handler (App side) waits on the dispatcher. If it only `BeginInvoke`s → close
  as NOT-A-BUG with a test pinning that.
- **Fix:** cancel the loop and do not wait on the UI thread (or `Wait(TimeSpan.Zero)` + log); handler must `BeginInvoke`.
- **Test:** `InstanceForwardPipeTests`: Dispose while the handler is blocked on a `ManualResetEventSlim` → Dispose
  returns without waiting for the handler (assert the event is still unset when Dispose returns).

### RV-P02 — `TryRestore` can raise a modal shell dialog · LOW · PLAUSIBLE
- **Where:** `WindowsRecycleBin.cs:149-158` (restore verb on the caller thread).
- **Fix:** refuse up front when `File.Exists(originalPath)` (return false → the existing "restore failed" path), and run
  the verb off the UI thread (callers in `UndoService` already use `Task.Run` at `:322`; check every caller).
- **Test:** add a seam for the shell restore call; `WindowsRecycleBinCandidateTests.TryRestore_FileExistsAtOriginalPath_ReturnsFalseWithoutShellCall`.

### RV-P03 — Explorer window enumeration aborts on one COM failure · LOW · PLAUSIBLE
- **Where:** `Explorer/ExplorerWindowSelector.cs:28` (`MoveNext` outside the try).
- **Fix:** manual enumerator loop with `try { if (!e.MoveNext()) break; } catch (COMException) { continue or break }`
  — a failing `MoveNext` usually cannot continue; then break but KEEP the snapshots found so far.
- **Test:** `ExplorerWindowSelectorTests`: enumerable that yields one valid window then throws → first snapshot returned.

### RV-P04 — Prefetch CTS leak and prefetch after Dispose · LOW · CONFIRMED
- **Where:** `Explorer/ExplorerOrderService.cs:116`, `:154`, `:161`.
- **Fix:** re-check `_disposed` inside `_prefetchGate`; dispose the CTS when its task completes (continuation) and in Dispose.
- **Test:** Prefetch racing Dispose (barrier) → no prefetch stored after Dispose; `ObjectDisposedException` never thrown.

### RV-P05 — `InstanceScope.BeforeOpenAsync` throws on a malformed path · LOW · PLAUSIBLE
- **Where:** `InstanceScope.cs:103` (`Path.GetFullPath(initialPath ?? folder)` unguarded on the OwnedElsewhere path).
- **Fix:** guard like `InstanceKeys.CanonicalKey`: catch `ArgumentException`/`NotSupportedException`/`PathTooLongException`
  → return the existing "owned by other instance / cannot open" decision.
- **Test:** `InstanceScopeTests.BeforeOpenAsync_FolderWithNulWhileOwnedElsewhere_ReturnsDecision`.

### RV-P06 — Negative memory reserve disables preload · LOW · CONFIRMED
- **Where:** `WindowsMemoryProbe.cs:47` (`(ulong)reserveBytes`).
- **Fix:** `Math.Max(0, reserveBytes)`; also clamp/report in `SettingsNormalizer` if the reserve comes from settings.
- **Test:** `MemoryHeadroomTests.EvaluateHeadroom_NegativeReserve_TreatedAsZero`.

### RV-P07 — Generator fails on two `en.json` additional files · LOW · PLAUSIBLE
- **Where:** `src/PhotoReview.Localization.Generator/TrGenerator.cs:38`, `:144` (`AddSource("Tr.g.cs")` per file).
- **Fix:** `.Collect()` the matching texts; if more than one, report a new diagnostic `PRLOC0xx` (next free id) and
  generate from the first by path order.
- **Tests:** `tests/PhotoReview.Core.Tests/Localization/LocalizationGeneratorTests.cs`: two `en.json` → diagnostic, no
  CS8785; plural `x.one` when `x` is a plain key, and `a.b` vs `a.B` → PRLOC003, first claimant kept.

### RV-P08 — Dangling `<summary>` in `WindowsRecycleBin` · LOW · CONFIRMED · haiku
- **Where:** `WindowsRecycleBin.cs:197-205` — the "Decides whether the shell can really recycle" summary sits above
  `RecycleItemFailure`; move it to `RecycleEligibility`. No test.
