<#
.SYNOPSIS
  Compare two structured perf results (schema photoreview.perf-result v1) with the Benchmark.Cli `compare` verb.

.DESCRIPTION
  Per metric: outliers (MAD) removed on the bad side, median compared against max(5 %, absolute floor) AND a 95 % interval of the
  difference. Verdicts PASS / WARN / REGRESSION / INCONCLUSIVE (busy machine > 60 % CPU, high variance, too few samples, missing
  metric, different machine or parameters). Exit codes: 0 PASS/WARN, 1 REGRESSION, 2 usage/schema error, 3 INCONCLUSIVE.
  Results come from `--startup-probe` (writes work/perf/*.json, git-ignored) or `--perf-export <perf-session dir>`.
  A committed baseline lives at tools/perf-baselines/<MACHINE>.json ONLY when the owner decided so.

.EXAMPLE
  .\tools\diag\perf-compare.ps1 -Baseline tools\perf-baselines\$env:COMPUTERNAME.json -Current work\perf\startup-probe-PC-20261013-101500.json
.EXAMPLE
  # stricter threshold, Markdown + JSON copies of the table
  .\tools\diag\perf-compare.ps1 -Baseline a.json -Current b.json -Extra '--threshold-percent','3','--markdown','out.md'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Current,
    [string[]]$Extra = @(),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $repo 'tools\PhotoReview.Benchmark.Cli\PhotoReview.Benchmark.Cli.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c Release --nologo /nodeReuse:false -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'build Benchmark.Cli failed' }
}
$cli = Join-Path $repo 'tools\PhotoReview.Benchmark.Cli\bin\Release\net10.0-windows\PhotoReview.Benchmark.Cli.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw "CLI not built: $cli" }
& $cli compare $Baseline $Current @Extra
exit $LASTEXITCODE
