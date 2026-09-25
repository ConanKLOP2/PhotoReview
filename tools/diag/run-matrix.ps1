<#
.SYNOPSIS
  D06: runs the perf-session scenario matrix (scenario x mode x condition x repeat), one fresh process per run.

.DESCRIPTION
  Each run invokes the built PhotoReview.Benchmark.Cli.exe directly (tools\PhotoReview.Benchmark.Cli\bin\Release\<tfm>\)
  in a new process; if that exe cannot be found (e.g. -SkipBuild before any build), falls back to
  `dotnet run --project PhotoReview.Benchmark.Cli -c Release --no-build -- --perf-session ...`. The driver only uses
  in-process WPF routed events (no OS-level input, no foreground changes; see
  docs/refactoring/diagnosis/perf-session.md if present -- otherwise this header is the usage doc). Conditions:
    cold-app       new process, caches left as they are
    cold-diskcache delete the active cache root's cache\*.pv4;*.png and thumbnails\*.png before every run
    warm           one unrecorded warm-up run for the cell, then the recorded runs
    cold-os        NOT automated: the script stops and prints what the user must do (reboot or RAMMap).
                   After doing it, re-run with -Conditions cold-os -ColdOsConfirmed (runs once per cell).
  Writes <OutRoot>\<stamp>\matrix.json (cells, status, durations) after every run. Run data is never committed.

  Cache isolation (perf(harness)): every run passes --cache-dir <batchDir>\cache to perf-session by
  default, so preview+thumbnail disk caches live under this batch's own folder instead of the real
  %LOCALAPPDATA%\PhotoReview (shared across cells/runs WITHIN one batch, so 'warm' still means warm).
  -SharedAppCache restores the old behavior (every run reads/writes the real app's caches -- useful
  for an explicit before/after comparison, never the default). -ColdDiskCache empties the batch's own
  cache dir before every recorded (non-warmup) run in every cell; it refuses to combine with
  -SharedAppCache, since that would otherwise silently empty the real app's cache instead.

  Warm-up (condition 'warm'): by default the warm-up run for a cell runs -WarmupScenario (default
  s1-open-folder: open folder + waitIdle) instead of the full measured scenario, since its only job is to warm the
  OS file cache and PhotoReview's disk cache before the recorded runs. Pass -FullWarmup to run the full scenario as
  the warm-up instead (old behaviour).

  -Profile quick|gate|full selects a scenario set and repeat count together (see table below). An explicit
  -Scenarios and/or -Repeat overrides the profile's corresponding value; -Modes/-Conditions/-FixtureAlias are never
  touched by -Profile.
    quick  S2 + S3, Repeat 1  (fast smoke check of the event-driven settle path)
    gate   S2 + S3 + S4, Repeat 2
    full   S1 + S1b + S2 + S3 + S4, Repeat 3  (the previous unconditional default set)

  Fixture-change guard: before the batch, and after every run, this script records each fixture folder's file count
  and total byte size. If either changes mid-batch (e.g. someone copies photos into the folder while a batch is
  running), it prints a loud WARNING for every affected run and marks matrix.json's top level `fixtureChanged: true`
  -- treat every cell in that batch as unverified.

.EXAMPLE
  .\tools\diag\run-matrix.ps1 -Scenarios s2-next-slow -Modes Fast,Preview,Original -Conditions warm -Repeat 3 -FixtureAlias F1

.EXAMPLE
  .\tools\diag\run-matrix.ps1 -Profile quick -FixtureAlias F1
  .\tools\diag\run-matrix.ps1 -Profile gate -FixtureAlias F1 -FullWarmup
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
    [switch]$SkipBuild,
    [ValidateSet('quick', 'gate', 'full')]
    [string]$Profile,
    [string]$WarmupScenario = 's1-open-folder',
    [switch]$FullWarmup,
    # perf(harness): by default every run in this batch passes --cache-dir <batchDir>\cache to
    # perf-session, so preview+thumbnail disk caches live under this batch's own folder instead
    # of the real %LOCALAPPDATA%\PhotoReview\{cache,thumbnails} (shared with whatever else is
    # using this machine's real PhotoReview install). The directory is still shared ACROSS every
    # run/cell in one batch, so a 'warm' condition's warm-up run still warms what its recorded
    # runs then read. -SharedAppCache opts back into the old behavior (no --cache-dir; every run
    # reads/writes the real app's caches) for an explicit before/after comparison against it.
    [switch]$SharedAppCache,
    # Empties the batch's own cache directory before every recorded (non-warmup) run in every
    # cell. Safe specifically because it targets <batchDir>\cache, not the real app's cache --
    # refuses to combine with -SharedAppCache, which would otherwise silently wipe that instead.
    [switch]$ColdDiskCache
)

if ($ColdDiskCache -and $SharedAppCache) {
    throw "-ColdDiskCache cannot be combined with -SharedAppCache: it would delete the real app's %LOCALAPPDATA%\PhotoReview cache, not a batch-owned one."
}

$ErrorActionPreference = 'Stop'

# `powershell -File` passes "a,b" as one string; accept both forms.
function Split-List([string[]]$values) { @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Scenarios = Split-List $Scenarios
$Modes = Split-List $Modes
$Conditions = Split-List $Conditions
$FixtureAlias = Split-List $FixtureAlias
foreach ($m in $Modes) { if ($m -notin 'Fast', 'Preview', 'Original') { throw "Invalid mode '$m' (Fast|Preview|Original)" } }
foreach ($c in $Conditions) { if ($c -notin 'cold-app', 'cold-diskcache', 'warm', 'cold-os') { throw "Invalid condition '$c' (cold-app|cold-diskcache|warm|cold-os)" } }

# TOOL-03: the 'cold-diskcache' condition clears the cache root of every recorded run; with -SharedAppCache that is the
# real app's %LOCALAPPDATA%\PhotoReview cache, so it is rejected exactly like -ColdDiskCache -SharedAppCache above.
if ($SharedAppCache -and $Conditions -contains 'cold-diskcache') {
    throw "-Conditions cold-diskcache cannot be combined with -SharedAppCache: it would delete the real app's %LOCALAPPDATA%\PhotoReview cache, not a batch-owned one."
}

# -Profile picks a scenario set + repeat count; an explicitly-passed -Scenarios/-Repeat wins over the profile.
if ($Profile) {
    $profileScenarios = switch ($Profile) {
        'quick' { @('s2-next-slow', 's3-next-burst') }
        'gate' { @('s2-next-slow', 's3-next-burst', 's4-jump') }
        'full' { @('s1-open-folder', 's1b-open-file', 's2-next-slow', 's3-next-burst', 's4-jump') }
    }
    $profileRepeat = switch ($Profile) { 'quick' { 1 }; 'gate' { 2 }; 'full' { 3 } }
    if (-not $PSBoundParameters.ContainsKey('Scenarios')) { $Scenarios = $profileScenarios }
    if (-not $PSBoundParameters.ContainsKey('Repeat')) { $Repeat = $profileRepeat }
    Write-Host "Profile '$Profile': scenarios=$($Scenarios -join ',') repeat=$Repeat"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scenarioDir = Join-Path $PSScriptRoot 'scenarios'
$testsProject = Join-Path $repoRoot 'tools\PhotoReview.Benchmark.Cli\PhotoReview.Benchmark.Cli.csproj'

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

function Resolve-ScenarioFile([string]$s) {
    $candidate = if (Test-Path -LiteralPath $s) { $s } else { Join-Path $scenarioDir (($s -replace '\.json$', '') + '.json') }
    if (-not (Test-Path -LiteralPath $candidate)) { throw "Scenario not found: $s" }
    return (Resolve-Path -LiteralPath $candidate).Path
}
$scenarioFiles = foreach ($s in $Scenarios) { Resolve-ScenarioFile $s }
$warmupScenarioFile = Resolve-ScenarioFile $WarmupScenario
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

# Prefer the built exe (no `dotnet run` host/build-check overhead per process); fall back to `dotnet run --no-build`.
$cliExe = Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $testsProject) 'bin\Release') -Filter 'PhotoReview.Benchmark.Cli.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($cliExe) { Write-Host "Using built exe: $($cliExe.FullName)" }
else { Write-Host "Built exe not found under tools\PhotoReview.Benchmark.Cli\bin\Release; falling back to 'dotnet run --no-build' (slower, one MSBuild up-to-date check per process)." -ForegroundColor Yellow }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$batchDir = Join-Path $OutRoot $stamp
New-Item -ItemType Directory -Force -Path $batchDir | Out-Null
$matrixPath = Join-Path $batchDir 'matrix.json'
$cells = New-Object System.Collections.Generic.List[object]
$localApp = Join-Path $env:LOCALAPPDATA 'PhotoReview'
# perf(harness): $cacheDir is passed as --cache-dir to every perf-session invocation below (see
# PerfSession.cs/CacheDirOverrideAppPaths) so this batch's preview+thumbnail disk caches live
# under the batch folder instead of the real %LOCALAPPDATA%\PhotoReview -- shared across every
# cell/run IN this batch (so 'warm' still means warm), but never touching whatever else uses the
# real app install on this machine. $null (-SharedAppCache) restores the old shared-cache behavior.
$cacheDir = if ($SharedAppCache) { $null } else { Join-Path $batchDir 'cache' }
if ($cacheDir) { Write-Host "Cache isolation: preview+thumbnail caches under $cacheDir (batch-scoped)" }
else { Write-Host "Cache isolation: -SharedAppCache -- using the real $localApp caches" -ForegroundColor Yellow }

# Fixture-change guard: snapshot every fixture folder now, and re-check after every run.
function Get-FixtureStat([string]$path) {
    $files = @(Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue)
    $bytes = if ($files.Count -eq 0) { 0 } else { ($files | Measure-Object -Property Length -Sum).Sum }
    return [pscustomobject]@{ Count = $files.Count; Bytes = [int64]$bytes }
}
$fixtureBaseline = @{}
foreach ($alias in $FixtureAlias) { $fixtureBaseline[$alias] = Get-FixtureStat $folders[$alias] }
$script:fixtureChanged = $false
function Test-FixtureUnchanged([string]$alias) {
    $current = Get-FixtureStat $folders[$alias]
    $base = $fixtureBaseline[$alias]
    if ($current.Count -ne $base.Count -or $current.Bytes -ne $base.Bytes) {
        $script:fixtureChanged = $true
        Write-Host "WARNING: fixture '$alias' ($($folders[$alias])) changed mid-batch: was $($base.Count) file(s)/$($base.Bytes) bytes, now $($current.Count) file(s)/$($current.Bytes) bytes. This entire batch is suspect." -ForegroundColor Red
    }
}

function Save-Matrix {
    $doc = [ordered]@{
        created = $stamp; commit = $commit; repeat = $Repeat
        scenarios = $Scenarios; modes = $Modes; conditions = $Conditions; fixtures = $FixtureAlias
        cacheIsolation = if ($cacheDir) { 'isolated' } else { 'shared' }; cacheDir = $cacheDir; coldDiskCache = [bool]$ColdDiskCache
        fixtureBaseline = $fixtureBaseline; fixtureChanged = $script:fixtureChanged
        diagEnv = @(Get-ChildItem Env: | Where-Object Name -like 'PHOTOREVIEW_DIAG_*' | ForEach-Object { "$($_.Name)=$($_.Value)" })
        cells = $cells
    }
    [System.IO.File]::WriteAllText($matrixPath, ($doc | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}

# Preview cache entries are ".pv4" (perf(cache) v4 -- see PreviewCacheFile); thumbnails are still
# plain PNGs. Removing both patterns from 'cache' also sweeps any pre-v4 ".png" leftovers so a
# 'cold-diskcache' condition run always starts genuinely cache-empty, not just v4-empty.
function Clear-DiskCache([string]$root) {
    $cacheSubDir = Join-Path $root 'cache'
    if (Test-Path -LiteralPath $cacheSubDir) {
        Get-ChildItem -LiteralPath $cacheSubDir -Filter '*.pv4' -File | Remove-Item -Force
        Get-ChildItem -LiteralPath $cacheSubDir -Filter '*.png' -File | Remove-Item -Force
    }
    $thumbSubDir = Join-Path $root 'thumbnails'
    if (Test-Path -LiteralPath $thumbSubDir) {
        Get-ChildItem -LiteralPath $thumbSubDir -Filter '*.png' -File | Remove-Item -Force
    }
}

function Invoke-Session([string]$scenario, [string]$folder, [string]$outDir, [string]$mode, [string]$alias, [string]$cacheDirForRun) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # native stderr must not become a terminating error (PS 5.1)
    $cacheArgs = if ($cacheDirForRun) { @('--cache-dir', $cacheDirForRun) } else { @() }
    try {
        if ($cliExe) {
            $output = & $cliExe.FullName --perf-session $scenario $folder $outDir --mode $mode --alias $alias --commit $commit @cacheArgs 2>&1
        }
        else {
            $output = & dotnet run --project $testsProject -c Release --no-build -- --perf-session $scenario $folder $outDir --mode $mode --alias $alias --commit $commit @cacheArgs 2>&1
        }
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    $sw.Stop()
    $text = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText((Join-Path $outDir 'console.log'), $text, (New-Object System.Text.UTF8Encoding($false)))
    $output | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" }
    Test-FixtureUnchanged $alias
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
                    $warmupTarget = if ($FullWarmup) { $scenario } else { $warmupScenarioFile }
                    $warmupTargetName = [System.IO.Path]::GetFileNameWithoutExtension($warmupTarget)
                    Write-Host "[$scenarioName $alias $mode $condition] warm-up ($warmupTargetName)"
                    $warm = Invoke-Session $warmupTarget $folders[$alias] (Join-Path $cellDir 'warmup') $mode $alias $cacheDir
                    $cells.Add([ordered]@{ scenario = $scenarioName; fixture = $alias; mode = $mode; condition = $condition; run = 0; warmup = $true
                            warmupScenario = $warmupTargetName
                            status = $(if ($warm.ExitCode -eq 0) { 'ok' } else { "fail($($warm.ExitCode))" }); seconds = $warm.Seconds
                            outDir = (Join-Path $cellDir 'warmup') })
                    Save-Matrix
                }
                for ($run = 1; $run -le $runs; $run++) {
                    # 'cold-diskcache' (a -Conditions value) always targets whichever cache root
                    # is actually active for this batch: the batch-scoped $cacheDir by default, or
                    # the real $localApp when -SharedAppCache restored the old behavior.
                    $condColdCacheRoot = if ($cacheDir) { $cacheDir } else { $localApp }
                    if ($condition -eq 'cold-diskcache') { Clear-DiskCache $condColdCacheRoot }
                    # -ColdDiskCache (a standalone switch, independent of -Conditions) additionally
                    # empties the batch's own cache dir before every recorded run in every cell,
                    # regardless of condition; the param block above already refuses to combine it
                    # with -SharedAppCache, so $cacheDir is always non-null here.
                    if ($ColdDiskCache) { Clear-DiskCache $cacheDir }
                    $outDir = Join-Path $cellDir ('run-{0:00}' -f $run)
                    Write-Host "[$scenarioName $alias $mode $condition] run $run/$runs"
                    $started = (Get-Date).ToString('o')
                    $result = Invoke-Session $scenario $folders[$alias] $outDir $mode $alias $cacheDir
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
if ($script:fixtureChanged) { Write-Host "WARNING: fixtureChanged=true -- one or more fixture folders changed mid-batch; treat these results as invalid." -ForegroundColor Red }
if ($failed -gt 0) { exit 1 }
