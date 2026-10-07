<#
.SYNOPSIS
  Real-machine check of the preload memory-pause auto-resume (PR #356, PreloadScheduler.ArmResumeCheckLocked): runs one
  perf-session that opens a folder and then does NOTHING (no navigation) while MemBalloon.ps1 pushes the machine's memory
  load to the preload pause limit for a short, bounded time, then releases it and reports whether preload resumed on its own.

.DESCRIPTION
  USAGE
    # pressure run: balloons leave ~TargetAvailGb..TargetAvailGb+ChunkGb available for HoldSec seconds, then are released.
    # PreloadMemoryLoadLimit=0.85 (in memory only) makes preload pause at ~85% load (~4.8 GB free on a 32 GB machine),
    # keeping a safe margin; the default 0.90 pauses only at ~3.2 GB available, too close to a 3 GB safety floor.
    .\tools\diag\preload-pressure-check.ps1 -FixtureAlias F-mid -OutDir C:\Temp\pp\balloon -Set PreloadMemoryLoadLimit=0.85
    # control run: identical session, no balloon
    .\tools\diag\preload-pressure-check.ps1 -FixtureAlias F-mid -OutDir C:\Temp\pp\control -NoBalloon -Set PreloadMemoryLoadLimit=0.85

  WHAT IT DOES
    1. Writes a scenario into <OutDir>: { open folder } then { waitMs SessionWaitSec }. No key steps at all, so any preload
       activity after the balloon is released can only come from the scheduler's own memory re-check, not a navigation.
    2. Starts the built PhotoReview.Benchmark.Cli.exe --perf-session with --cache-dir <OutDir>\cache (isolated; the real
       %LOCALAPPDATA%\PhotoReview caches and config.json are never written) and --resource-sample.
    3. After BalloonDelaySec, starts MemBalloon.ps1 instances of ChunkGb each, one at a time, re-measuring before each one and
       adding it only while (available - ChunkGb) >= TargetAvailGb (the app keeps growing its cache while preload runs, so one
       big balloon sized up front overshoots). Each has -TimeoutMinutes as a hard self-release cap. Holds them HoldSec seconds,
       then creates the shared stop file. A watchdog (0.25-1 s) releases everything early if available RAM falls below
       MinAvailGb. The release is in a finally block, and a balloon process that does not exit within 30 s of the stop file
       is killed (its memory is private: killing frees it). Pass -Set Key=Value for perf-session in-memory overrides.
    4. Waits (bounded) for the session, then reads the perf trace CSV: PreloadPaused events (load %, available MB) and
       PreloadItem events (decoded/hit/...) before the balloon, while it is held, and after the release; plus the time from
       the release to the first decoded preload item. Writes <OutDir>\pressure.csv (1 s machine memory samples) and
       <OutDir>\summary.json, and prints a verdict:
         RESUMED      a PreloadPaused event while the balloon was held and decoded preload items after the release
         NO-PAUSE     preload never paused (pressure too low, or preload had already finished before the balloon)
         NOT-RESUMED  paused, but no decoded preload item after the release
    The app log (AppLog) is not enabled by perf-session, so the perf trace is the evidence; 'Preload resumed after memory
    pause' is only written to app.log in the real app with logging on.

  SAFETY: the balloon is bounded three ways (HoldSec, the watchdog, MemBalloon -TimeoutMinutes) and the script verifies at
  the end that no MemBalloon process it started is still alive. The fixture folder is only read.
#>
[CmdletBinding()]
param(
    [string]$FixtureAlias = 'F-mid',
    [string]$Folder,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$FixturesFile,
    [string]$CliExe,
    [ValidateSet('Fast', 'Preview', 'Original')][string]$Mode = 'Preview',
    [switch]$NoBalloon,
    [ValidateRange(3, 16)][double]$TargetAvailGb = 4.0,
    [ValidateRange(3, 16)][double]$MinAvailGb = 3.5,
    [ValidateRange(0.5, 8)][double]$ChunkGb = 2,
    # In-memory AppSettings overrides passed through as perf-session --set Key=Value (e.g. PreloadMemoryLoadLimit=0.85).
    [string[]]$Set = @(),
    [ValidateRange(0, 120)][int]$BalloonDelaySec = 10,
    [ValidateRange(10, 150)][int]$HoldSec = 75,
    [ValidateRange(30, 600)][int]$SessionWaitSec = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PressureCheckNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);
    public static MEMORYSTATUSEX Get()
    {
        var s = new MEMORYSTATUSEX(); s.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref s);
        return s;
    }
}
'@
function Get-Mem { $s = [PressureCheckNative]::Get(); [pscustomobject]@{ Load = [int]$s.dwMemoryLoad; AvailGb = $s.ullAvailPhys / 1GB } }

# ---- inputs ---------------------------------------------------------------------------------------------------------
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
if (-not $Folder) {
    if (-not $FixturesFile) { $FixturesFile = Join-Path (Resolve-WorkDir) 'diag\fixtures.local.json' }
    $raw = [System.IO.File]::ReadAllText($FixturesFile, [System.Text.Encoding]::UTF8)
    try { $fixtures = $raw | ConvertFrom-Json } catch { $fixtures = ($raw -replace '(?<!\\)\\(?![\\"])', '\\') | ConvertFrom-Json }
    $entry = $fixtures.$FixtureAlias
    if (-not $entry -or -not $entry.path) { throw "Fixture alias '$FixtureAlias' has no path in $FixturesFile" }
    $Folder = $entry.path
}
if (-not (Test-Path -LiteralPath $Folder)) { throw "Folder not found: $Folder" }

if (-not $CliExe) {
    $candidates = @(Join-Path $repoRoot 'tools\PhotoReview.Benchmark.Cli\bin\Release')
    $common = (& git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $common) { $candidates += Join-Path (Split-Path -Parent $common) 'tools\PhotoReview.Benchmark.Cli\bin\Release' }
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter 'PhotoReview.Benchmark.Cli.exe' -Recurse -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) { throw 'PhotoReview.Benchmark.Cli.exe not found; build tools\PhotoReview.Benchmark.Cli (Release) or pass -CliExe.' }
    $CliExe = $found.FullName
}
$cliVersion = (Get-Item -LiteralPath (Join-Path (Split-Path -Parent $CliExe) 'PhotoReview.Benchmark.Cli.dll')).VersionInfo.ProductVersion

if (Test-Path -LiteralPath $OutDir) { throw "OutDir $OutDir already exists; pass a new folder." }
New-Item -ItemType Directory -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$sessionDir = Join-Path $OutDir 'session'
$cacheDir = Join-Path $OutDir 'cache'
$scenarioPath = Join-Path $OutDir 'preload-pressure.json'
$stopFile = Join-Path $OutDir 'balloon.stop'
$readyFile = Join-Path $OutDir 'balloon.ready'
$pressureCsv = Join-Path $OutDir 'pressure.csv'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$scenario = [ordered]@{
    name = 'preload-pressure'
    note = 'tools/diag/preload-pressure-check.ps1: open the folder, then no navigation at all while the balloon comes and goes.'
    steps = @([ordered]@{ open = 'folder' }, [ordered]@{ waitMs = $SessionWaitSec * 1000 })
}
[System.IO.File]::WriteAllText($scenarioPath, ($scenario | ConvertTo-Json -Depth 4), $utf8)
[System.IO.File]::WriteAllText($pressureCsv, "utc,phase,loadPercent,availMb`r`n", $utf8)

$marks = [ordered]@{}
function Mark([string]$name) { $marks[$name] = (Get-Date).ToUniversalTime(); Write-Host ("{0:HH:mm:ss.fff}Z  {1}" -f $marks[$name], $name) }
$script:phase = 'before'
$script:lowestAvailGb = [double]::MaxValue
$script:maxLoad = 0
function Sample {
    $m = Get-Mem
    if ($m.AvailGb -lt $script:lowestAvailGb) { $script:lowestAvailGb = $m.AvailGb }
    if ($m.Load -gt $script:maxLoad) { $script:maxLoad = $m.Load }
    [System.IO.File]::AppendAllText($pressureCsv, ("{0:o},{1},{2},{3}`r`n" -f (Get-Date).ToUniversalTime(), $script:phase, $m.Load, [math]::Round($m.AvailGb * 1024)), $utf8)
    return $m
}
function Wait-Seconds([double]$seconds, $cli, [switch]$Watchdog) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        $m = Sample
        if ($Watchdog -and $m.AvailGb -lt $MinAvailGb) { Write-Host ("Watchdog: available {0:N2} GB < {1} GB, releasing early." -f $m.AvailGb, $MinAvailGb) -ForegroundColor Yellow; return 'watchdog' }
        if ($cli -and $cli.HasExited) { return 'cli-exited' }
        Start-Sleep -Milliseconds 1000
    }
    return 'elapsed'
}

# ---- run --------------------------------------------------------------------------------------------------------------
$start = Get-Mem
Write-Host ("CLI {0} ({1}); folder {2}; mode {3}; available {4:N1} GB, load {5}%." -f $CliExe, $cliVersion, $Folder, $Mode, $start.AvailGb, $start.Load)
$cliArgs = @('--perf-session', "`"$scenarioPath`"", "`"$Folder`"", "`"$sessionDir`"", '--mode', $Mode, '--alias', $FixtureAlias,
    '--cache-dir', "`"$cacheDir`"", '--resource-sample')
foreach ($pair in $Set) { $cliArgs += @('--set', $pair) }
$cli = Start-Process -FilePath $CliExe -ArgumentList $cliArgs -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $OutDir 'cli.out.log') -RedirectStandardError (Join-Path $OutDir 'cli.err.log')
Mark 'sessionStarted'
$balloons = New-Object System.Collections.Generic.List[object]
$balloonGb = 0
$releaseReason = $null
try {
    $null = Wait-Seconds $BalloonDelaySec $cli
    if (-not $NoBalloon -and -not $cli.HasExited) {
        # Inflate in ChunkGb steps, each only while (available - chunk) stays >= TargetAvailGb, re-measured before every
        # chunk: the app keeps growing its cache while preload runs, so one big balloon sized at the start overshoots
        # (a 2026-10-07 single 14.4 GB balloon hit 1.7 GB available for ~1 s). Bounded to 60 s.
        $timeoutMinutes = [math]::Max(1, [int][math]::Ceiling(($HoldSec + 90) / 60.0))
        $script:phase = 'inflating'
        Mark 'balloonStarted'
        $inflateDeadline = (Get-Date).AddSeconds(60)
        $watchdogHit = $false
        while ((Get-Date) -lt $inflateDeadline -and -not $cli.HasExited) {
            $m = Sample
            if ($m.AvailGb -lt $MinAvailGb) { $watchdogHit = $true; break }
            if ($m.AvailGb - $ChunkGb -lt $TargetAvailGb) { break }
            $ready = Join-Path $OutDir ("balloon{0}.ready" -f $balloons.Count)
            $balloonArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'MemBalloon.ps1')`"",
                '-SizeGb', $ChunkGb.ToString([System.Globalization.CultureInfo]::InvariantCulture), '-StopFile', "`"$stopFile`"",
                '-ReadyFile', "`"$ready`"", '-TimeoutMinutes', $timeoutMinutes)
            $b = Start-Process -FilePath 'powershell.exe' -ArgumentList $balloonArgs -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $OutDir ("balloon{0}.out.log" -f $balloons.Count)) `
                -RedirectStandardError (Join-Path $OutDir ("balloon{0}.err.log" -f $balloons.Count))
            $balloons.Add($b)
            $chunkDeadline = (Get-Date).AddSeconds(30)
            while (-not (Test-Path -LiteralPath $ready) -and -not $b.HasExited -and (Get-Date) -lt $chunkDeadline) {
                if ((Sample).AvailGb -lt $MinAvailGb) { $watchdogHit = $true; break }
                Start-Sleep -Milliseconds 250
            }
            if ($watchdogHit -or -not (Test-Path -LiteralPath $ready)) { break }
            $balloonGb += $ChunkGb
        }
        if ($watchdogHit) { $releaseReason = 'watchdog-while-inflating'; Write-Host 'Watchdog while inflating; releasing.' -ForegroundColor Yellow }
        elseif ($balloonGb -le 0) { $releaseReason = 'no-room'; Write-Host 'No room for even one chunk; no pressure applied.' -ForegroundColor Yellow }
        else {
            Mark 'balloonHeld'
            $script:phase = 'held'
            $releaseReason = Wait-Seconds $HoldSec $cli -Watchdog
        }
    }
}
finally {
    if ($balloons.Count -gt 0) {
        New-Item -ItemType File -Path $stopFile -Force | Out-Null
        Mark 'balloonReleaseRequested'
        $script:phase = 'after'
        foreach ($b in $balloons) {
            if (-not $b.WaitForExit(30000)) { Write-Host "Balloon $($b.Id) did not exit in 30 s; killing it." -ForegroundColor Yellow; Stop-Process -Id $b.Id -Force -ErrorAction SilentlyContinue; $null = $b.WaitForExit(10000) }
        }
        Mark 'balloonExited'
    }
    $script:phase = 'after'
}
# Session: bounded wait (scenario wait + 3 min for open/teardown), then kill.
$sessionDeadline = $marks['sessionStarted'].AddSeconds($SessionWaitSec + 180)
while (-not $cli.HasExited -and (Get-Date).ToUniversalTime() -lt $sessionDeadline) { $null = Sample; Start-Sleep -Milliseconds 1000 }
if (-not $cli.HasExited) { Write-Host 'Session did not finish in time; killing it.' -ForegroundColor Yellow; Stop-Process -Id $cli.Id -Force -ErrorAction SilentlyContinue }
$null = $cli.WaitForExit(10000)
Mark 'sessionExited'
$leftover = @($balloons | ForEach-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
if ($leftover.Count -gt 0) { throw "MemBalloon process(es) $(($leftover | ForEach-Object { $_.Id }) -join ', ') still alive after the run; stop them now with Stop-Process." }
Write-Host 'No MemBalloon process left.'

# ---- analysis -----------------------------------------------------------------------------------------------------------
$traceFile = Get-ChildItem -LiteralPath $sessionDir -Filter '*.csv' -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 2 | Select-Object -Last 1) -like 'utcTicks,*' } | Select-Object -First 1
$events = @()
if ($traceFile) {
    $events = @(Get-Content -LiteralPath $traceFile.FullName | Select-Object -Skip 1 | ConvertFrom-Csv |
        Where-Object { $_.event -in 'PreloadPaused', 'PreloadItem', 'PreloadCancel', 'Folder' } |
        ForEach-Object { $_ | Add-Member -NotePropertyName Utc -NotePropertyValue ([DateTime]::new([long]$_.utcTicks, [DateTimeKind]::Utc)) -PassThru })
}
function Phase-Of([DateTime]$t) {
    if (-not $marks.Contains('balloonStarted')) { return 'control' }
    if ($t -lt $marks['balloonStarted']) { return 'before' }
    if ($marks.Contains('balloonReleaseRequested') -and $t -ge $marks['balloonReleaseRequested']) { return 'after' }
    return 'held'
}
$items = @($events | Where-Object event -eq 'PreloadItem')
$paused = @($events | Where-Object event -eq 'PreloadPaused')
$byPhase = [ordered]@{}
foreach ($g in ($items | Group-Object { Phase-Of $_.Utc })) {
    $byPhase[$g.Name] = [ordered]@{}
    foreach ($k in ($g.Group | Group-Object { $_.text })) { $byPhase[$g.Name][$k.Name] = $k.Count }
}
$release = if ($marks.Contains('balloonReleaseRequested')) { $marks['balloonReleaseRequested'] } else { $null }
$firstDecodedAfter = if ($release) { $items | Where-Object { $_.Utc -ge $release -and $_.text -eq 'decoded' } | Select-Object -First 1 } else { $null }
$pausesHeld = @($paused | Where-Object { (Phase-Of $_.Utc) -eq 'held' })
$verdict = if ($NoBalloon) { if ($paused.Count -eq 0) { 'CONTROL-NO-PAUSE' } else { 'CONTROL-PAUSED' } }
    elseif ($pausesHeld.Count -eq 0) { 'NO-PAUSE' }
    elseif ($firstDecodedAfter) { 'RESUMED' } else { 'NOT-RESUMED' }

$marksIso = [ordered]@{}
foreach ($k in $marks.Keys) { $marksIso[$k] = $marks[$k].ToString('o') }
$summary = [ordered]@{
    verdict = $verdict; cli = $CliExe; cliVersion = $cliVersion; folder = $Folder; mode = $Mode; balloonGb = $balloonGb
    releaseReason = $releaseReason; marksUtc = $marksIso; minAvailGb = [math]::Round($script:lowestAvailGb, 2); maxLoadPercent = $script:maxLoad
    traceFile = if ($traceFile) { $traceFile.FullName } else { $null }
    preloadPaused = @($paused | ForEach-Object { [ordered]@{ utc = $_.Utc.ToString('o'); phase = Phase-Of $_.Utc; loadPercent = $_.a; availableMb = $_.b } })
    preloadItemsByPhase = $byPhase
    firstDecodedAfterRelease = if ($firstDecodedAfter) { [ordered]@{ utc = $firstDecodedAfter.Utc.ToString('o'); secondsAfterRelease = [math]::Round(($firstDecodedAfter.Utc - $release).TotalSeconds, 1) } } else { $null }
    # Last preload item before the release: with a real pause it is the pause time plus the in-flight decodes draining.
    lastPreloadItemBeforeReleaseUtc = if ($release) { $last = $items | Where-Object { $_.Utc -lt $release } | Select-Object -Last 1; if ($last) { $last.Utc.ToString('o') } else { $null } } else { $null }
    lastPreloadItemUtc =if ($items.Count) { $items[-1].Utc.ToString('o') } else { $null }
}
[System.IO.File]::WriteAllText((Join-Path $OutDir 'summary.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
$summary | ConvertTo-Json -Depth 6 | Write-Host
Write-Host "Verdict: $verdict  ($OutDir\summary.json)"
