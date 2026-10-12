<#
.SYNOPSIS
  WP21-STALL: experiment matrix runner - key->frame + per-frame stall statistics for several CONFIGS of PhotoReview.exe (shell Win32),
  interleaved by pass. Uses the out-of-process probe `PhotoReview.Benchmark.Cli --startup-probe` (NOWPF-WP29, PR #460) with a key gap
  override (PHOTOREVIEW_PROBE_KEY_GAP, see below) and the per-frame log of the shell (PHOTOREVIEW_FRAME_LOG).

.DESCRIPTION
  -ConfigFile: JSON array of { "name": "...", "gap": 250, "keys": 40, "exe": "<optional PhotoReview.exe>", "env": { "VAR": "value" } }.
  Per config and pass it runs the probe (-RoundsPerPass fresh processes, -Keys posted WM_KEYDOWN/WM_KEYUP spaced `gap` ms), parses the
  per-frame log (frames spaced ~gap only) and prints a Markdown table: key->frame P50/P95 (probe), frame P50/P95/max, frames >= 100 ms.
  Deletes the copied photos/runs dirs after each run (each run copies ~120 MB; the disk fills up otherwise).
  NOTE: the stock probe CLI has no --key-gap; the experiments patched ProcessProbeSession.cs to read PHOTOREVIEW_PROBE_KEY_GAP
  (Thread.Sleep(keyGapMs) -> that value). Without the patch `gap` is ignored (always 250 ms).
  The hypothesis knobs of the experiments (swap effect, buffers, latency, flush, D2D/D3D threading, adapter, DO_NOT_WAIT, warm
  variants) are in tools/diag/stall-experiment-knobs.patch (`git apply` on commit 36b55eef); the shipped fix is the default build.
#>
param(
  [Parameter(Mandatory)][string]$ShellExe,
  [Parameter(Mandatory)][string]$OutDir,
  [Parameter(Mandatory)][string]$ConfigFile,   # JSON: [{ "name": "...", "gap": 250, "keys": 40, "env": {"PHOTOREVIEW_STALL_X":"1"} }, ...]
  [int]$Passes = 2, [int]$RoundsPerPass = 2, [int]$Keys = 40,
  [string]$Cli = (Join-Path $PSScriptRoot '..\PhotoReview.Benchmark.Cli\bin\Release\net10.0-windows\PhotoReview.Benchmark.Cli.exe'),
  [string]$Source = 'C:\Xiuren\[[WALLPAPER]'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$configs = Get-Content $ConfigFile -Raw | ConvertFrom-Json
function Pct($a, $p) { if ($a.Count -eq 0) { return [double]::NaN }; $s = @($a | Sort-Object); $s[[Math]::Min($s.Count-1, [Math]::Max(0, [int][Math]::Ceiling($p/100*$s.Count)-1))] }
function SetEnv([string]$n, [string]$v) { [Environment]::SetEnvironmentVariable($n, $v, 'Process') }
$rows = @{}
foreach ($c in $configs) { $rows[$c.name] = @{ frames = New-Object System.Collections.Generic.List[double]; ends = New-Object System.Collections.Generic.List[double]; kp95 = @(); kp50 = @(); keys = 0; cpu = @(); nowf = 0 } }
for ($pass = 0; $pass -lt $Passes; $pass++) {
  foreach ($c in $configs) {
    $safe = ($c.name -replace '[^A-Za-z0-9]+', '_')
    $dir = Join-Path $OutDir "$safe-p$pass"
    $log = Join-Path $dir 'frames.log'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $names = @()
    foreach ($p in $c.env.PSObject.Properties) { SetEnv $p.Name ([string]$p.Value); $names += $p.Name }
    SetEnv 'PHOTOREVIEW_STALL_LOG' $log
    SetEnv 'PHOTOREVIEW_FRAME_LOG' $log
    $exe = if ($c.exe) { [string]$c.exe } else { $ShellExe }
    if ($c.gap) { SetEnv 'PHOTOREVIEW_PROBE_KEY_GAP' ([string]$c.gap) }
    $kk = if ($c.keys) { [int]$c.keys } else { $Keys }
    try {
      & $Cli --startup-probe --source $Source --count 10 --rounds $RoundsPerPass --warmup 0 --seed (1 + $pass) --keys $kk --out $dir --shell $exe | Out-Null
    } finally {
      foreach ($n in $names) { SetEnv $n $null }
      SetEnv 'PHOTOREVIEW_STALL_LOG' $null
      SetEnv 'PHOTOREVIEW_FRAME_LOG' $null
      SetEnv 'PHOTOREVIEW_PROBE_KEY_GAP' $null
    }
    $r = $rows[$c.name]
    foreach ($sub in 'photos', 'runs') { $pp = Join-Path $dir $sub; if (Test-Path -LiteralPath $pp) { Remove-Item -LiteralPath $pp -Recurse -Force -ErrorAction SilentlyContinue } }
    if (Test-Path (Join-Path $dir 'summary.json')) {
      $sum = Get-Content (Join-Path $dir 'summary.json') -Raw | ConvertFrom-Json
      $k = $sum.aggregates[0].KeyToFrameCached
      $r.keys += $k.Count; if ($k.Count -gt 0) { $r.kp95 += $k.P95; $r.kp50 += $k.Median }
      foreach ($line in (Import-Csv (Join-Path $dir 'runs.csv'))) { $r.cpu += [double]$line.systemCpuPercent; $r.nowf += [int]$line.keysWithoutFrame }
    }
    if (Test-Path $log) {
      $g0 = if ($c.gap) { [double]$c.gap } else { 250.0 }
      foreach ($l in Get-Content $log) {
        if ($l -match '^[KF] gap=([\d.]+) frame=([\d.]+) endDraw=([\d.]+) present=([\d.]+)') {
          $gap = [double]$Matches[1]
          if ($gap -ge 0.5 * $g0 -and $gap -le $g0 + 520) { $r.frames.Add([double]$Matches[2]); $r.ends.Add([double]$Matches[3]) }
        }
      }
    }
  }
}
$out = New-Object System.Text.StringBuilder
[void]$out.AppendLine('| Config | keys(cached) | key->frame P50 | key->frame P95 (mean of passes) | frames n | frame P50 | P95 | max | >=100 ms | EndDraw>=100 | no-frame keys | CPU % |')
[void]$out.AppendLine('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($c in $configs) {
  $r = $rows[$c.name]
  $n = $r.frames.Count
  $stall = @($r.frames | Where-Object { $_ -ge 100 }).Count
  $stallE = @($r.ends | Where-Object { $_ -ge 100 }).Count
  $mx = if ($n) { ($r.frames | Measure-Object -Maximum).Maximum } else { 0 }
  $p50 = if ($r.kp50.Count) { ($r.kp50 | Measure-Object -Average).Average } else { [double]::NaN }
  $p95 = if ($r.kp95.Count) { ($r.kp95 | Measure-Object -Average).Average } else { [double]::NaN }
  $cpu = if ($r.cpu.Count) { ($r.cpu | Measure-Object -Average).Average } else { 0 }
  $line = '| {0} | {1} | {2:F1} | {3:F1} | {4} | {5:F1} | {6:F1} | {7:F0} | {8} ({9:F1} %) | {10} | {11} | {12:F0} |' -f $c.name, $r.keys, $p50, $p95, $n, (Pct $r.frames 50), (Pct $r.frames 95), $mx, $stall, $(if($n){100.0*$stall/$n}else{0}), $stallE, $r.nowf, $cpu
  [void]$out.AppendLine($line)
}
$out.ToString() | Tee-Object (Join-Path $OutDir 'table.md')

