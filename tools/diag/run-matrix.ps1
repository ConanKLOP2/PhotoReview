<#
.SYNOPSIS
  D06: runs the perf-session scenario matrix (scenario x mode x condition x repeat), one fresh process per run.

.DESCRIPTION
  Every run is `dotnet run --project PhotoReview.Tests -c Release --no-build -- --perf-session ...` in a new
  process. The driver only uses in-process WPF routed events (no OS-level input, no foreground changes; see
  docs/refactoring/diagnosis/perf-session.md). Conditions:
    cold-app       new process, caches left as they are
    cold-diskcache delete %LOCALAPPDATA%\PhotoReview\cache\*.png and thumbnails\*.png before every run
    warm           one unrecorded warm-up run for the cell, then the recorded runs
    cold-os        NOT automated: the script stops and prints what the user must do (reboot or RAMMap).
                   After doing it, re-run with -Conditions cold-os -ColdOsConfirmed (runs once per cell).
  Writes <OutRoot>\<stamp>\matrix.json (cells, status, durations) after every run. Run data is never committed.

.EXAMPLE
  .\tools\diag\run-matrix.ps1 -Scenarios s2-next-slow -Modes Fast,Preview,Original -Conditions warm -Repeat 3 -FixtureAlias F1
#>
[CmdletBinding()]
param(
    [string[]]$Scenarios = @('s1-open-folder', 's1b-open-file', 's2-next-slow', 's3-next-burst', 's4-jump'),
    [string[]]$Modes = @('Preview'),
    [string[]]$Conditions = @('warm'),
    [ValidateRange(1, 100)]
    [int]$Repeat = 3,
    [string[]]$FixtureAlias = @('F1'),
    [string]$OutRoot,
    [string]$FixturesFile,
    [switch]$ColdOsConfirmed,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# `powershell -File` passes "a,b" as one string; accept both forms.
function Split-List([string[]]$values) { @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Scenarios = Split-List $Scenarios
$Modes = Split-List $Modes
$Conditions = Split-List $Conditions
$FixtureAlias = Split-List $FixtureAlias
foreach ($m in $Modes) { if ($m -notin 'Fast', 'Preview', 'Original') { throw "Invalid mode '$m' (Fast|Preview|Original)" } }
foreach ($c in $Conditions) { if ($c -notin 'cold-app', 'cold-diskcache', 'warm', 'cold-os') { throw "Invalid condition '$c' (cold-app|cold-diskcache|warm|cold-os)" } }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scenarioDir = Join-Path $PSScriptRoot 'scenarios'
$testsProject = Join-Path $repoRoot 'tests\PhotoReview.Tests\PhotoReview.Tests.csproj'

# work\ is git-ignored and lives in the main checkout; agent worktrees fall back to it.
function Resolve-WorkDir {
    $local = Join-Path $repoRoot 'work'
    if (Test-Path -LiteralPath $local) { return $local }
    $common = (& git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $common) {
        $main = Join-Path (Split-Path -Parent $common) 'work'
        if (Test-Path -LiteralPath $main) { return $main }
    }
    return $local
}
$workDir = Resolve-WorkDir
if (-not $OutRoot) { $OutRoot = Join-Path $workDir 'diag\runs' }
if (-not $FixturesFile) { $FixturesFile = Join-Path $workDir 'diag\fixtures.local.json' }

if ($Conditions -contains 'cold-os' -and -not $ColdOsConfirmed) {
    Write-Host ''
    Write-Host 'cold-os requires emptying the OS file cache. This script will NOT do it (needs admin).' -ForegroundColor Yellow
    Write-Host 'Do ONE of the following yourself, then re-run with -ColdOsConfirmed:'
    Write-Host '  1. Reboot, log in, wait ~2 minutes for startup I/O to settle.'
    Write-Host '  2. Sysinternals RAMMap (as administrator): Empty > Empty Standby List.'
    Write-Host 'Run only one cold-os cell per reset (use -Repeat 1 and one scenario/mode), because the first run warms the cache again.'
    exit 3
}

# fixtures.local.json may contain unescaped backslashes (e.g. C:\photos\...); retry with them escaped.
function Read-Fixtures([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Fixture file not found: $path" }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    try { return $raw | ConvertFrom-Json }
    catch { return ($raw -replace '(?<!\\)\\(?![\\"])', '\\') | ConvertFrom-Json }
}
$fixtures = Read-Fixtures $FixturesFile

$scenarioFiles = foreach ($s in $Scenarios) {
    $candidate = if (Test-Path -LiteralPath $s) { $s } else { Join-Path $scenarioDir (($s -replace '\.json$', '') + '.json') }
    if (-not (Test-Path -LiteralPath $candidate)) { throw "Scenario not found: $s" }
    (Resolve-Path -LiteralPath $candidate).Path
}
$folders = @{}
foreach ($alias in $FixtureAlias) {
    $entry = $fixtures.$alias
    if (-not $entry -or -not $entry.path) { throw "Fixture alias '$alias' has no path in $FixturesFile" }
    if (-not (Test-Path -LiteralPath $entry.path)) { throw "Fixture '$alias' folder does not exist" }
    $folders[$alias] = $entry.path
}

$commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if (-not $SkipBuild) {
    Write-Host "Building $testsProject (Release)..."
    & dotnet build $testsProject -c Release -nologo -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$batchDir = Join-Path $OutRoot $stamp
New-Item -ItemType Directory -Force -Path $batchDir | Out-Null
$matrixPath = Join-Path $batchDir 'matrix.json'
$cells = New-Object System.Collections.Generic.List[object]
$localApp = Join-Path $env:LOCALAPPDATA 'PhotoReview'

function Save-Matrix {
    $doc = [ordered]@{
        created = $stamp; commit = $commit; repeat = $Repeat
        scenarios = $Scenarios; modes = $Modes; conditions = $Conditions; fixtures = $FixtureAlias
        diagEnv = @(Get-ChildItem Env: | Where-Object Name -like 'PHOTOREVIEW_DIAG_*' | ForEach-Object { "$($_.Name)=$($_.Value)" })
        cells = $cells
    }
    [System.IO.File]::WriteAllText($matrixPath, ($doc | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}

function Clear-DiskCache {
    foreach ($sub in 'cache', 'thumbnails') {
        $dir = Join-Path $localApp $sub
        if (Test-Path -LiteralPath $dir) { Get-ChildItem -LiteralPath $dir -Filter '*.png' -File | Remove-Item -Force }
    }
}

function Invoke-Session([string]$scenario, [string]$folder, [string]$outDir, [string]$mode, [string]$alias) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # native stderr must not become a terminating error (PS 5.1)
    try {
        $output = & dotnet run --project $testsProject -c Release --no-build -- --perf-session $scenario $folder $outDir --mode $mode --alias $alias --commit $commit 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    $sw.Stop()
    $text = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText((Join-Path $outDir 'console.log'), $text, (New-Object System.Text.UTF8Encoding($false)))
    $output | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" }
    return [pscustomobject]@{ ExitCode = $code; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

foreach ($scenario in $scenarioFiles) {
    $scenarioName = [System.IO.Path]::GetFileNameWithoutExtension($scenario)
    foreach ($alias in $FixtureAlias) {
        foreach ($mode in $Modes) {
            foreach ($condition in $Conditions) {
                $cellDir = Join-Path $batchDir "$scenarioName\$alias-$mode-$condition"
                $runs = if ($condition -eq 'cold-os') { 1 } else { $Repeat }
                if ($condition -eq 'warm') {
                    Write-Host "[$scenarioName $alias $mode $condition] warm-up"
                    $warm = Invoke-Session $scenario $folders[$alias] (Join-Path $cellDir 'warmup') $mode $alias
                    $cells.Add([ordered]@{ scenario = $scenarioName; fixture = $alias; mode = $mode; condition = $condition; run = 0; warmup = $true
                            status = $(if ($warm.ExitCode -eq 0) { 'ok' } else { "fail($($warm.ExitCode))" }); seconds = $warm.Seconds
                            outDir = (Join-Path $cellDir 'warmup') })
                    Save-Matrix
                }
                for ($run = 1; $run -le $runs; $run++) {
                    if ($condition -eq 'cold-diskcache') { Clear-DiskCache }
                    $outDir = Join-Path $cellDir ('run-{0:00}' -f $run)
                    Write-Host "[$scenarioName $alias $mode $condition] run $run/$runs"
                    $started = (Get-Date).ToString('o')
                    $result = Invoke-Session $scenario $folders[$alias] $outDir $mode $alias
                    $cells.Add([ordered]@{ scenario = $scenarioName; fixture = $alias; mode = $mode; condition = $condition; run = $run; warmup = $false
                            status = $(if ($result.ExitCode -eq 0) { 'ok' } else { "fail($($result.ExitCode))" }); started = $started
                            seconds = $result.Seconds; outDir = $outDir })
                    Save-Matrix
                }
            }
        }
    }
}

$failed = @($cells | Where-Object { $_.status -ne 'ok' }).Count
Write-Host "Matrix done: $($cells.Count) run(s), $failed failed. $matrixPath"
if ($failed -gt 0) { exit 1 }
