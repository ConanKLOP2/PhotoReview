---
id: TEST-HANG-GUARD
order: 17
summary: |-
  `tests/test.runsettings` (wired in via `tests/Directory.Build.props`) bounds every `dotnet test` to a 120 s per-test hang timeout and a 20 min session timeout with no CLI flags required; CI/verify-all.ps1's own `--blame-hang*` flags coexist without a duplicate-collector error (2026-09-27).
---

# TEST-HANG-GUARD — repo-wide hang guard for `dotnet test`

## Problem

CI (`.github/workflows/ci.yml`) and `tools/verify-all.ps1` both always pass
`--blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none` to every `dotnet test`
invocation. A raw `dotnet test PhotoReview.slnx ...` — the command agents run day to day per
AGENTS.md "Quick Checks", and the only command available before a project is even built through
`verify-all.ps1` — has no such guard. On 2026-09-27 a deadlocked UI test hung a raw `dotnet test`
for 80+ minutes before it was noticed and killed by hand.

## Decision

Add a repo-wide guard that applies automatically to every `dotnet test` invocation under `tests/`,
with no CLI flags required:

- `tests/test.runsettings` sets `RunConfiguration/TestSessionTimeout` to 1,200,000 ms (20 minutes)
  and configures the `blame` data collector with `CollectDumpOnTestSessionHang TestTimeout="120s"
  HangDumpType="None"` — the same 120 s per-test hang timeout CI/verify-all.ps1 already use on the
  command line, just also active with zero flags.
- `tests/Directory.Build.props` imports the root `Directory.Build.props` (so every project under
  `tests/` keeps inheriting the shared analyzer/nullable/`TreatWarningsAsErrors` settings) and adds
  `<RunSettingsFilePath>$(MSBuildThisFileDirectory)test.runsettings</RunSettingsFilePath>`. MSBuild
  applies `Directory.Build.props` files found in every ancestor directory automatically, so every
  `.csproj` under `tests/` (all 7 projects, including `PhotoReview.TestSupport`/`.Windows` which are
  not runnable test projects themselves but pick up the harmless property too) gets the setting with
  no per-project edits.
- `RunSettingsFilePath` is the MSBuild property the .NET SDK's `Microsoft.TestPlatform.targets`
  falls back to for `VSTestSetting` (the equivalent of the CLI `--settings` flag) when no explicit
  `--settings`/`-s` is passed — confirmed by reading
  `Microsoft.TestPlatform.targets` in the installed SDK (10.0.401): `VSTestSetting="$([MSBuild]::ValueOrDefault($(VSTestSetting), '$(RunSettingsFilePath)'))"`.

## Interaction with CI/verify-all.ps1's own `--blame-hang*` flags

CI and `tools/verify-all.ps1` keep passing their own `--blame-hang --blame-hang-timeout 120s
--blame-hang-dump-type none` flags unchanged — they are not removed, and nothing needed to change
there. Verified empirically on this machine that combining both does **not** raise a
duplicate-data-collector error:

```
dotnet test tests/PhotoReview.Core.Tests/PhotoReview.Core.Tests.csproj -c Release --no-build \
  --filter "Category!=Manual&Category!=Native&Category!=Slow" \
  --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none
```

ran clean (`Passed! - Failed: 0, Passed: 1598, ... `), and a run with **no** blame flags at all
(relying purely on `tests/test.runsettings`) still printed `Data collector 'Blame' message: ...`,
confirming the collector was active either way. VSTest's CLI blame flags and a runsettings-provided
`blame` `DataCollector` both end up configuring the same collector instance rather than registering
two competing ones, so there is a single effective configuration, not a conflict. Net effect: CI and
`verify-all.ps1` are unaffected (same behavior as before), and a bare `dotnet test` with zero flags
now gets the guard it previously lacked. One source of truth for the *values* (120 s per-test / 20
min session) is `tests/test.runsettings`; CI/verify-all.ps1's own flags happen to use the same 120 s
value already, so nothing there needed to change.

## Proof (throwaway test, not committed)

Added a throwaway test to `tests/PhotoReview.Core.Tests` that blocks forever:

```csharp
public class ScratchHangProbeTests
{
    [Fact]
    public void Scratch_DeliberateHang_ForHangGuardProof()
    {
        new ManualResetEventSlim().Wait();
    }
}
```

Ran with **no** CLI hang flags at all:

```
dotnet test tests/PhotoReview.Core.Tests/PhotoReview.Core.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~ScratchHangProbeTests"
```

Result: aborted after 2 minutes 4 seconds (elapsed, includes ~4s test-host startup/shutdown
overhead around the 120 s inactivity window) with:

```
dotnet : The active test run was aborted. Reason: Test host process crashed
Data collector 'Blame' message: The specified inactivity time of 2 minutes has elapsed.
Collecting hang dumps from testhost and its child processes.
Test Run Aborted.
Attachments:
  .../TestResults/<guid>/Sequence_<hash>.xml
```

and the generated `Sequence_*.xml` named the exact hung test:

```xml
<Test Name="PhotoReview.Core.Tests.ScratchHangProbeTests.Scratch_DeliberateHang_ForHangGuardProof"
      Completed="False" />
```

The scratch test file and its `TestResults`/hang-dump artifacts were deleted afterward and never
committed or pushed; the assembly was rebuilt clean before the real verification run below.

## Session-timeout sizing (20 minutes)

Measured wall-clock on this machine (Release, `--no-build`, default filter
`Category!=Manual&Category!=Native&Category!=Slow`, machine also under load from other concurrent
agent sessions at the time — see caveat below):

| Project | Solo run | In the combined `dotnet test PhotoReview.slnx` run (5 projects, machine under load) |
|---|---|---|
| PhotoReview.Architecture.Tests | a few seconds | 5 s |
| PhotoReview.Imaging.Tests | ~16-36 s | 36 s |
| PhotoReview.App.Tests | ~23 s | 1 m 3 s |
| PhotoReview.Core.Tests | ~23-40 s | 1 m 5 s |
| PhotoReview.Integration.Tests (slowest normal project; real WPF/UI tests) | ~49-70 s | 1 m 39 s |

`dotnet test PhotoReview.slnx` runs all 5 test projects concurrently by default (separate
`testhost` processes), so per-project wall time is inflated versus running a project alone — the
whole combined run still finished in well under 2 minutes. Including `Category=Slow` for
`PhotoReview.Integration.Tests` alone (the project with the most `Slow`-tagged tests) still finished
in ~1 minute. Given all of that, 20 minutes (1,200,000 ms) per `dotnet test` *session* is generous —
more than 10x any observed single-project or combined-solution run — while still bounding a session
that doesn't hang on any individual test (covered by the 120 s blame timeout) but somehow never
terminates (e.g. a livelock spanning many tests). `tools/verify-all.ps1 -Slow`/`-Native` still invoke
`dotnet test` once per project (not one big session across all Slow/Native tests at once), so they
are not expected to approach the 20-minute cap either; if a future Slow/Native run ever needs more
headroom, override per-invocation on the command line with `-- RunConfiguration.TestSessionTimeout=<ms>`
rather than raising the shared default.

**Caveat:** this machine was running other agents' test suites concurrently while these
measurements were taken (per AGENTS.md, this repo is routinely worked on by several parallel
sessions), so the "in the combined run" timings above are somewhat inflated versus a quiet machine
and should not be read as tight per-project budgets — they only need to demonstrate that 20 minutes
has large headroom, which they do.

## Verification

- `dotnet build PhotoReview.slnx -c Release` — 0 warnings, 0 errors.
- `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Slow"`
  — all 5 projects passed (Architecture 63/63, Imaging 577/577, App.Tests 1138/1138, Core.Tests
  1598/1598, Integration.Tests 597/597); the known flaky
  `FolderLoadCoordinatorTests.LoadAsync_SecondFolderWhileFirstScanBlocked_*` test did not fail on
  this run.
