# RAW integration - Tests and CI

Status record, 2026-09-30, for [PR #239](https://github.com/ConanKLOP2/PhotoReview/pull/239) (`feat/raw-support-integration` -> `master`, draft). Tip when written: `cd8ff26d`. Related gate detail: [docs/TESTING.md](../../../TESTING.md).

Legend: DONE = done and evidenced; IN PROGRESS = being worked; NOT VERIFIED = no evidence yet.

## 1. Test counts (last full local run)

Release build, filter `Category!=Manual`, run before the last three test commits.

| Project | Result |
|---|---|
| Architecture | 64 pass |
| Core | 1911 pass |
| App | 1289 pass |
| Integration | 771 pass |
| Imaging | 1267 of 1268 pass; the one failure was the LibRaw private-bytes test (section 2, since reworked) |

- NOT VERIFIED: the last three test commits were pushed WITHOUT a full test run, at the user's explicit request ("push now, no more tests"): `a18adf82` (RawCorpus helper), `e4a88645` (composition/wiring guards), `cd8ff26d` (journal fixtures).
- DONE: CI of #239 was green (3/3) at `4f6b0bde`. Later commits have not been observed on CI in this record.
- CI gate (`.github/workflows/ci.yml`): `Category!=Manual&Category!=Native&Category!=Slow`, plus a second step for `Category=Integration&Category=Slow`. So `Native` (real OS/DLL) tests never run in regular CI.

## 2. Flaky and environmental tests

| Test | What was measured | State |
|---|---|---|
| `Decode_Repeated200Times_DoesNotContinuouslyGrowPrivateBytes` (Imaging, `Category=Native`, not in CI) | Failed on identical runs, also at `c1e5c452` (growth -103 MB..+72 MB against a 32 MB budget). Private bytes after a full GC are bimodal (about 80 MB or 135-215 MB). | `f8730c11`: compares medians of samples (decodes 50-100 vs 150-200), budget 192 MB. An injected 8 MB/decode leak gives +789 MB and fails; a 1 MB/decode leak is below resolution. DONE, but not re-run on a full suite. |
| `OC14_FileActionDuringUndo...` (App) | Failed once on CI (total+1: expected 2, actual 1) on a commit that changed no App code; rerun passed; 25/25 local passes. | IN PROGRESS: agent `oc14-flake` (run `wf_88e3099b-97d`) is finding the root cause; it had uncommitted changes, result not integrated. Root cause unknown. |
| SEC-01 real-junction tests | Skip on machines whose `%TEMP%` is under redirected AppData (Claude desktop app); junction writes are not allowed there. | DONE in #245 (`a9fe5f81`): skip when the temp folder does not allow junction writes. Coverage is therefore absent on such machines. |
| `GroupEntries` timing test (Core) | Wall-clock budget flaked under full parallel runs (see TESTING.md "Timing tests"). | DONE in #245 (`a9fe5f81`): replaced by an allocation guard, plus an allocation trim in `CaptureGroupBuilder`; timings live in a `Category=Manual` report test. |

## 3. RAW corpus and strict mode

- The corpus is 23 CC0 samples from raw.pixls.us (~590 MB), listed in `tools/raw-samples.txt`, fetched by `tools/fetch-raw-samples.ps1` into `tests/Fixtures/raw-corpus`. It is gitignored and never committed.
- Fetch script: SHA-256 pinned per file, fail-closed on mismatch; in normal mode each pinned URL is matched to the live raw.pixls.us record and its CC0 URL/label must agree with the manifest. `-SelfTest` runs in CI without network. A `-FormatFilter` that matches nothing now fails (`71634d91`).
- Regular CI never fetches the corpus, so corpus-dependent tests return early (silent skip) there.
- Strict mode: shared `RawCorpus` helper (`a18adf82`). With `PHOTOREVIEW_RAW_CORPUS_STRICT=1` a missing sample makes the test fail instead of skip. `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1` does the same for `libraw.dll` and makes the LibRaw test decode the whole corpus (it cannot be combined with `PHOTOREVIEW_LIBRAW_SAMPLE`, which selects one sample).

## 4. Manual workflow "RAW corpus tests (manual)"

File: `.github/workflows/raw-corpus.yml`, added in `4f6b0bde`. `workflow_dispatch` only, `windows-latest`, 45 min timeout.

- Steps: fetch native + LibRaw binaries, restore the corpus cache (key = hash of `tools/raw-samples.txt`), run `fetch-raw-samples.ps1`, check file count >= manifest rows, build Imaging.Tests (Release), run it with `--filter "Category!=Manual"` (so `Native` is included), both strict env vars set, upload TRX.
- How to run: GitHub Actions > "RAW corpus tests (manual)" > Run workflow. GitHub only offers the button once the file is on the default branch (`master`), so it becomes usable after #239 merges.
- NOT VERIFIED: the workflow has NOT been run yet; the YAML, cache key and step wiring are untested on a runner.
- Does not cover: App/Core/Integration projects (Imaging only); UI behaviour; anything needing a human; cameras outside the 23 samples.

## 5. Not covered by automated tests

- NOT VERIFIED on a running app by a human: all behaviour changes of the review waves (RAF displayed size; LibRaw zoom decode with camera white balance, written by struct offset with a read-back guard, the part least comfortable; conservative RAM estimate for RAW in Original mode; EXIF block up to 4 MiB; RAW toggle / pair-mode change reloads the folder; Recovery retry refusing permanent delete when the setting is off). Evidence is automated tests plus the corpus only.
- Corpus tests do not run in regular CI (section 3); the manual workflow is unrun.
- `Native` tests (LibRaw runtime) are outside the CI gate.
- SEC-01 junction tests skip on redirected-temp machines.
- LibRaw runs in-process: a native crash would take the app down; no test can show otherwise. A helper process is only planned if RawFullDecode OnZoom becomes default (ADR 0009 keeps it Never).
- Older-build journal downgrade: guard is unit-tested (`4ad0c680`, fixtures from authentic origin/master journal lines in `cd8ff26d`), but a real older build was not run against it in this record.
- Decided, so intentionally not tested or changed: RW2 1:1 crop, ORF size, size-only reconcile of group Copy.

## 6. Reproduce locally

```powershell
# whole gate, hidden desktop (UI windows stay off your desktop)
tools/verify-all.ps1 -All -Hidden
# one project
tools/run-tests-hidden.ps1 tests/PhotoReview.Imaging.Tests -c Release --nologo --filter "Category!=Manual"
# corpus + strict mode
tools/fetch-native.ps1; tools/fetch-libraw.ps1; tools/fetch-raw-samples.ps1
$env:PHOTOREVIEW_RAW_CORPUS_STRICT = "1"
$env:PHOTOREVIEW_LIBRAW_STRICT_CORPUS = "1"
tools/run-tests-hidden.ps1 tests/PhotoReview.Imaging.Tests -c Release --nologo --filter "Category!=Manual"
```

- Optional `PHOTOREVIEW_LIBRAW_SAMPLE=<file>` decodes one sample only (not with strict LibRaw mode).
- Never run `dotnet test` unbounded and never exclude `Category=UI` locally (AGENTS.md > Tests).

## 7. Git notes

- `c1e5c452` is the #244 merge commit, so `git log c1e5c452..HEAD` contains #245 (`a9fe5f81`) and the direct commits; #241 (`6b840e19`), #242 (`8f9af342`), #243 (`34ed759c`) and #244 are before it. Git wins over the earlier session notes where they differ.
- PRs: [#241](https://github.com/ConanKLOP2/PhotoReview/pull/241), [#242](https://github.com/ConanKLOP2/PhotoReview/pull/242), [#243](https://github.com/ConanKLOP2/PhotoReview/pull/243), [#244](https://github.com/ConanKLOP2/PhotoReview/pull/244), [#245](https://github.com/ConanKLOP2/PhotoReview/pull/245).
