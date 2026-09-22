# Mandatory Priority Rules for PhotoReview Application

## Maintain Work Status for the Next AI

- Maintain `task_on_progress.md` at the repository root. Read it upon starting; update it after substantial changes and prior to handoff.
- Keep entries concise: objectives, files reviewed/modified, changes made, roadblocks, remaining tasks, notes for continuation, and test/validation results. Include the update date; never assume unverified claims as facts.

## Mandatory Process Before Making Changes

The application must prioritize the following principles when processing and reviewing photos:

1. **Minimize Disk Reads:** Always minimize disk I/O as much as possible. Prioritize reusing read data, caching, and efficient sequential reading mechanisms to avoid redundant disk I/O.
2. **Maximize RAM Utilization:** Prioritize using high RAM amounts to optimize speed. The system has 32 GB of RAM, so the target usage is at least 16 GB where applicable. If the total size of the photo folder is under 16 GB, prioritize loading the entire folder into RAM, provided it does not cause errors or severely impact the system.
3. **Prioritize Review Speed:** All designs and optimizations must prioritize the speed of photo switching, image rendering, and fast review operations.
4. **Prioritize Image Quality:** Whenever possible, images must be loaded and rendered at the highest possible quality; avoid reducing quality or relying on lower-resolution previews unless strictly necessary.

## Documentation Reading Strategy (Token Diet)

**Goal:** Reduce token overhead by tiering documentation. Read only what's needed.

1. **Every session (T0, ≤12 KB):** `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md` — establishes context, constraints, and links to scope-specific docs.
2. **Per task (T1, ≤15 KB per file):** Use `INDEX.md` to find which single plan/architecture doc relates to the work. Read that file + relevant ADR. Do not pre-read all plans.
3. **Archive (T2, unlimited):** `docs/archive/` and `docs/archive/future/` are historical only. Do not read unless referenced or verifying decisions. Use `git log --follow` for change rationale.
4. **Task completion:** When a task is done and reaches status DONE, compress it to a single line (`ID · status · SHA`) in its plan, move detailed evidence/findings to archive, and reference the digest in `task_on_progress.md`.

## Mandatory Build, Publish, and Git Push Workflow

- After each completed change: commit, and push to an appropriate branch for me to review and merge.