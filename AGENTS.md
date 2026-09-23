# Mandatory Priority Rules for PhotoReview Application

## Maintain Work Status for the Next AI

- Maintain `task_on_progress.md` at the repository root. Read it upon starting; update it after substantial changes and prior to handoff.
- Keep entries concise: objectives, files reviewed/modified, changes made, roadblocks, remaining tasks, notes for continuation, and test/validation results. Include the update date; never assume unverified claims as facts.
- **Update it again at every one of these triggers, not just once per task** — a doc that says "not yet merged" after it *was* merged is worse than no doc:
  1. **A branch/PR you documented as open gets merged or closed.** Check this proactively — before writing "not yet merged"/"in progress" into the doc, `git fetch` and confirm it's still true; if you later learn (via `git log`, the user saying "đã merge", a CI notification, etc.) that status changed, update the doc in that same turn, don't wait to be asked to "review" or "rà soát".
  2. **You push a new commit/branch/PR that supersedes or fixes something already described in the doc** (e.g. a post-merge fix) — fold it into the existing entry instead of leaving the old entry standing alone.
  3. **A deferred/blocked item gets unblocked or explicitly declined by the user** — move it out of "deferred" instead of leaving stale reasoning behind.
  4. **Before ending a turn where you pushed anything** — re-read what you're about to write and ask: "if someone reads only this file next session, will they know the *current* merge/branch state, not the state as of when I started this turn?"

## Mandatory Process Before Making Changes

The application must prioritize the following principles when processing and reviewing photos:

1. **Minimize Disk Reads:** Always minimize disk I/O as much as possible. Prioritize reusing read data, caching, and efficient sequential reading mechanisms to avoid redundant disk I/O.
2. **Maximize RAM Utilization:** Prioritize using high RAM amounts to optimize speed. The system has 32 GB of RAM, so the target usage is at least 16 GB where applicable. If the total size of the photo folder is under 16 GB, prioritize loading the entire folder into RAM, provided it does not cause errors or severely impact the system.
3. **Prioritize Review Speed:** All designs and optimizations must prioritize the speed of photo switching, image rendering, and fast review operations.

## Documentation Reading Strategy (Token Diet)

**Goal:** Reduce token overhead by tiering documentation. Read only what's needed.

1. **Every session (T0, ≤12 KB):** `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md` — establishes context, constraints, and links to scope-specific docs.
2. **Per task (T1, ≤15 KB per file):** Use `INDEX.md` to find which single plan/architecture doc relates to the work. Read that file + relevant ADR. Do not pre-read all plans.
3. **Archive (T2, unlimited):** `docs/archive/` and `docs/archive/future/` are historical only. Do not read unless referenced or verifying decisions. Use `git log --follow` for change rationale.
4. **Task completion:** When a task is done and reaches status DONE, compress it to a single line (`ID · status · SHA`) in its plan, move detailed evidence/findings to archive, and reference the digest in `task_on_progress.md`.

## Coding Conventions (Naming & Analyzer Warnings)

**Goal:** Keep the build warning-free without fighting analyzer rules that don't fit the codebase's real conventions.

1. **Production code (`src/`, `tools/`):** Strict PascalCase for all public/internal members. No public mutable fields (`CA1051`) — use properties. **Exception:** `MainWindow.xaml.cs` fields (`_files`, `_index`, etc.) are `public` by deliberate ST06/Q-ST3 decision (avoids test reflection); do not "fix" these by renaming — see [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md). New public fields elsewhere are not permitted without the same kind of documented decision.
2. **Test code (`tests/`):** `Method_Scenario_ExpectedResult` (underscore-separated) is the accepted naming convention — do not rename tests to remove underscores. `CA1707` is suppressed for test projects via `.editorconfig` (`dotnet_code_quality.CA1707.api_surface = public`, scoped so test assemblies' internal/test-only methods aren't flagged).
3. **String comparisons (`CA1310`):** Always pass an explicit `StringComparison`. Use `Ordinal`/`OrdinalIgnoreCase` for file paths and file names (Windows path comparison is not culture-aware); `CurrentCulture`-based comparison is reserved for user-facing text sorting/display only.
4. **Culture-sensitive formatting (`CA1305`):** Any `ToString`/`Parse`/`Format` that writes to a log, CSV, journal, or other machine-read/diagnostic file must use `CultureInfo.InvariantCulture` — never the user's locale (this app ships with Vietnamese UI text; do not let a Vietnamese locale reformat numbers/dates in diagnostic output). UI-facing display text may use `CurrentCulture`.
5. **Nullable warnings (`CS8603` and similar):** Fix these for real — they flag a genuine possible-null-return path, not a style preference. Do not suppress.
6. **New analyzer suppressions:** Any new `#pragma warning disable` or `.editorconfig` rule change must include a one-line justification comment/commit message citing the reason (architecture decision, false positive, etc.) — never suppress silently to make CI green.

## Mandatory Build, Publish, and Git Push Workflow

- After each completed change: commit, and push to an appropriate branch for me to review and merge.