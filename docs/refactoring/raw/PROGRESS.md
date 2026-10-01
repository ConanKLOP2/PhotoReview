# Camera RAW support: post-merge record

PR [#239](https://github.com/ConanKLOP2/PhotoReview/pull/239) was merged into `master` on 2026-10-01 (squash `2f2bf342`). Design and decisions: [ADR 0009](../../adr/0009-camera-raw-support.md), [DECISIONS.md](DECISIONS.md) (Q-RAW); the pre-fix survey report was removed 2026-10-01 (`git show 2f2bf342:docs/refactoring/raw/SURVEY.md`). The long progress series (review waves, test/CI notes, resume checklist, the 2026-10-01 multi-agent review) was deleted after the merge; recover it with `git show 2f2bf342:docs/refactoring/raw/progress/<file>` (`01-review-and-fixes`, `02-tests-and-ci`, `03-decisions-and-open-items`, `04-in-progress-and-resume`, `05-multi-agent-review-2026-10-01`).

## Evidence

Full local gate and strict corpus run green at `c624d083` (Imaging 1651/1651 with `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`); CI green at the merged tip. The OC14 CI flake was root-caused and fixed (stale FileNotFound dropped a restored file).

## Decisions taken with the user (2026-09-30), do not reopen without a new report

| Item | Chosen | Revisit when |
|---|---|---|
| RW2 1:1 crop (GH5) | Keep as is; no crop property on `RawContainerInfo` | A user reports wrong RW2 aspect or size |
| ORF displayed size | Reader mirrors LibRaw's 4080-wide trim (E-P3 reports 4056x3040); other Olympus widths not mirrored | A new Olympus body shows reader != LibRaw size, or a LibRaw bump changes `identify.cpp` |
| Group Copy reconcile | Compare by size only (FAT/exFAT mtime rounding risk) | Group Copy moves to a checksum or volume-aware rule |
| Older-build journal | Downgrade guard in the new build plus README note; no separate journal file (ADR 0003) | A downgrade becomes a supported scenario |
| LibRaw in-process | Accepted; a native crash takes the app down | `RawFullDecode = OnZoom` becomes the default |

## Still open (all optional)

| Item | State |
|---|---|
| Manual workflow `.github/workflows/raw-corpus.yml` | Never run: GitHub offers "Run workflow" only for a default-branch file. Until it runs once on `master`, RAW corpus tests skip in CI. Run it, then fix any strict-mode failure |
| RAW-62 real-machine check | Waived by the user. NOT VERIFIED in a running app: LibRaw zoom white balance (written by struct offset with a read-back guard, re-check on any LibRaw bump), RAF displayed size, RAM estimate in Original mode, 4 MiB EXIF block, folder reload on RAW/pair toggle, localized RAW errors |
| Cache-restored RAW has no "RAW preview WxH" label | Known limitation: `PhotoInfo.RawPreviewWidth/Height` is 0 for cache-restored images |
| Machine leftovers (original dev PC) | `C:\jxtmp`, `D:\tmp_ps.txt`, agent worktrees (`agent-*`, `wf_*`), scratch branches (`worktree-*`, `work/raw-direct`, `fix/pr241-*`, `chore/*`, `test/pr242-*`, `fix/raw-remaining-test-failures`); the Codex worktree `raw-cache-preload` is NOT scratch. Untracked, never commit: `tests/Fixtures/raw-corpus` (~590 MB) and `native/` binaries. Inspect with `git worktree list` and `git branch --no-merged origin/master` before deleting anything |

## Reproduce locally

- Native binaries: `tools/fetch-native.ps1`, `tools/fetch-libraw.ps1` (`-Verify` checks pins offline). Corpus: `tools/fetch-raw-samples.ps1` (manifest `tools/raw-samples.txt`).
- Gates and filters: [TESTING.md](../../TESTING.md) (corpus tests: set both `*_STRICT` variables above).
