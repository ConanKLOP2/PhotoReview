---
id: SEC-01
order: 22
summary: |-
  A relative Move/Copy destination through an existing junction/symlink could resolve outside the photo folder; `ActionDestinationPolicy`/`FileActionService` now also check the resolved (reparse-point-followed) path, via a new `IFileSystem.ResolveRealPath` seam.
---

# SEC-01 — relative Move/Copy destination could escape the photo folder through an existing junction/symlink

## Finding (external static review, verified 2026-09-27)

`ActionDestinationPolicy.Validate` (`src/PhotoReview.Core/FileActions/ActionDestinationPolicy.cs`, ~line
27-50) and the run-time containment check in `FileActionService.ExecuteAsync`
(`src/PhotoReview.Core/FileActions/FileActionService.cs`, previously ~line 91-110) both only inspect the
destination **as a string**: no `..` segment, not rooted, and (in `FileActionService`) the lexically
combined, `Path.GetFullPath`-normalized destination folder must start with the source folder. None of
those checks touch the real filesystem, so none of them notice when an **intermediate directory that
already exists on disk is a reparse point (NTFS junction or directory symlink)** whose real target is
outside the photo folder.

Concrete repro: photo folder `C:\photos` contains a child directory `link` that is a junction pointing at
`C:\outside` (created with `mklink /J` or `Directory.CreateSymbolicLink`, no special privilege needed for a
junction). A user or a Move/Copy action profile with the relative destination `link\processed`:

- `ActionDestinationPolicy.Validate("link\processed")` → `Ok` (no `..`, not rooted).
- `FileActionService`'s lexical check: `Path.GetFullPath("C:\photos\link\processed")` = `C:\photos\link\processed`,
  which lexically starts with `C:\photos\` → also passes.
- The actual `IFileSystem.Move`/`Copy` call, however, is a real Win32 file operation: Windows transparently
  follows the `link` junction while traversing the path, so the file physically lands in
  `C:\outside\processed\`, not anywhere under the photo folder.

The lexical-containment guarantee `ActionDestinationPolicy`/`FileActionService` exist to provide (Q-R1..R6,
"relative action destinations stay inside the photo folder") is bypassed for any destination that passes
through an existing reparse point, whether created by the user unknowingly (e.g. a synced-folder tool that
leaves junctions behind) or planted intentionally.

## Fix

Containment now also holds after **resolving every reparse point that already exists** along the
destination path — including the final segment (the destination file/folder itself), so a destination that
is itself a symlink is caught the same way.

- **New seam**: `IFileSystem.ResolveRealPath(string path)` (default interface method, returns
  `Path.GetFullPath(path)` unchanged — a fake with no real symlinks is a no-op). `PhysicalFileSystem`
  overrides it: walks `path` one segment at a time from its root and, whenever an existing segment is a
  reparse point (`FileSystemInfo.LinkTarget is not null`), replaces it with
  `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`'s final target before continuing — a segment
  that does not exist yet is kept as-is (nothing to resolve; an as-yet-nonexistent junction cannot redirect
  anything). `CountingFileSystem`/`SlowLinkFileSystem` (the two other `IFileSystem` decorators) forward it to
  their inner instance like every other method.
- **New policy method**: `ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(sourceFolder,
  destinationFolderOrPath, IFileSystem)` resolves both sides with `ResolveRealPath` and checks the resolved
  destination is still the resolved source folder or a descendant of it. It reuses the existing
  `ActionDestinationCheck.EscapesSourceFolder` value — no new enum member needed, the meaning ("this
  destination would not stay inside the photo folder") already fits exactly.
- **Wired into `FileActionService.ExecuteAsync`**, right after `destinationPath` is computed and before the
  existing "destination already exists" check, **only for a relative (non-rooted) destination** — an
  absolute destination (e.g. `D:\Backup`) is an explicit user choice that is allowed to leave the photo
  folder by design (unchanged behavior, see `ActionDestinationPolicy.Validate`'s own doc comment). On
  rejection it throws the same `JournalCodedException(JournalErrors.DestinationOutsideSource)` an ordinary
  lexical escape throws, so nothing is journaled and no mutation happens (same guarantee as before, just
  covering the reparse-point case too).
- **Test seam**: `InMemoryFileSystem.AddReparsePoint(path, target)` (new, in
  `tests/PhotoReview.Core.Tests/Fakes/InMemoryFileSystem.cs`) registers a simulated reparse point so
  fast (`HotPath`) tests can exercise the escape check without touching the real OS; it overrides
  `ResolveRealPath` to substitute the registered target while walking the path, exactly like
  `PhysicalFileSystem` does for a real one.

## Test coverage

- `tests/PhotoReview.Core.Tests/FileActions/ActionDestinationPolicyTests.cs`: four new
  `HotPath` unit tests directly against `ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint` using
  `InMemoryFileSystem.AddReparsePoint` — junction escaping the folder (rejected), junction resolving back
  inside the folder (allowed), ordinary destination with no reparse points (unaffected), and a destination
  *file* itself registered as a reparse point pointing outside (rejected).
- `tests/PhotoReview.Core.Tests/FileActions/FileActionServiceTests.cs`: two new end-to-end tests through
  `FileActionService.ExecuteAsync` (Move and Copy) reproducing the exact repro above with a simulated
  junction, plus one proving a junction resolving back inside the folder still succeeds. Existing tests in
  the same file (`ExecuteAsync_RelativeDestinationInsideSource_Succeeds`,
  `ExecuteAsync_AbsoluteDestinationOutsideSource_StaysAllowed`, `ExecuteAsync_Move_Success`, and the
  pre-existing lexical-escape tests) continue to cover ordinary relative/absolute destinations with no
  regression.
- `tests/PhotoReview.Core.Tests/FileActions/SymlinkDestinationEscapeNativeTests.cs` (new, `Category=Native`,
  excluded from the default local/CI filter): the same three scenarios end-to-end against the real
  `PhysicalFileSystem`, using a **real NTFS junction** created via `cmd /c mklink /J` (junctions need no
  special privilege, unlike `Directory.CreateSymbolicLink`, which needs `SeCreateSymbolicLinkPrivilege` —
  not available on this machine without elevation/Developer Mode, confirmed by hand). If junction creation
  ever fails on some machine, each test skips itself gracefully (early return, same pattern as
  `RealPhotosManualTests` — xUnit 2.9.3 has no `Assert.Skip`).

## Mutation check

Temporarily reverted the `FileActionService.ExecuteAsync` call to
`ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint` (kept everything else) and reran:

- `FileActionServiceTests` SEC-01 tests: the two escape tests (Move, Copy) failed as expected
  (`Assert.False(result.Succeeded)` got `True`); the "stays inside" test still passed (correct — it isn't
  supposed to fail either way).
- `SymlinkDestinationEscapeNativeTests` (real junction, run explicitly): the escape test failed
  (1 failed / 2 passed) the same way.

Re-applied the fix; both suites went back to fully green. This confirms the new tests actually exercise the
guarded code and are not vacuously passing.

## Verification

- `dotnet build PhotoReview.slnx -c Release` — 0 warnings, 0 errors.
- `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Slow"`
  — all 5 projects passed (Architecture 63/63, Imaging 578/578, App.Tests 1144/1144, Core.Tests 1611/1611,
  Integration.Tests 605/605).
- `SymlinkDestinationEscapeNativeTests` (`Category=Native`) run explicitly: 3/3 passed, real junctions
  actually created and exercised on this machine (confirmed non-trivial wall time vs. a vacuous skip, and
  the mutation check above). Left excluded from the default gate per the repo's `Native` convention.
- `tests/PhotoReview.Architecture.Tests/core-filesystem-boundary-allowlist.txt`: bumped
  `src/PhotoReview.Core/IO/PhysicalFileSystem.cs` from 14 to 16 (the two new `Directory.Exists`/`File.Exists`
  calls inside `ResolveRealPath`'s reparse-point walk) — `PhysicalFileSystem` is the one designated
  `IFileSystem` implementation, exactly the file that list already carves out for direct `File.`/`Directory.`
  calls.

## Scope not touched

`ActionDestinationPolicy.ValidatePickedFolder` ("Move to… / Copy to…" folder picker) already requires the
destination to be an absolute, pre-existing, user-picked folder (`Path.IsPathFullyQualified` +
`directoryExists`); it was never subject to the relative-destination lexical check this bug was in, so it
is out of scope here (an absolute destination is an explicit user choice, allowed to be anywhere, symlinked
or not — unchanged before and after this fix).
