# AR06 — Release output and workspace cleanup

**Finding:** F8 · **Decision:** Q-AR4 (recommended: CI path is the only release path) · **Branch:** `chore/ar06-release-cleanup` · **Size:** ~2 h · **GUI:** no

## 1. State (verified 2026-09-23 on the user's machine)

| Item | State | Tracked in git? |
|---|---|---|
| `src/PhotoReview.App/bin/Release/net10.0-windows/publish/` | Path used by README, CI (`ci.yml` "Publish Release"), `verify-all.ps1`, `verify-release.ps1`. Local copy dated 2026-09-18 (stale). | No (bin) |
| `outputs/release/PhotoReview-framework-dependent/` | 5 files: App exe/dll/pdb/deps/runtimeconfig — **no `PhotoReview.Core.dll`** → cannot start. | No |
| `outputs/*.ps1`, `outputs/photo-review-config.example.json` | Association scripts + example config, referenced by README. | Yes |
| Root `PhotoReview.App/`, `PhotoReview.Tests/`, `PhotoReview.Tests.Unit/` | Only `bin/`, `obj/`, a `.csproj.user`, `Properties/PublishProfiles` — leftovers of the pre-`src/` layout. | No (ignored) |
| `git worktree list` | `C:/Users/.../.codex/worktrees/6559/PhotoReview` and `.claude/worktrees/agent-addb7cccfe75b70eb` marked **prunable**. | — |
| Local branch `refactor/T14c-inv5` | Not on origin. | — |
| `Claude outputs/` at repo root | Untracked copy of an assistant report. | No |

## 2. Steps (Q-AR4 = "CI path only")
1. Delete `outputs/release/` (untracked, broken). Add `outputs/release/` to `.gitignore` so it cannot come back as a half-copy.
2. Check `PhotoReview.App/Properties/PublishProfiles/*.pubxml` (root leftover) for anything not already in `src/PhotoReview.App/Properties/PublishProfiles`; if identical or absent, delete the three root leftover folders.
3. `git worktree prune -v`; ask the user before deleting `refactor/T14c-inv5` (`git log master..refactor/T14c-inv5 --oneline` first; if empty → safe).
4. Move `Claude outputs/` content into `docs/refactoring/ARCH-REVIEW-SUMMARY.md` (already done by AR00) and delete the folder; add `Claude outputs/` to `.gitignore`.
5. README (EN + VI) "Build, Test, and Publish": state that the only supported release folder is `src/PhotoReview.App/bin/Release/net10.0-windows/publish` (framework-dependent) and that CI uploads it as artifact `release-publish`.
6. After AR01: `verify-release.ps1` also lists `PhotoReview.Core.dll`, `PhotoReview.Imaging.dll`, `PhotoReview.Platform.Windows.dll`, `PhotoReview.Benchmarking.dll`, `PhotoReview.PerfAnalysis.dll` in `$required` — the broken `outputs/release` copy shows why checking only the exe is not enough.

## 3. Verification
`git status` clean except intended changes; `.\tools\verify-all.ps1` passes; `verify-release.ps1` fails when any required dll is deleted from a copy of the publish folder (try once manually).

## 4. Acceptance
One release location, documented; no untracked half-built release on disk; no prunable worktrees.
