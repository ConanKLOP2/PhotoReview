<#
.SYNOPSIS
  OPTIONAL local perf gate (verify-all.ps1 -PerfGate): measure the published exes with `--startup-probe` and compare with the
  machine baseline tools/perf-baselines/<COMPUTERNAME>.json. Off by default: CI runners are too noisy and have no baseline.

.DESCRIPTION
  - No baseline file -> prints how to create one and exits 0 (the gate cannot fail without a baseline).
  - REGRESSION (exit 1 of compare) or a schema/usage error (2) -> exit 1.
  - INCONCLUSIVE (exit 3: busy machine, high variance, ...) -> warning, exit 0; re-run on a quiet machine.
  - -UpdateBaseline copies the fresh measurement over the baseline file (only when the owner decided to re-baseline).
  Needs the fixture folder (-Source, default env PHOTOREVIEW_PERF_SOURCE, else C:\Xiuren\[[WALLPAPER]) with >= -Count .jpg files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$Baseline,
    [string]$Source,
    [ValidateRange(3, 200)][int]$Rounds = 6,
    [ValidateRange(0, 10)][int]$Warmup = 1,
    [ValidateRange(3, 200)][int]$Count = 10,
    [ValidateRange(0, 200)][int]$Keys = 12,
    [switch]$SkipBuild,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Baseline) { $Baseline = Join-Path $repo ("tools\perf-baselines\{0}.json" -f $env:COMPUTERNAME) }
if (-not $Source) { $Source = if ($env:PHOTOREVIEW_PERF_SOURCE) { $env:PHOTOREVIEW_PERF_SOURCE } else { 'C:\Xiuren\[[WALLPAPER]' } }
$hasBaseline = Test-Path -LiteralPath $Baseline
if (-not $hasBaseline -and -not $UpdateBaseline) {
    Write-Host "[SKIP] perf gate: no baseline at $Baseline."
    Write-Host "       Create one on a quiet machine: .\tools\diag\perf-gate.ps1 -ReleaseDirectory <publish dir> -UpdateBaseline  (commit it only when the owner agrees)."
    exit 0
}

$shell = Join-Path $ReleaseDirectory 'PhotoReview.exe'
$wpf = Join-Path $ReleaseDirectory 'PhotoReview.App.exe'
foreach ($exe in @($shell, $wpf)) { if (-not (Test-Path -LiteralPath $exe)) { throw "published exe not found: $exe" } }
if (-not (Test-Path -LiteralPath $Source)) { throw "perf fixture folder not found: $Source (set -Source or PHOTOREVIEW_PERF_SOURCE)" }

$project = Join-Path $repo 'tools\PhotoReview.Benchmark.Cli\PhotoReview.Benchmark.Cli.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c Release --nologo /nodeReuse:false -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'build Benchmark.Cli failed' }
}
$cli = Join-Path $repo 'tools\PhotoReview.Benchmark.Cli\bin\Release\net10.0-windows\PhotoReview.Benchmark.Cli.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $repo "work\perf\gate-$stamp"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$current = Join-Path $outDir 'perf-result.json'

& $cli --startup-probe --shell $shell --wpf $wpf --source $Source --count $Count --rounds $Rounds --warmup $Warmup --keys $Keys --out $outDir --perf-json $current
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] startup-probe exit $LASTEXITCODE"; exit 1 }

if ($UpdateBaseline) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Baseline) | Out-Null
    Copy-Item -LiteralPath $current -Destination $Baseline -Force
    Write-Host "[OK] baseline written: $Baseline"
    exit 0
}

& $cli compare $Baseline $current --markdown (Join-Path $outDir 'compare.md') --json (Join-Path $outDir 'compare.json')
$code = $LASTEXITCODE
switch ($code) {
    0 { Write-Host '[PASS] perf gate'; exit 0 }
    3 { Write-Warning 'perf gate INCONCLUSIVE (busy machine / noise / missing metric): not a failure; re-run when the machine is quiet'; exit 0 }
    default { Write-Host "[FAIL] perf gate (compare exit $code); see $outDir\compare.md"; exit 1 }
}
