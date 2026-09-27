---
id: CI-TAG-VERSION-BATCH-PUSH
order: 26
summary: |-
  `tag-version`'s single batched `git push origin "${created[@]}"` failed the whole job on every run because of one permanently un-pushable historical tag (v2.0.179) -- switched to pushing each tag individually and treating that one known GitHub restriction as an expected, logged skip instead of a job failure.
---

# CI-TAG-VERSION-BATCH-PUSH — one un-pushable historical tag failed `tag-version` every run

## Symptom

The `tag-version` job's "Create version tags (current + any missed)" step failed on essentially every
`master` run (e.g. runs 36334213322, 36326858187), even when the current run's own tag (e.g. `v2.0.217`)
was created and would have pushed fine.

## Cause

The backfill loop creates every missing historical tag locally, then pushes them all in one command:
`git push origin "${created[@]}"`. One historical tag, `v2.0.179`, points at a commit that itself modified
`.github/workflows/ci.yml`. GitHub's default `GITHUB_TOKEN` is hard-blocked at the platform level (not a
`permissions:` setting that can be granted) from creating or updating any ref whose target commit touches a
workflow file, to prevent workflow-permission escalation. `git push` with multiple refspecs is atomic-per-ref
but reports failure for the whole invocation (`error: failed to push some refs`) if any one ref is rejected,
so this single always-rejected tag failed the job every single run.

## Fix

`.github/workflows/ci.yml`, `tag-version` > "Create version tags (current + any missed)":

- Push each newly created tag individually in a loop instead of one batched `git push origin "${created[@]}"`,
  so one rejected tag can no longer block the others.
- On an individual push failure, detect the known-impossible case two ways (either is sufficient): grep the
  push's stderr for GitHub's actual rejection wording (`workflow`/`refusing to allow`), or check whether the
  tag's target commit touched `.github/workflows/` via `git diff-tree --no-commit-id --name-only -r <sha> --
  .github/workflows`. When detected, log it as an expected, permanently-skippable condition (`::warning::` +
  a `$GITHUB_STEP_SUMMARY` line) and continue; the job's exit code stays 0 as long as every failure is of this
  kind. Any other push failure (network, auth, etc.) still increments a failure counter and fails the job
  (`exit 1`) with `::error::`, so a genuine problem is never swallowed.
- The local tag is **not** deleted after a known-impossible push failure. This doesn't actually matter for
  future runs either way: the job always starts from a fresh `actions/checkout`, so nothing from this run's
  local tag state carries forward. `v2.0.179` will be recreated locally and its push retried (and skipped
  with the same warning) on every future `master` run, forever -- that repetition is accepted as the
  cheapest option per the task's own framing (a one-line warning per run is harmless noise, not a failure).
  A persistent skip-list to silence the recurring warning would need state stored outside the ephemeral
  checkout (e.g. a repo file); not worth it for one line of log output.

## Verification

- Local dry-run: extracted the loop's logic into a standalone script run against a scratch git repo, with a
  `git` wrapper on `PATH` that simulates GitHub's real rejection message for a push whose target commit
  touches `.github/workflows/`, and (separately) an unrelated forced failure for another tag. Confirmed: (1)
  a mix of 3 pushable tags + 1 permanently-rejected tag pushes the 3, warns and skips the 1, exits 0; (2) an
  unrelated forced failure on one tag still exits non-zero with an `::error::` line, alongside the expected
  skip for the workflow-touching tag.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings/errors (CI-only change, no C# touched).
- `tools/check-doc-links.ps1`, `tools/docs-budget.ps1 -Check`: pass.
