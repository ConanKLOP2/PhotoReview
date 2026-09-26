# Mandatory Priority Rules for PhotoReview Application

## Maintain Work Status for the Next AI

- Maintain `task_on_progress.md` at the repository root. Read it upon starting; update it after substantial changes and prior to handoff.
- Keep entries concise: objectives, files reviewed/modified, changes made, roadblocks, remaining tasks, notes for continuation, and test/validation results. Include the update date; never assume unverified claims as facts.
- **Update it again at every one of these triggers, not just once per task** — a doc that says "not yet merged" after it *was* merged is worse than no doc:
  1. **A branch/PR you documented as open gets merged or closed.** Check proactively — `git fetch` before writing "not yet merged"/"in progress"; if status changes later (via `git log`, the user saying "đã merge", CI, etc.), update the doc that same turn, unprompted.
  2. **You push a commit/branch/PR that supersedes or fixes something already in the doc** (e.g. a post-merge fix) — fold it into the existing entry, don't leave the old one standing alone.
  3. **A deferred/blocked item gets unblocked or explicitly declined by the user** — move it out of "deferred" instead of leaving stale reasoning behind.
  4. **Before ending a turn where you pushed anything** — ask: "if someone reads only this file next session, will they know the *current* merge/branch state?"

## Mandatory Process Before Making Changes

The application must prioritize the following principles when processing and reviewing photos:

1. **Minimize Disk Reads:** Always minimize disk I/O as much as possible. Prioritize reusing read data, caching, and efficient sequential reading mechanisms to avoid redundant disk I/O.
2. **Maximize RAM Utilization:** Prioritize using high RAM amounts to optimize speed. The system has 32 GB of RAM, so the target usage is at least 16 GB where applicable. If the total size of the photo folder is under 16 GB, prioritize loading the entire folder into RAM, provided it does not cause errors or severely impact the system.
3. **Prioritize Review Speed:** All designs and optimizations must prioritize the speed of photo switching, image rendering, and fast review operations.

## Documentation Reading Strategy (Token Diet)

**Goal:** Reduce token overhead by tiering documentation. Read only what's needed.

1. **Every session (T0, ≤16 KB total):** `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md` — establishes context, constraints, and links to scope-specific docs.
2. **Per task (T1, ≤24 KB per file):** Use `INDEX.md` to find which single plan/architecture doc relates to the work. Read that file + relevant ADR. Do not pre-read all plans.
3. **Archive (T2, unlimited):** `docs/archive/` and `docs/archive/future/` are historical only. Do not read unless referenced or verifying decisions. Use `git log --follow` for change rationale.
4. **Task completion:** When a task is done and reaches status DONE, compress it to a single line (`ID · status · SHA`) in its plan, move detailed evidence/findings to archive, and reference the digest in `task_on_progress.md`.

## Coding Conventions (Naming & Analyzer Warnings)

**Goal:** Keep the build warning-free without fighting analyzer rules that don't fit the codebase's real conventions.

1. **Production code (`src/`, `tools/`):** Strict PascalCase for all public/internal members. No public mutable fields (`CA1051`) — use properties. New public fields need a documented decision (see [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md) ST06 for the superseded `MainWindow` exception).
2. **Test code (`tests/`):** `Method_Scenario_ExpectedResult` (underscore-separated) is accepted — do not rename tests to remove underscores. `CA1707` is suppressed for test projects via `.editorconfig`, scoped so only test-only methods are exempt.
3. **String comparisons (`CA1310`):** Always pass an explicit `StringComparison`. Use `Ordinal`/`OrdinalIgnoreCase` for file/path names (not culture-aware on Windows); `CurrentCulture` is reserved for user-facing text sorting/display only.
4. **Culture-sensitive formatting (`CA1305`):** Any `ToString`/`Parse`/`Format` writing to a log, CSV, journal, or other machine-read/diagnostic file must use `CultureInfo.InvariantCulture`, never the user's locale (app ships Vietnamese UI text). UI-facing display text may use `CurrentCulture`.
5. **Nullable warnings (`CS8603` and similar):** Fix these for real — they flag a genuine possible-null-return path, not style. Do not suppress.
6. **New analyzer suppressions:** Any new `#pragma warning disable` or `.editorconfig` rule change needs a one-line justification (architecture decision, false positive, etc.) — never suppress silently to make CI green. Release build treats warnings as errors (AR12b).

## Mandatory Build, Publish, and Git Push Workflow

- After each completed change: commit, and push to an appropriate branch for me to review and merge.
- Version is automatic (`Directory.Build.targets`): `2.0.N`, +1 per merge to master, CI tags `v2.0.N`; never hand-edit `<Version>`.
- Base every PR on `master` (no stacked PRs).

## Agent Workflow (applies on every machine)

- **Decision log:** `master` is the only source of truth for the handoff (`task_on_progress.md`), `docs/refactoring/OPEN-DECISIONS.md` and the current `WORK-*` doc — there is no `develop` branch (retired 2026-09-26; it drifted 170 commits behind and was merged once). At session start `git fetch` and read them on `origin/master`. A PR that changes project state updates them in the same PR; a decision taken outside a code PR gets its own small docs PR in the same turn. Feature/fix/docs branches all start from `origin/master`.
- **Worktrees:** the user runs several sessions on one repo — never switch branches or leave edits in the main checkout; work in `git worktree add .claude/worktrees/<name> -b <branch> origin/master`. Never use bare `git stash` (shared stack).
- **Parallel agents:** split independent work across subagents in isolated worktrees with a fixed contract (names, files, keys) agreed first; new members/i18n keys are appended at the end of shared files; the lead merges the branches (one integration PR when branches overlap). Pick the cheapest suitable agent/model: `Explore` for read-only searches, a small model for mechanical edits and doc updates, the strongest model only for risky work (concurrency, data safety, Fit/T89, integration merges).
- **Verify claims:** never report an agent's "done / N tests pass" without rebuilding (`dotnet build PhotoReview.slnx -c Release`, 0 warnings) and rerunning the gate on the merged result.
- **No stacked PRs:** after the user merges, check each PR head with `git merge-base --is-ancestor <head> origin/master`, not the MERGED label.
- **Recycle Bin:** never run code that deletes from, sweeps or empties the user's real Recycle Bin — including mutation checks of such code (2026-09-24 incident, Q-R9 declined). Use fakes; Native bin tests only remove their own items.
- **Real-machine checks:** Claude runs perf/headless checks on the user's PC itself (machine-specific fixture paths live in `work/diag/fixtures.local.json` / `CLAUDE.local.md`); only visual checks go to the user.
- **Asking the user to decide:** never ask a bare question. For every decision give (1) the current state and why it matters, (2) 2-4 options, each with detailed pros and cons (risk to user data, effort, behaviour change, what must be tested/changed), (3) one recommended option with the reason, and (4) what you will do once the user picks. Reply in Vietnamese (user rule). Record the outcome in `OPEN-DECISIONS.md`.

## Tests

- Must fail when the guarded code is broken (mutate to check). No source-text tests, fixed-delay asserts or `Task.Yield()` polling; real-OS tests are `Native`/`Slow` and self-cleaning. Local filter: `Category!=Manual&Category!=Native&Category!=Slow`.
