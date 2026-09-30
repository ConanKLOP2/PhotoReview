# RAW integration: decisions and open items

Status record for PR https://github.com/ConanKLOP2/PhotoReview/pull/239 (`feat/raw-support-integration` -> `master`).
Written 2026-09-30 from the working session plus git history; where they disagreed, git was trusted.

- Tip when written: `cd8ff26d`. `git log origin/master..origin/feat/raw-support-integration` lists 120 commits
  (the session fact sheet said 85 before review work; the 120 includes the review-fix waves and merged PRs).
- Start of this session's review work: `c1e5c452` (after PRs #241-#245 were merged into the branch).
- The original RAW format/zoom/pair/decoder decisions (Q-RAW-01..07) stay in [DECISIONS.md](../DECISIONS.md) and
  [ADR 0009](../../../adr/0009-camera-raw-support.md); this file only adds what was decided or deferred later.

## Status legend

| Tag | Meaning |
|---|---|
| DONE | Implemented on the branch, covered by automated tests |
| IN PROGRESS | Being worked on; result not integrated |
| NOT VERIFIED | No evidence beyond code reading, or never run on a real app or real CI |

## Decisions taken with the user (2026-09-30)

| Item | Context | Options | Chosen | Residual risk | Revisit when |
|---|---|---|---|---|---|
| RW2 1:1 crop | Panasonic GH5 1:1 file: crop 3888x3888 disagrees with the 4:3 embedded preview and with LibRaw | (a) keep as is; (b) add a crop property to `RawContainerInfo` | (a) keep | Displayed and 100 % size of 1:1 RW2 may differ from LibRaw | A user reports wrong RW2 aspect or size |
| ORF displayed size | IFD0 size is the raw frame; LibRaw decodes it unchanged (E-M1 4640x3472, OM-1 5220x3912) except a hard-coded LibRaw rule that trims a 4080-wide frame (E-PM1/E-PL3/E-P3) by 24 columns; the camera image proper is MakerNote 0x0614/0x0615 (4608x3456, 5184x3888, 4032x3024) | (a) keep IFD0; (b) apply the LibRaw trim so reader == zoom decode; (c) report 0x0614/0x0615 | (b): reader reports 4056x3040 for E-P3, pinned against a real decode (`OrfDisplayedSizeTests`). Cross-format audit: 20 of 23 corpus files report the camera-visible size and LibRaw a few px more (`RawSizeConsistencyAuditTests`) | (b) mirrors one LibRaw family rule; other Olympus widths with LibRaw trims (4100, 10400, 8200, 8180, 9280) are not mirrored (no corpus file) | A new Olympus body shows reader != LibRaw size, or a LibRaw bump changes identify.cpp |
| Group Copy reconcile | Reconcile compares a group Copy by size only, not mtime | (a) size only; (b) mtime with ~2 s tolerance | (a) keep (FAT/exFAT mtime rounding risk); tolerance stays optional | A same-size but different Copy destination counts as done | Group Copy moves to a checksum or volume-aware rule |
| Older-build journal | Older builds drop `GroupId`/`GroupMembers` when they rewrite a group line ([ADR 0003](../../../adr/0003-journal-startup.md)) | (a) guard in the new build plus README note; (b) separate journal file | (a) accepted | Residual risks are listed in ADR 0003: older-build compaction hides the signature; older-build Undo acts on the first member only | A downgrade becomes a supported scenario: reconsider (b) |
| LibRaw in-process | LibRaw is native code loaded in the app process | (a) in-process; (b) separate helper process | (a) accepted | A native crash in LibRaw takes the app down | `RawFullDecode = OnZoom` becomes the default (ADR 0009 keeps `Never`) |
| Error keys and overlay | `image.error.rawUnsupported` / `rawCorrupt` / `rawNoPreview` and `overlay.rawPreview` were unwired | Wire or leave | DONE: wired via `UserFacingError.Localized`; info line shows "RAW preview WxH" (Q-RAW-03), hidden after full decode | Wording not reviewed by a native speaker | Next localization pass |

## Open or deferred items

| Item | State | Context and residual risk | Revisit when |
|---|---|---|---|
| White-balance offset write | DONE, NOT VERIFIED on a real app | LibRaw zoom decode uses camera white balance, written by a verified struct offset with a read-back guard (`232c9005`). Least comfortable part: a LibRaw layout change breaks the offset; the read-back guard is meant to detect that | Any LibRaw version bump; first real-machine zoom check |
| Cache-restored RAW has no preview label | Known limitation | `PhotoInfo.RawPreviewWidth/Height` is 0 for a cache-restored image because it cannot tell (doc comment in `ImagePresenter.cs`), so the "RAW preview WxH" text is absent there; the dimensions shown stay correct | When cache entries carry preview identity |
| `OC14_FileActionDuringUndo...` CI flake | OPEN (investigation stopped manually) | Failed once on CI (expected 2, actual 1) on a commit that changed no App code; rerun passed; 25/25 local passes. Agent `oc14-flake` (branch `worktree-wf_88e3099b-97d-4`) was finding the root cause and was stopped manually; it had uncommitted changes and is NOT integrated | Agent result reviewed |
| Manual corpus workflow | NOT VERIFIED | `.github/workflows/raw-corpus.yml` (`4f6b0bde`) has never run: GitHub offers "Run workflow" only once the file is on `master`. Until then RAW corpus tests skip silently in CI | After the first merge to `master`, run it once |
| Memory leak guard | DONE | `Decode_Repeated200Times_DoesNotContinuouslyGrowPrivateBytes` (Category=Native, not in CI) failed identically at `c1e5c452` (bimodal private bytes). The guard now compares medians with a 192 MB budget (`f8730c11`); an injected 8 MB/decode leak fails it, 1 MB/decode is below resolution | More machines |
| SEC-01 junction tests | DONE (skip) | Real-junction tests skip when `%TEMP%` is under redirected AppData (Claude desktop app); they do not run there | Temp policy changes |
| Last three test commits | NOT VERIFIED | `a18adf82`, `e4a88645`, `cd8ff26d` were pushed WITHOUT a full test run, at the user's explicit request | Next full run |
| Human check on a running app | NOT VERIFIED | Only automated tests plus corpus cover the behaviour changes below; the RAW-62 real-machine check remains waived (ADR 0009) | Before merge |
| Machine leftovers | Open | `C:\jxtmp`, `D:\tmp_ps.txt`, agent worktrees (`agent-*`, `wf_*`), scratch branches (`worktree-*`, `work/raw-direct`, `fix/pr241-*`, `chore/*`, `test/pr242-*`, `fix/raw-remaining-test-failures`); the RAW corpus (~590 MB) and `native/` binaries are untracked (never commit) | Delete once commits are on the branch |

## Behaviour changes to review before merge

1. RAF displayed size now comes from the CFA header: X-T2 6000x4000, X-E2S 4896x3264, X100V 6240x4160 (`2db2ef11`).
2. LibRaw zoom decode uses camera white balance; insufficient memory maps to `InvalidOperationException`, not "corrupt" (`232c9005`).
3. RAM estimate is conservative for RAW in Original mode, incl. Canon sRAW (`b2cccf5f`); Original mode offers the full decode on zoom (`a52a53ef`).
4. TIFF EXIF block is sized from IFD offsets, cap 4 MiB (was fixed 128 KiB) (`eb41bac9`).
5. Changing the RAW toggle or `RawPairMode` reloads the folder (`07037175`, `8f9af342`).
6. Recovery retry refuses a permanent delete when `AllowPermanentDeleteWithoutRecycleBin` is off (`37ae2f4c`).
7. A RAW without an embedded JPEG falls back to LibRaw instead of failing (`ceaf3bed`).
8. Adobe RGB is detected via the Interop IFD "R03", also for previews with a declared size (`aa3d6124`).
9. Group undo retry closes earlier Failed lines (`2ed0edd8`); a partly failed group Recycle registers completed members for Ctrl+Z (`90271a85`).
10. Journal lines with a blank member Source are quarantined (`80492486`); the downgrade guard restores group members at startup (`4ad0c680`).
11. `RemovePaths` degrades a capture instead of dropping it; group Move cleans a partial cross-volume destination when provably ours (`dd3483af`).
12. Localized RAW error sentences and the RAW preview size on the info line (`e6576196`, `3d2aafe6`, `19f3ba29`).
13. Unused `WicRawFullDecoder` / `WicCodecRegistry` removed (`54a1b1c6`).

## Findings deliberately not changed

- RawSurvey JPEG item: not a real bug.
- Reconcile Copy mtime comparison and ORF active area: unchanged (see the decisions table).

## Docs to update

- [DECISIONS.md](../DECISIONS.md) Q-RAW-02: still says RAW-30 probes WIC for the survey; the WIC full decoder was removed
  (`54a1b1c6`), so the WIC comparison is survey history only.
- [SURVEY.md](../SURVEY.md): ORF rows show "Unsupported"; ORF EXIF magics and the RAF size logic were fixed after it was
  written. Mark it as a pre-fix snapshot or re-run the survey.
- ADR 0003 group-journal section: add the blank-Source quarantine and "Committed with a missing member becomes Failed in
  Recovery" if not already covered; the downgrade guard is already described there.
- `task_on_progress.md` and `docs/ACTIVE-TASKS.md`: refresh by a docs-sync change (AGENTS.md rule).
