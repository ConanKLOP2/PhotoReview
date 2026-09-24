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

1. **Every session (T0, ≤12 KB):** `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md` — establishes context, constraints, and links to scope-specific docs.
2. **Per task (T1, ≤15 KB per file):** Use `INDEX.md` to find which single plan/architecture doc relates to the work. Read that file + relevant ADR. Do not pre-read all plans.
3. **Archive (T2, unlimited):** `docs/archive/` and `docs/archive/future/` are historical only. Do not read unless referenced or verifying decisions. Use `git log --follow` for change rationale.
4. **Task completion:** When a task is done and reaches status DONE, compress it to a single line (`ID · status · SHA`) in its plan, move detailed evidence/findings to archive, and reference the digest in `task_on_progress.md`.

## Coding Conventions (Naming & Analyzer Warnings)

**Goal:** Keep the build warning-free without fighting analyzer rules that don't fit the codebase's real conventions.

1. **Production code (`src/`, `tools/`):** Strict PascalCase for all public/internal members. No public mutable fields (`CA1051`) — use properties. New public fields need a documented decision (see [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md) ST06 for the superseded `MainWindow` exception).
2. **Test code (`tests/`):** `Method_Scenario_ExpectedResult` (underscore-separated) is accepted — do not rename tests to remove underscores. `CA1707` is suppressed for test projects via `.editorconfig`, scoped so only test-only methods are exempt.
3. **String comparisons (`CA1310`):** Always pass an explicit `StringComparison`. Use `Ordinal`/`OrdinalIgnoreCase` for file/path names (not culture-aware on Windows); `CurrentCulture` is reserved for user-facing text sorting/display only.
4. **Culture-sensitive formatting (`CA1305`):** Any `ToString`/`Parse`/`Format` writing to a log, CSV, journal, or other machine-read/diagnostic file must use `CultureInfo.InvariantCulture`, never the user's locale (app ships Vietnamese UI text). UI-facing display text may use `CurrentCulture`.
5. **Nullable warnings (`CS8603` and similar):** Fix these for real — they flag a genuine possible-null-return path, not style. Do not suppress.
6. **New analyzer suppressions:** Any new `#pragma warning disable` or `.editorconfig` rule change needs a one-line justification (architecture decision, false positive, etc.) — never suppress silently to make CI green.

## Mandatory Build, Publish, and Git Push Workflow

- After each completed change: commit, and push to an appropriate branch for me to review and merge.
- Version is automatic (`Directory.Build.targets`): `2.0.N`, +1 per merge to master, CI tags `v2.0.N`; never hand-edit `<Version>`.
- Base every PR on `master` (no stacked PRs).

## Tests

- Must fail when the guarded code is broken (mutate to check). No source-text tests, fixed-delay asserts or `Task.Yield()` polling; real-OS tests are `Native`/`Slow` and self-cleaning. Local filter: `Category!=Manual&Category!=Native&Category!=Slow`.
