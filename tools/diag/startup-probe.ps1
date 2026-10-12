<#
.SYNOPSIS
  Interleaved startup + key->frame probe of the PUBLISHED R2R builds: Win32 shell (PhotoReview.exe) vs WPF (PhotoReview.App.exe).
  WP-29 of NO-WPF-EXEC-PLAN-WP (perf gate wave 3). The measuring logic is C# (tools/PhotoReview.Benchmark.Cli/ShellHost, unit-tested);
  this script only publishes (optional), calls `--startup-probe` and prints where the CSV/JSON/Markdown went.

.DESCRIPTION
  Per run (one fresh process, own data root, own PHOTOREVIEW_PERF_TRACE dir, own photo folder copy of -Count JPEGs from -Source):
    first frame drawn (RenderedFrame), window shown/revealed (Startup marks, ms since process start), key->frame of -Keys posted
    WM_KEYDOWN/WM_KEYUP (PostMessage, NO SendInput) once the neighbours are preloaded (all keys and ramHit-only), peak working set,
    system CPU % during the run. Closed with WM_CLOSE (CloseMainWindow); only the process the probe itself created is ever killed
    (after a 15 s close timeout) - never Get-Process/Stop-Process by name.
  Schedule: interleaved round-robin, seeded shuffle per round, -Warmup rounds discarded. The first app in the output (wpf) is the
  baseline; the shell is compared with it (regression = delta > max(5 %, 1 ms)).
  Targets (docs/refactoring/perf/<date>-nowpf-wave3.md): first image <= 600 ms (NW-5) / 450 ms (card), window <= 150 ms,
  key->frame P95 in cache <= 3 ms, regression <= 5 %.

.EXAMPLE
  # publish both exes into <OutDir>\pub, then 8 interleaved rounds (+1 discarded):
  .\tools\diag\startup-probe.ps1 -Publish -Rounds 8 -OutDir $env:TEMP\wp29-probe
.EXAMPLE
  # reuse earlier publishes:
  .\tools\diag\startup-probe.ps1 -ShellExe D:\pub\shell\PhotoReview.exe -WpfExe D:\pub\wpf\PhotoReview.App.exe -Rounds 8
#>
[CmdletBinding()]
param(
    [string]$ShellExe,
    [string]$WpfExe,
    [string]$Source = 'C:\Xiuren\[[WALLPAPER]',
    [switch]$Publish,
    [string]$OutDir,
    [ValidateRange(1, 200)][int]$Rounds = 8,
    [ValidateRange(0, 10)][int]$Warmup = 1,
    [ValidateRange(3, 200)][int]$Count = 10,
    [ValidateRange(0, 200)][int]$Keys = 12,
    [int]$Seed = 1,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('pr-startup-probe-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($Publish) {
    $ShellExe = Join-Path $OutDir 'pub\shell\PhotoReview.exe'
    $WpfExe = Join-Path $OutDir 'pub\wpf\PhotoReview.App.exe'
    dotnet publish "$repo\src\PhotoReview.Shell.Win32\PhotoReview.Shell.Win32.csproj" -c Release -r win-x64 --self-contained false -o (Split-Path $ShellExe) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'publish shell failed' }
    dotnet publish "$repo\src\PhotoReview.App\PhotoReview.App.csproj" -c Release -r win-x64 --self-contained false -o (Split-Path $WpfExe) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'publish wpf failed' }
}
if (-not $ShellExe -and -not $WpfExe) { throw 'pass -Publish or -ShellExe/-WpfExe' }

$cliProject = "$repo\tools\PhotoReview.Benchmark.Cli\PhotoReview.Benchmark.Cli.csproj"
if (-not $SkipBuild) {
    dotnet build $cliProject -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'build Benchmark.Cli failed' }
}
$cli = Join-Path $repo 'tools\PhotoReview.Benchmark.Cli\bin\Release\net10.0-windows\PhotoReview.Benchmark.Cli.exe'
if (-not (Test-Path $cli)) { throw "CLI not built: $cli" }

$probeArgs = @('--startup-probe', '--source', $Source, '--count', $Count, '--rounds', $Rounds, '--warmup', $Warmup,
    '--seed', $Seed, '--keys', $Keys, '--out', $OutDir)
if ($ShellExe) { $probeArgs += @('--shell', $ShellExe) }
if ($WpfExe) { $probeArgs += @('--wpf', $WpfExe) }

$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
Write-Host "CPU load before the batch: $cpu %"
& $cli @probeArgs
exit $LASTEXITCODE
