<#
.SYNOPSIS
  Device-tuning matrix: runs config x scenario x repeat perf-sessions in randomized, interleaved order,
  one fresh PhotoReview.Benchmark.Cli process per run (docs/refactoring/perf/PLAN-device-config-bench.md, section 2).

.DESCRIPTION
  USAGE
    # 1. preview the expanded schedule (builds nothing, runs nothing, writes nothing):
    .\tools\diag\tune-matrix.ps1 -Stage s2-workers -Profile gate -FixtureAlias F4 -Repeat 3 -DryRun
    # 2. run it unattended (long: use run_in_background and a hard timeout; the PC must stay on AC power, no sleep):
    .\tools\diag\tune-matrix.ps1 -Stage s2-workers -Profile gate -FixtureAlias F4 -Repeat 3 -SkipBuild
    # 3. resume after an interruption: pass the same parameters plus the batch dir printed at the start:
    .\tools\diag\tune-matrix.ps1 -Stage s2-workers -Profile gate -FixtureAlias F4 -Repeat 3 -SkipBuild -BatchDir <OutRoot>\tune-...
    # 4. rank: .\tools\diag\tune-rank.ps1 -BatchDir <batch dir> -Baseline <config id>

  STAGE FILE  (tools\diag\tune\<stage>.json, or any path): a JSON array of configs
    [{ "id": "w8", "decoder": "WicDirect", "mode": "Preview", "set": { "PreloadWorkerCount": 8 }, "env": { "DOTNET_gcServer": "0" } }]
    id      required; several entries may share one id (s0-noise.json: the default config x6) - each entry is one run per round.
    decoder Wpf|WicDirect|TurboJpeg (default WicDirect), mode Fast|Preview|Original (default Preview): always passed
            explicitly so the baseline never depends on a leftover default.
    set     becomes repeatable `--set Key=Value` for --perf-session (whitelisted AppSettings, in memory only; the CLI
            rejects unknown keys / out-of-range values and records the effective values in session.json).
    env     environment variables of THAT child process only (never persisted, never set in this shell).

  SCHEDULE: for each scenario, for each repeat round, every config entry runs once in a seeded random order
  (round-robin: no config runs twice before every other one ran). Same -Seed + same stage file = same schedule.
  ONE warm-up per (fixture, decoder, mode) combo (s1-open-folder, unrecorded, skipped with -ColdDiskCache); the first run
  of each combo is therefore not discarded again. -CooldownSeconds (default 30) between runs so a laptop CPU does not
  throttle into later cells. Caches ALWAYS live under <batch>\cache (--cache-dir); the real %LOCALAPPDATA%\PhotoReview
  caches are never used and there is no -SharedAppCache. -ColdDiskCache empties <batch>\cache before every recorded run.

  RESOURCE SAMPLER: every recorded run (and warm-up) passes `--resource-sample` to the CLI, which then samples ITSELF every 500 ms
  into <run dir>\resources.csv: min/avg available physical RAM (GlobalMemoryStatusEx), working set, peak working set, private bytes,
  GC pause %, page faults/s and CPU %. It runs in-process (a thread of the measured CLI process, which is also the app host), not as a
  separate sidecar, because GC pause time and page faults are only cheaply readable from inside; it dies with the run, nothing to kill.
  tune-rank.ps1 fills the minAvailRam constraint from it. -NoResourceSample turns it off.

  STAGE FILES shipped in tools\diag\tune\ (see README.md there for how to run each stage): s0-noise, s1-decoder-mode, s2-workers,
  s3-window, s3-window-refine, s4-ram, s5-caches, s6-pressure (with tools\diag\MemBalloon.ps1), s8-runtime. Every ID 'default' is the
  shipped default config of that stage = baseline and A/A group for tune-rank.ps1.

  matrix.json (rewritten after every run) holds the environment checklist (AC power, power plan, free RAM, free disk,
  CPU idle %, fixture fingerprint, commit, versions), the expanded configs and one entry per finished run. Refuses to start
  on battery unless -AllowBattery. Resume skips runs already ok/fail in matrix.json (-RetryFailed re-runs the failed ones).
  Run data goes under work\diag\tune-runs (git-ignored) and is never committed.

.EXAMPLE
  .\tools\diag\tune-matrix.ps1 -Stage s0-noise -Profile quick -FixtureAlias F4 -Repeat 1 -DryRun
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Stage,
    [string[]]$Scenario,
    [ValidateSet('quick', 'gate', 'full')]
    [string]$Profile,
    [ValidateRange(1, 100)]
    [int]$Repeat = 3,
    [string]$FixtureAlias = 'F4',
    [string]$FixturesFile,
    [string]$OutRoot,
    [string]$BatchDir,
    [switch]$SkipBuild,
    [switch]$ColdDiskCache,
    [ValidateRange(0, 3600)]
    [int]$CooldownSeconds = 30,
    [int]$Seed = 1,
    [switch]$AllowBattery,
    [switch]$NoResourceSample,
    [switch]$RetryFailed,
    [switch]$DryRun,
    [string]$WarmupScenario = 's1-open-folder',
    [ValidateRange(1, 600)]
    [int]$RunTimeoutMinutes = 30,
    [ValidateRange(0, 600)]
    [int]$IdleCheckSeconds = 10
)

$ErrorActionPreference = 'Stop'
$dry = $DryRun -or $WhatIfPreference
# -WhatIf is only an alias of -DryRun here; leaving the preference on would make read-only cmdlets (Resolve-Path, Get-FileHash) misbehave.
$WhatIfPreference = $false

function Split-List([string[]]$values) { @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scenarioDir = Join-Path $PSScriptRoot 'scenarios'
$tuneDir = Join-Path $PSScriptRoot 'tune'
$cliProject = Join-Path $repoRoot 'tools\PhotoReview.Benchmark.Cli\PhotoReview.Benchmark.Cli.csproj'
. (Join-Path $PSScriptRoot 'Fixture-Fingerprint.ps1')   # Get-FixtureStat / Test-FixtureStatEqual (same guard as run-matrix.ps1)

# work\ is git-ignored and lives in the main checkout; agent worktrees fall back to it (same as run-matrix.ps1).
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
if (-not $OutRoot) { $OutRoot = Join-Path $workDir 'diag\tune-runs' }
if (-not $FixturesFile) { $FixturesFile = Join-Path $workDir 'diag\fixtures.local.json' }

# ---- scenarios (-Scenario list or -Profile, like run-matrix.ps1) -------------------------------------------------
$profileScenarios = @{
    quick = @('s2-next-slow-quick', 's3-next-burst')
    gate  = @('s2-next-slow', 's3-next-burst', 's4-jump')
    full  = @('s1-open-folder', 's1b-open-file', 's2-next-slow', 's3-next-burst', 's4-jump')
}
$Scenario = Split-List $Scenario
if ($Scenario.Count -eq 0) {
    if (-not $Profile) { $Profile = 'quick' }
    $Scenario = $profileScenarios[$Profile]
}
function Resolve-ScenarioFile([string]$s) {
    $candidate = if (Test-Path -LiteralPath $s) { $s } else { Join-Path $scenarioDir (($s -replace '\.json$', '') + '.json') }
    if (-not (Test-Path -LiteralPath $candidate)) { throw "Scenario not found: $s" }
    return (Resolve-Path -LiteralPath $candidate).Path
}
$scenarioFiles = @($Scenario | ForEach-Object { Resolve-ScenarioFile $_ })
$warmupScenarioFile = Resolve-ScenarioFile $WarmupScenario

# ---- stage file --------------------------------------------------------------------------------------------------
$stagePath = if (Test-Path -LiteralPath $Stage) { (Resolve-Path -LiteralPath $Stage).Path } else { Join-Path $tuneDir (($Stage -replace '\.json$', '') + '.json') }
if (-not (Test-Path -LiteralPath $stagePath)) { throw "Stage file not found: $Stage (looked in $tuneDir)" }
$stageHash = (Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash.Substring(0, 16)
$stageRaw = [System.IO.File]::ReadAllText($stagePath, [System.Text.Encoding]::UTF8)
$stageItems = @((ConvertFrom-Json $stageRaw) | ForEach-Object { $_ })   # PS 5.1 returns a top-level JSON array as ONE object; this unrolls it
if ($stageItems.Count -eq 0) { throw "Stage file $stagePath has no configs" }

function Convert-ToArgText($value) {
    if ($value -is [bool]) { return $value.ToString().ToLowerInvariant() }
    if ($value -is [IConvertible]) { return [Convert]::ToString($value, [System.Globalization.CultureInfo]::InvariantCulture) }
    return [string]$value
}
$configs = New-Object System.Collections.Generic.List[object]
$entryNo = 0
foreach ($item in $stageItems) {
    $entryNo++
    if (-not $item.id -or $item.id -notmatch '^[A-Za-z0-9._-]+$') { throw "Stage entry #$entryNo needs an 'id' of letters, digits, . _ - (got '$($item.id)')" }
    $decoder = if ($item.decoder) { [string]$item.decoder } else { 'WicDirect' }
    $mode = if ($item.mode) { [string]$item.mode } else { 'Preview' }
    if ($decoder -notin 'Wpf', 'WicDirect', 'TurboJpeg') { throw "Stage entry '$($item.id)': invalid decoder '$decoder' (Wpf|WicDirect|TurboJpeg)" }
    if ($mode -notin 'Fast', 'Preview', 'Original') { throw "Stage entry '$($item.id)': invalid mode '$mode' (Fast|Preview|Original)" }
    $set = [ordered]@{}
    if ($item.set) { foreach ($p in $item.set.PSObject.Properties) { $set[$p.Name] = Convert-ToArgText $p.Value } }
    $envMap = [ordered]@{}
    if ($item.env) {
        foreach ($p in $item.env.PSObject.Properties) {
            if ($p.Name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Stage entry '$($item.id)': invalid environment variable name '$($p.Name)'" }
            $envMap[$p.Name] = Convert-ToArgText $p.Value
        }
    }
    $configs.Add([pscustomobject]@{ id = [string]$item.id; decoder = $decoder; mode = $mode; set = $set; env = $envMap })
}

# ---- schedule: scenario > round > shuffled config entries ------------------------------------------------------------
function Get-Schedule {
    $rng = New-Object System.Random($Seed)
    $schedule = New-Object System.Collections.Generic.List[object]
    foreach ($sf in $scenarioFiles) {
        $sname = [System.IO.Path]::GetFileNameWithoutExtension($sf)
        $perId = @{}   # id -> number of entries with that id (runs per round)
        foreach ($c in $configs) { $perId[$c.id] = 1 + [int]$perId[$c.id] }
        for ($round = 1; $round -le $Repeat; $round++) {
            $order = New-Object System.Collections.Generic.List[object]
            $seenInRound = @{}
            foreach ($c in $configs) {
                $occ = 1 + [int]$seenInRound[$c.id]; $seenInRound[$c.id] = $occ
                $order.Add([pscustomobject]@{ Config = $c; Run = ($round - 1) * $perId[$c.id] + $occ })
            }
            for ($i = $order.Count - 1; $i -gt 0; $i--) {   # Fisher-Yates, seeded
                $j = $rng.Next($i + 1)
                $tmp = $order[$i]; $order[$i] = $order[$j]; $order[$j] = $tmp
            }
            foreach ($o in $order) {
                $schedule.Add([pscustomobject]@{
                        Key = "$sname/$($o.Config.id)/run-$('{0:00}' -f $o.Run)"
                        Scenario = $sname; ScenarioFile = $sf; Config = $o.Config; Run = $o.Run; Round = $round
                    })
            }
        }
    }
    return $schedule
}
$schedule = Get-Schedule

function Format-Args($config) {
    $parts = @("--decoder $($config.decoder)", "--mode $($config.mode)")
    foreach ($k in $config.set.Keys) { $parts += "--set $k=$($config.set[$k])" }
    foreach ($k in $config.env.Keys) { $parts += "env:$k=$($config.env[$k])" }
    return $parts -join ' '
}

if ($dry) {
    Write-Host "DRY RUN (nothing built, run or written). Stage '$stagePath' (hash $stageHash), seed $Seed, repeat $Repeat, scenarios: $($Scenario -join ', ')"
    Write-Host "Fixture alias: $FixtureAlias; cooldown ${CooldownSeconds}s between runs; coldDiskCache=$([bool]$ColdDiskCache); warm-up once per (fixture, decoder, mode) combo: $(-not $ColdDiskCache)"
    Write-Host ''
    $n = 0
    foreach ($s in $schedule) {
        $n++
        Write-Host ('{0,4}  round {1}  {2,-20} {3,-14} run-{4:00}  {5}' -f $n, $s.Round, $s.Scenario, $s.Config.id, $s.Run, (Format-Args $s.Config))
    }
    $combos = @($schedule | ForEach-Object { "$($_.Config.decoder)/$($_.Config.mode)" } | Select-Object -Unique)
    $estimate = ($schedule.Count * $CooldownSeconds) / 60
    Write-Host ''
    Write-Host "$($schedule.Count) recorded run(s), $(if ($ColdDiskCache) { 0 } else { $combos.Count }) warm-up(s); cooldowns alone add ~$([math]::Round($estimate, 1)) min."
    return
}

# ---- fixtures ---------------------------------------------------------------------------------------------------------
function Read-Fixtures([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Fixture file not found: $path" }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    try { return $raw | ConvertFrom-Json }
    catch { return ($raw -replace '(?<!\\)\\(?![\\"])', '\\') | ConvertFrom-Json }
}
$fixtures = Read-Fixtures $FixturesFile
$entry = $fixtures.$FixtureAlias
if (-not $entry -or -not $entry.path) { throw "Fixture alias '$FixtureAlias' has no path in $FixturesFile" }
if (-not (Test-Path -LiteralPath $entry.path)) { throw "Fixture '$FixtureAlias' folder does not exist" }
$folder = [string]$entry.path

# ---- environment checklist ---------------------------------------------------------------------------------------------
function Get-EnvironmentChecklist {
    $battery = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $onAc = if ($battery.Count -eq 0) { $true } else { -not ($battery | Where-Object { $_.BatteryStatus -eq 1 }) }
    $plan = ((& powercfg /getactivescheme 2>$null) -join ' ')
    $os = Get-CimInstance Win32_OperatingSystem
    $freeRamGb = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    $totalRamGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    $drives = @{}
    foreach ($root in @((Split-Path -Qualifier $OutRoot), 'C:') | Select-Object -Unique) {
        $d = Get-PSDrive -Name $root.TrimEnd(':') -ErrorAction SilentlyContinue
        if ($d) { $drives[$root] = [math]::Round($d.Free / 1GB, 1) }
    }
    $idle = $null
    if ($IdleCheckSeconds -gt 0) {
        $samples = @(for ($i = 0; $i -lt $IdleCheckSeconds; $i++) {
                (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentIdleTime
                Start-Sleep -Seconds 1
            })
        $idle = [math]::Round(($samples | Measure-Object -Average).Average, 1)
    }
    $warnings = @()
    if (-not $onAc) { $warnings += 'on battery' }
    if ($plan -notmatch 'High performance|Ultimate|Hohe Leistung') { $warnings += "power plan is not High performance ($plan)" }
    if ($freeRamGb -lt 24) { $warnings += "free RAM $freeRamGb GB < 24 GB" }
    foreach ($k in $drives.Keys) { if ($drives[$k] -lt 10) { $warnings += "$k free $($drives[$k]) GB < 10 GB" } }
    if ($null -ne $idle -and $idle -lt 95) { $warnings += "CPU idle $idle % < 95 % (another heavy app?)" }
    return [ordered]@{
        capturedAt = (Get-Date).ToString('o'); machine = $env:COMPUTERNAME; onAcPower = $onAc; powerPlan = $plan
        freeRamGb = $freeRamGb; totalRamGb = $totalRamGb; freeDiskGb = $drives; cpuIdlePercentAvg = $idle; idleCheckSeconds = $IdleCheckSeconds
        warnings = $warnings
    }
}
Write-Host 'Environment checklist:'
$envCheck = Get-EnvironmentChecklist
$envCheck.GetEnumerator() | ForEach-Object { if ($_.Key -ne 'warnings') { Write-Host ("  {0,-18} {1}" -f $_.Key, ($(if ($_.Value -is [System.Collections.IDictionary]) { ($_.Value.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join '; ' } else { $_.Value }))) } }
foreach ($w in $envCheck.warnings) { Write-Host "  WARNING: $w" -ForegroundColor Yellow }
if (-not $envCheck.onAcPower -and -not $AllowBattery) {
    throw 'Not on AC power: laptop results are invalid on battery (throttling). Plug in, or pass -AllowBattery to override.'
}

# ---- build + exe ----------------------------------------------------------------------------------------------------
$commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if (-not $SkipBuild) {
    Write-Host "Building $cliProject (Release)..."
    & dotnet build $cliProject -c Release -nologo -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}
$cliExe = Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $cliProject) 'bin\Release') -Filter 'PhotoReview.Benchmark.Cli.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($cliExe) { Write-Host "Using built exe: $($cliExe.FullName)" }
else { Write-Host "Built exe not found; falling back to 'dotnet run --no-build' (slower)." -ForegroundColor Yellow }
$cliVersion = if ($cliExe) { $cliExe.VersionInfo.ProductVersion } else { $null }

# ---- batch dir / matrix.json (resume) -----------------------------------------------------------------------------------
$resumed = $false
if ($BatchDir -and (Test-Path -LiteralPath (Join-Path $BatchDir 'matrix.json'))) {
    $resumed = $true
    $batch = (Resolve-Path -LiteralPath $BatchDir).Path
}
else {
    $batch = if ($BatchDir) { $BatchDir } else { Join-Path $OutRoot ('tune-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    New-Item -ItemType Directory -Force -Path $batch | Out-Null
}
$matrixPath = Join-Path $batch 'matrix.json'
$cacheDir = Join-Path $batch 'cache'   # ALWAYS isolated: there is deliberately no shared-cache option
$stamp = Split-Path -Leaf $batch
. (Join-Path $PSScriptRoot 'Fixture-Fingerprint.ps1')
$fixtureBaseline = Get-FixtureStat $folder
$doc = [ordered]@{
    created = $stamp; stage = $stagePath; stageHash = $stageHash; seed = $Seed; repeat = $Repeat; scenarios = $Scenario
    fixtureAlias = $FixtureAlias; fixtureFingerprint = $fixtureBaseline; fixtureChanged = $false
    commit = $commit; cliVersion = $cliVersion; cooldownSeconds = $CooldownSeconds; coldDiskCache = [bool]$ColdDiskCache
    cacheIsolation = 'isolated'; cacheDir = $cacheDir
    configs = $configs; sessions = @(); cells = @()
}
if ($resumed) {
    $old = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
    if ($old.stageHash -ne $stageHash -or $old.seed -ne $Seed -or $old.repeat -ne $Repeat -or $old.fixtureAlias -ne $FixtureAlias -or (($old.scenarios -join ',') -ne ($Scenario -join ','))) {
        throw "Cannot resume $batch : stage file / seed / repeat / scenarios / fixture differ from the recorded batch."
    }
    if (-not (Test-FixtureStatEqual $old.fixtureFingerprint $fixtureBaseline)) {
        throw "Cannot resume $batch : fixture '$FixtureAlias' changed since the batch started."
    }
    $doc.created = $old.created; $doc.fixtureChanged = [bool]$old.fixtureChanged; $doc.commit = $old.commit; $doc.cliVersion = $old.cliVersion
    $doc.sessions = @($old.sessions)
    $doc.cells = @($old.cells)
}
$sessionEntry = [ordered]@{ started = (Get-Date).ToString('o'); resumed = $resumed; environment = $envCheck }
$doc.sessions = @($doc.sessions) + @([pscustomobject]$sessionEntry)
$cells = New-Object System.Collections.Generic.List[object]
foreach ($c in @($doc.cells)) { $cells.Add($c) }
if ($RetryFailed) {
    $keep = @($cells | Where-Object { $_.status -eq 'ok' -or $_.warmup })
    $cells.Clear(); foreach ($c in $keep) { $cells.Add($c) }
}
function Save-Matrix {
    $doc.cells = $cells
    [System.IO.File]::WriteAllText($matrixPath, ($doc | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
}
Save-Matrix
Write-Host "Batch dir: $batch$(if ($resumed) { ' (resuming)' })"
Write-Host "Cache isolation: preview+thumbnail caches under $cacheDir (batch-scoped)"

function Clear-DiskCache([string]$root) {   # same patterns as run-matrix.ps1 (batch-owned dir only)
    $c = Join-Path $root 'cache'
    if (Test-Path -LiteralPath $c) {
        Get-ChildItem -LiteralPath $c -Filter '*.pv4' -File | Remove-Item -Force
        Get-ChildItem -LiteralPath $c -Filter '*.png' -File | Remove-Item -Force
    }
    $t = Join-Path $root 'thumbnails'
    if (Test-Path -LiteralPath $t) { Get-ChildItem -LiteralPath $t -Filter '*.png' -File | Remove-Item -Force }
}
function Quote-Arg([string]$a) { if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }

function Invoke-Session($config, [string]$scenarioFile, [string]$outDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $argList = @('--perf-session', $scenarioFile, $folder, $outDir, '--decoder', $config.decoder, '--mode', $config.mode,
        '--alias', $FixtureAlias, '--commit', [string]$commit, '--cache-dir', $cacheDir)
    if (-not $NoResourceSample) { $argList += '--resource-sample' }   # in-process sampler: <run dir>\resources.csv every 500 ms (tune-rank reads it)
    foreach ($k in $config.set.Keys) { $argList += @('--set', "$k=$($config.set[$k])") }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ($cliExe) { $psi.FileName = $cliExe.FullName; $full = $argList }
    else { $psi.FileName = 'dotnet'; $full = @('run', '--project', $cliProject, '-c', 'Release', '--no-build', '--') + $argList }
    $psi.Arguments = ($full | ForEach-Object { Quote-Arg ([string]$_) }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($k in $config.env.Keys) { $psi.EnvironmentVariables[$k] = [string]$config.env[$k] }   # this child only
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEndAsync(); $stderr = $proc.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $proc.WaitForExit($RunTimeoutMinutes * 60 * 1000)) { $timedOut = $true; try { $proc.Kill() } catch { } ; $proc.WaitForExit() }
    $code = if ($timedOut) { -1 } else { $proc.ExitCode }
    $sw.Stop()
    $text = $stdout.Result + [Environment]::NewLine + $stderr.Result
    [System.IO.File]::WriteAllText((Join-Path $outDir 'console.log'), $text, (New-Object System.Text.UTF8Encoding($false)))
    ($text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 2) | ForEach-Object { Write-Host "    $_" }
    $cur = Get-FixtureStat $folder
    if (-not (Test-FixtureStatEqual $fixtureBaseline $cur)) {
        $doc.fixtureChanged = $true
        Write-Host "WARNING: fixture '$FixtureAlias' changed mid-batch; this entire batch is suspect." -ForegroundColor Red
    }
    return [pscustomobject]@{ ExitCode = $code; TimedOut = $timedOut; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

# ---- run -------------------------------------------------------------------------------------------------------------
$done = @{}; foreach ($c in $cells) { if (-not $c.warmup) { $done[$c.key] = $true } }
$warmed = @{}; foreach ($c in $cells) { if ($c.warmup -and $c.status -eq 'ok') { $warmed[$c.combo] = $true } }
$todo = @($schedule | Where-Object { -not $done.ContainsKey($_.Key) })
Write-Host "$($schedule.Count) scheduled run(s), $($schedule.Count - $todo.Count) already finished, $($todo.Count) to run."
$n = 0
foreach ($s in $todo) {
    $n++
    $combo = "$FixtureAlias/$($s.Config.decoder)/$($s.Config.mode)"
    if (-not $ColdDiskCache -and -not $warmed.ContainsKey($combo)) {
        Write-Host "[warm-up $combo] $WarmupScenario"
        $wdir = Join-Path $batch "warmup\$($combo -replace '/', '-')"
        $w = Invoke-Session $s.Config $warmupScenarioFile $wdir
        $cells.Add([pscustomobject]@{ key = "warmup/$combo"; combo = $combo; warmup = $true; status = $(if ($w.ExitCode -eq 0) { 'ok' } else { "fail($($w.ExitCode))" }); seconds = $w.Seconds; outDir = $wdir })
        if ($w.ExitCode -eq 0) { $warmed[$combo] = $true }
        Save-Matrix
    }
    if ($ColdDiskCache) { Clear-DiskCache $cacheDir }
    $outDir = Join-Path $batch ("{0}\{1}\{2}\run-{3:00}" -f $s.Scenario, $FixtureAlias, $s.Config.id, $s.Run)
    Write-Host "[$n/$($todo.Count)] $($s.Scenario) $($s.Config.id) run $($s.Run) (round $($s.Round)) $(Format-Args $s.Config)"
    $started = (Get-Date).ToString('o')
    $r = Invoke-Session $s.Config $s.ScenarioFile $outDir
    $status = if ($r.TimedOut) { 'fail(timeout)' } elseif ($r.ExitCode -eq 0) { 'ok' } else { "fail($($r.ExitCode))" }
    $cells.Add([pscustomobject]@{
            key = $s.Key; scenario = $s.Scenario; fixture = $FixtureAlias; configId = $s.Config.id; decoder = $s.Config.decoder; mode = $s.Config.mode
            set = $s.Config.set; env = $s.Config.env; run = $s.Run; round = $s.Round; warmup = $false
            status = $status; started = $started; seconds = $r.Seconds; outDir = $outDir
        })
    Save-Matrix
    if ($CooldownSeconds -gt 0 -and $n -lt $todo.Count) { Write-Host "    cooldown ${CooldownSeconds}s"; Start-Sleep -Seconds $CooldownSeconds }
}
$failed = @($cells | Where-Object { $_.status -ne 'ok' }).Count
Write-Host "Tune matrix done: $(@($cells | Where-Object { -not $_.warmup }).Count) recorded run(s), $failed failed/warm-up-failed entr(ies). $matrixPath"
if ($doc.fixtureChanged) { Write-Host 'WARNING: fixtureChanged=true -- treat these results as invalid.' -ForegroundColor Red }
if ($failed -gt 0) { exit 1 }
