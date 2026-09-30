# RAW progress 04 - In progress and how to resume

State as of 2026-09-30, checked with `git` and read-only `gh` at the time of writing. Where the session fact sheet and git
disagreed, git is used and the difference is stated.

## 1. Exact current state

| Item | State |
|---|---|
| PR | https://github.com/ConanKLOP2/PhotoReview/pull/239 (`feat/raw-support-integration` -> `master`), OPEN, mergeable per `gh` |
| Draft flag | `gh` reports `isDraft: false` (the fact sheet said draft; trust `gh`, re-check before relying on it) |
| Branch tip | `cd8ff26d` (also the tip of `origin/feat/raw-support-integration`, so all commits below are pushed) |
| Size | 120 commits in `origin/master..HEAD`; 29 commits in `c1e5c452..HEAD` (`c1e5c452` = tip at the start of the review work) |
| Master drift | `origin/master` (`53c45d9d`) is 1 commit ahead of the merge-base `16ac0cd5`; master is NOT an ancestor of the branch, so a merge or rebase of master may be needed |
| CI | Last two completed runs succeeded at `f8730c11` and `4f6b0bde`; the run for `cd8ff26d` (id 36714406524) was still `queued` at the time of writing, so CI at the tip is NOT VERIFIED |
| Local-only work | No integration-branch commit is local-only; this docs series is committed in a scratch worktree and not pushed |

### Pushed but not covered by a full test run

Commits after `4f6b0bde` were pushed without a full test run (explicit user request "push now, no more tests"):

- `a18adf82` shared `RawCorpus` helper; corpus tests fail instead of skip under `PHOTOREVIEW_RAW_CORPUS_STRICT=1`
- `e4a88645` composition tests for the `RecoveryRetryService` settings delegate and the `RawDecoder` LibRaw no-preview wiring
- `cd8ff26d` journal downgrade guard pinned with authentic `origin/master` journal lines

Last full local run (Release, `Category!=Manual`, before those commits): Architecture 64, Core 1911, App 1289,
Integration 771 passed; Imaging 1267 of 1268 (the failing one is the memory guard, since made robust in `f8730c11`).

## 2. In progress

### 2.1 OC14 CI flake (root cause not found)
- Test: `OC14_FileActionDuringUndo...` failed once on CI (total+1 assertion, expected 2, actual 1) on a commit that changed no
  App code. A rerun passed; 25 of 25 local runs passed.
- An agent (workflow run `wf_88e3099b-97d`) is investigating in the worktree
  `D:/MyProject/PhotoReview/.claude/worktrees/wf_88e3099b-97d-4`, branch `worktree-wf_88e3099b-97d-4` (git shows it at
  `f8730c11`, locked). It had uncommitted changes; its result is NOT integrated. Inspect that worktree before deleting it.
- Treat the flake as an open risk: a red CI on the App suite may be this, so rerun once before investigating.

### 2.2 Manual corpus workflow never run
- `.github/workflows/raw-corpus.yml` (`workflow_dispatch`) fetches the 23 CC0 samples (about 590 MB, cached), checks
  completeness, and runs `Imaging.Tests` (`Category!=Manual`, including Native) with `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and
  `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`.
- GitHub offers "Run workflow" only once the file is on the default branch, so it cannot run before #239 merges.
  Until then the corpus tests skip silently in normal CI.

### 2.3 Not verified on a running app
All behaviour changes below have automated tests and corpus checks only, no human check in the running app:
- RAF displayed size (raw size from the CFA header)
- LibRaw zoom decode with camera white balance (written by struct offset with a read-back guard; least comfortable part)
- RAM estimate now conservative for RAW in Original loading mode; RAW full decode on zoom in that mode
- EXIF block up to 4 MiB
- RAW toggle / pair-mode change reloads the folder
- Recovery retry refuses permanent delete when `AllowPermanentDeleteWithoutRecycleBin` is off
- Localized RAW error sentences and the "RAW preview WxH" info line

### 2.4 Decisions already taken (2026-09-30), not open
Keep RW2 1:1 crop; keep ORF size; keep size-only reconcile of group Copy; accept older-build journal risk (guard + README
note, residual risk in ADR 0003); accept LibRaw in-process (ADR 0009 keeps full decode `Never` by default).

## 3. Resume checklist (another machine)

1. `git clone https://github.com/ConanKLOP2/PhotoReview.git` (or `git fetch origin` in an existing clone).
2. `git worktree add .claude/worktrees/raw -b work/raw origin/feat/raw-support-integration` (tip `cd8ff26d` or newer).
   Do not edit the main checkout (see `AGENTS.md`).
3. Read `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md`, then `docs/refactoring/raw/DECISIONS.md`.
4. Native binaries (untracked, never commit): `tools/fetch-native.ps1` and `tools/fetch-libraw.ps1`
   (`-Verify` checks pins offline; the LibRaw fetch also runs on build). Run with
   `powershell -NoProfile -ExecutionPolicy Bypass -File <script>`.
5. RAW corpus (untracked, about 590 MB): `tools/fetch-raw-samples.ps1` (manifest `tools/raw-samples.txt`; optional
   `-FormatFilter`, `-Limit`; it fails if the filter matches nothing).
6. Build: `dotnet build PhotoReview.slnx -c Release` (must be 0 warnings).
7. Gates: `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"`; for corpus tests set
   `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1` first. Docs gates:
   `tools/docs-budget.ps1 -Check` and `tools/check-doc-links.ps1`.
8. CI status: `gh pr view 239 --json state,isDraft,mergeable,headRefOid` and
   `gh run list --branch feat/raw-support-integration`; confirm the run's `headSha` equals the branch tip.
9. Before merge, check master drift: `git rev-list --count HEAD..origin/master`; merge or rebase per team practice.
10. After merge: Actions > "RAW corpus tests (manual)" > Run workflow on `master`, or `gh workflow run raw-corpus.yml`;
    then `gh run watch`. Fix any strict-mode failure it exposes.
11. After the user merges, verify with `git merge-base --is-ancestor <head> origin/master`, not the MERGED label.

## 4. Cleanup (review first, do NOT run blindly)

Leftovers on the original working machine:
- Files: `C:\jxtmp` (empty), `D:\tmp_ps.txt`.
- Worktrees under `D:/MyProject/PhotoReview/.claude/worktrees/`: `agent-*`, `wf_*` (several are `locked`), and
  `raw-integration-branch-status-91809e` (branch `work/raw-direct`). The Codex worktree
  `C:/Users/KymdanVti/.codex/worktrees/raw-cache-preload/PhotoReview` (`feat/raw22-cache-preload-ram`) is NOT scratch; keep it.
- Local scratch branches: `worktree-*`, `work/raw-direct`, `fix/pr241-followup`, `fix/pr241-review-followup`,
  `chore/merge-master-into-raw`, `test/pr242-missing-tests`, `fix/raw-remaining-test-failures`,
  `claude/raw-integration-branch-status-91809e`.
- The corpus (`tests/Fixtures/raw-corpus`) and `native/` binaries are untracked local copies; never commit them.

Safe procedure (only once the work is confirmed on the integration branch; the OC14 worktree first needs its uncommitted
changes saved or discarded on purpose):

```powershell
# 1. list and inspect
git worktree list
git -C <worktree> status --short            # must be empty, or discarded on purpose
# 2. branches holding commits NOT in the integration branch (do not delete these blindly)
git branch --no-merged origin/feat/raw-support-integration
# 3. remove one worktree (locked ones: unlock first)
git worktree unlock <path>
git worktree remove <path>                   # refuses if dirty; --force only after step 1
git worktree prune
# 4. delete a branch (-d refuses unmerged work; -D only after checking step 2)
git branch -d <branch>
```

Squash-merged PR branches (`fix/pr241-*`, `test/pr242-*`) may need `-D` even though their content landed; compare with
`git diff origin/feat/raw-support-integration <branch>` first.

## 5. What to do next (by value)

1. Verify the behaviour changes of section 2.3 in the running app on real RAW folders (highest risk: LibRaw white balance,
   RAF size, RAM estimate).
2. Confirm CI is green at the branch tip, handle master drift, then merge #239.
3. Run the manual corpus workflow after merge; it is the only automated check of real-camera files and LibRaw.
4. Decide the remaining optional items: separate journal file instead of the downgrade guard, 2 s mtime tolerance for group
   Copy reconcile, a crop property for RW2, a helper process if `RawFullDecode OnZoom` ever becomes default.
5. Find the OC14 flake root cause (integrate or discard the agent worktree), then do the cleanup in section 4.
