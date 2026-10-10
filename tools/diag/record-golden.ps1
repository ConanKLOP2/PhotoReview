<#
.SYNOPSIS
    Re-records the NO-WPF golden files (tests/Fixtures/golden/*.v1.json) from the WPF build (WP-10).

.DESCRIPTION
    The golden files are the equivalence reference of the Win32 shell (docs/refactoring/decisions/NO-WPF-EXEC-PLAN.md section 7.3):
    G-VIEW viewport-layout, G-INPUT input-scripts, G-KEY key-names, G-MENU context-menu, G-OVL overlay-boxes. They are recorded by the
    real WPF MainWindow (never shown: laid out at fixed client sizes), on the private hidden desktop of tools/run-tests-hidden.ps1,
    with PHOTOREVIEW_GOLDEN_RECORD=1 (the recorders refuse to write without it). Output is deterministic (no timestamp, no machine
    name): running it twice on the same build must leave `git diff` empty. A non-empty diff after a WPF change is the behaviour change
    to review; never re-record to "make the conformance test green" without understanding it.

    Conditions: any DPI/monitor (DPI is emulated per case), no window is shown. Overlay boxes (G-OVL) depend on the recording
    machine's Segoe UI / Segoe UI Emoji; conformance allows +/- 2 DIP.

.PARAMETER NoBuild
    Skip `dotnet build` (use the existing Release build of the two test projects).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/diag/record-golden.ps1
#>
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repo

$integration = 'tests/PhotoReview.Integration.Tests/PhotoReview.Integration.Tests.csproj'
$appTests = 'tests/PhotoReview.App.Tests/PhotoReview.App.Tests.csproj'

if (-not $NoBuild) {
    foreach ($project in @($integration, $appTests)) {
        & dotnet build $project -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
    }
}

$env:PHOTOREVIEW_GOLDEN_RECORD = '1'
$env:PHOTOREVIEW_DIAG_INSTANCE_LABEL = 'AGENT CHECK'
$guard = @('--blame-hang', '--blame-hang-timeout', '120s', '--blame-hang-dump-type', 'none')
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\run-tests-hidden.ps1') `
        $integration -c Release --no-build --filter 'FullyQualifiedName~GoldenRecordTests' @guard
    if ($LASTEXITCODE -ne 0) { throw 'recording viewport/input/menu/overlay golden failed' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\run-tests-hidden.ps1') `
        $appTests -c Release --no-build --filter 'FullyQualifiedName~KeyNameGoldenTests.RecordKeyNames' @guard
    if ($LASTEXITCODE -ne 0) { throw 'recording key-names golden failed' }
}
finally {
    Remove-Item Env:\PHOTOREVIEW_GOLDEN_RECORD -ErrorAction SilentlyContinue
}

git -C $repo status --short -- tests/Fixtures/golden
