---
id: SEC-03
order: 22
summary: |-
  Publish-guard containment made component-aware (path segments, not a raw string prefix) and resolves reparse points before checking approval, with a new `-SelfTest` gate in `verify-all.ps1`.
---

# SEC-03 — harden the publish-directory ownership guard

## Problem

`tools/Publish-Guard.ps1`'s `Assert-ReleaseDirectoryOwned` guards the destructive recursive delete in
`tools/verify-all.ps1`'s `Publish-ReleaseDirectory` (TOOL-02): before `Remove-Item -Recurse -Force` runs
on `-ReleaseDirectory`, the function must confirm the target is either under an approved output root
(`src\PhotoReview.App\bin`, `outputs\release`) or carries a `.photoreview-publish` marker left by a
previous publish, and its leaf name is `publish` or `PhotoReview-self-contained`.

A 2026-09-27 quality review flagged two theoretical weaknesses in the containment check, even though
this is a local dev/build-tooling script (not exposed to untrusted input from photo folders, so
real-world risk is lower than a production data-safety bug):

1. Containment used a raw string prefix
   (`$full.StartsWith($rootFull + $sep, OrdinalIgnoreCase)`). A shared string prefix between two
   sibling directory names (`C:\approved-root\publish` vs `C:\approved-root2\publish`) cannot actually
   fool this today because `$sep` is appended to `$rootFull` before the `StartsWith` check, so the next
   character after the shared prefix must be a separator — `approved-root2\...` fails that. Still,
   a raw string comparison is fragile against Unicode-normalization or other lexical quirks, so
   component-by-component (path-segment) comparison is more robust defensively.
2. The check never resolved reparse points (NTFS junctions/symlinks). A junction or symlink named
   `publish` placed at or under an approved root, but pointing at an arbitrary real directory outside
   any approved root, would pass the old containment check (it only inspected the link's own path
   string) even though the actual filesystem delete follows the link to the real, unapproved target.

## Decision

Hardened `Assert-ReleaseDirectoryOwned` in `tools/Publish-Guard.ps1`:

- **Component-aware containment** (`Get-PathSegments` / `Test-PathUnderRoot`): both the candidate path
  and each approved root are split into path segments (trimming trailing separators first) and compared
  segment-by-segment with `OrdinalIgnoreCase`; the candidate must have strictly more segments than the
  root (equal to the root does not count as "under" it). Replaces the raw `StartsWith` prefix check.
- **Reparse-point resolution** (`Resolve-FinalTarget`): before the containment check, the target
  directory is walked through up to 32 hops of `Get-Item -Force` / `.LinkType` / `.Target` to reach its
  real final directory (looped-link detection included), and containment is checked against that
  resolved path, not the original (possibly linked) one. Filesystem I/O elsewhere (`Test-Path`,
  `Remove-Item`) already follows reparse points transparently, so this only changes the *decision*, not
  actual disk access. The marker-file check is left reading through the original path — the OS already
  follows the same link when it opens that file, so it inspects the same real location.
- No change to the function's contract: same parameters, same behavior for every case that already
  passed (approved root, marker-carrying directory, bad leaf name, nonexistent target).

## Tests

No existing PowerShell test harness (`.Tests.ps1` / Pester) in `tools/`; the established local
convention for this kind of self-contained script logic is an in-memory `-SelfTest` switch dot-sourced
by `verify-all.ps1` as its own gate (see `tools/i18n-check.ps1 -SelfTest` for the strict-JSON validator).
Followed the same pattern:

- `tools/Publish-Guard.ps1 -SelfTest` runs `Invoke-PublishGuardSelfTest`, which builds a scratch temp
  directory (`%TEMP%\PublishGuardSelfTest-<guid>`), exercises `Assert-ReleaseDirectoryOwned` against
  approved/sibling/case-variant/trailing-separator/marker/bad-leaf/nonexistent/junction fixtures, and
  always deletes the temp directory in a `finally` block. It never calls `Remove-Item` on anything the
  guard itself would refuse, and it never touches the real release output.
- Covers: exact approved-root match, a sibling root with a shared string prefix (`approved-root2` vs
  `approved-root`), a case-variant path, a target and/or root with a trailing separator already present,
  an unapproved directory with no marker (refused), an unapproved directory with a marker (allowed), a
  directory whose leaf name isn't `publish`/`PhotoReview-self-contained` (refused), a nonexistent target
  (allowed — nothing to delete), and a directory junction placed under the approved root but pointing
  outside it (refused). The junction case creates an NTFS directory junction, which needs no admin
  rights/Developer Mode (unlike a symlink); if junction creation ever fails in some environment, that one
  case is skipped with a message rather than failing the whole self-test.
- Wired into `tools/verify-all.ps1` as a new gate, `Check publish-guard ownership check (self-test)`,
  placed immediately before the `Publish framework-dependent release` gate that actually calls
  `Assert-ReleaseDirectoryOwned` for real.

## Verification

- `dotnet build PhotoReview.slnx -c Release` — 0 warnings/errors (script-only change, no C# touched).
- `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Slow"` — unaffected (no C# touched).
- `pwsh ./tools/Publish-Guard.ps1 -SelfTest` — all cases pass, including the new junction case.
- `tools/verify-all.ps1` run end-to-end once locally to confirm the new gate and the existing publish/
  wipe/verify-release gates still pass.
