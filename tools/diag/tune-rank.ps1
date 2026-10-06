<#
.SYNOPSIS
  Ranks the configs of a tune-matrix.ps1 batch: results.csv (one row per run) + report.md
  (docs/refactoring/perf/PLAN-device-config-bench.md, sections 2-3).

.DESCRIPTION
  USAGE
    .\tools\diag\tune-rank.ps1 -BatchDir <OutRoot>\tune-20261006-193645 -Baseline default [-Metric p95] [-NoisePct 5]

  Per run it calls `PhotoReview.Benchmark.Cli.exe --perf-analyze <run dir>` (reuses an existing summary.json unless
  -Reanalyze) and reads: finalVisualMs p50/p95, kindCounts (non-RamHit share), gcTimePercent, frameTimeP95Ms, the R-THREAD
  evidence (t_input P95), preload.pausedCount, incomplete navs, plus process.json (peak working set) and session.json (errors).
  Metrics the analyzer / session files do NOT emit are reported as n/a (never invented): min available RAM during the run.
  frame P95 is n/a for runs where the analyzer found no frame-time data.

  Aggregation: per (scenario, config) the MEDIAN of the per-run values (so "median of run medians" for -Metric p50, median of
  run P95s for p95), a seeded bootstrap 95 % CI of that median (resampling runs), and ratio vs the -Baseline config.
  Objective: geometric mean of the ratios over -GeoScenarios (default S2,S3,S4; scenario = prefix of the scenario file name).
  Constraints (a config violating any evaluable one is disqualified): zero failed runs, peak WS (max over runs) <= -MaxPeakWsGb,
  min available RAM >= -MinAvailRamGb (n/a), GC time % (median) <= -MaxGcPct, frame P95 (median) <= -MaxFrameP95Ms,
  input P95 (median) <= -MaxInputP95Ms, preload-paused-for-memory events = 0. -NoisePct (from the S0 A/A runs) enables the
  plan's winner rule: >= 10 % better than baseline and >= 2x the noise.
  Needs at least 2 runs per cell for a meaningful CI; with 1 run the CI equals the value.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BatchDir,
    [Parameter(Mandatory)][string]$Baseline,
    [ValidateSet('p50', 'p95')]
    [string]$Metric = 'p95',
    [string[]]$GeoScenarios = @('S2', 'S3', 'S4'),
    [double]$MaxPeakWsGb = 20,
    [double]$MinAvailRamGb = 4,
    [double]$MaxGcPct = 10,
    [double]$MaxFrameP95Ms = 33,
    [double]$MaxInputP95Ms = 16,
    [double]$NoisePct,
    [int]$BootstrapResamples = 2000,
    [int]$Seed = 1,
    [switch]$Reanalyze,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$matrixPath = Join-Path $BatchDir 'matrix.json'
if (-not (Test-Path -LiteralPath $matrixPath)) { throw "No matrix.json in $BatchDir" }
if (-not $OutDir) { $OutDir = $BatchDir }
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
$GeoScenarios = @($GeoScenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })

$cliDir = Join-Path $repoRoot 'tools\PhotoReview.Benchmark.Cli\bin\Release'
$cliExe = Get-ChildItem -LiteralPath $cliDir -Filter 'PhotoReview.Benchmark.Cli.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $cliExe -and ($matrix.cells | Where-Object { -not $_.warmup -and -not (Test-Path -LiteralPath (Join-Path $_.outDir 'summary.json')) })) {
    throw "Built PhotoReview.Benchmark.Cli.exe not found under $cliDir (build Release first) and some runs have no summary.json."
}

function F($v, [int]$digits = 1) { if ($null -eq $v -or ($v -is [double] -and [double]::IsNaN($v))) { 'n/a' } else { ([double]$v).ToString("F$digits", $inv) } }
function Csv($v) { if ($null -eq $v -or ($v -is [double] -and [double]::IsNaN($v))) { '' } else { ([IConvertible]$v).ToString($inv) } }
function Get-Median([double[]]$values) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $s = @($values | Sort-Object); $m = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return [double]$s[$m] } else { return ([double]$s[$m - 1] + [double]$s[$m]) / 2 }
}
function Get-Bootstrap95([double[]]$values, [System.Random]$rng) {
    if (-not $values -or $values.Count -eq 0) { return @($null, $null) }
    if ($values.Count -eq 1) { return @($values[0], $values[0]) }
    $meds = New-Object 'double[]' $BootstrapResamples
    for ($b = 0; $b -lt $BootstrapResamples; $b++) {
        $pick = New-Object 'double[]' $values.Count
        for ($i = 0; $i -lt $values.Count; $i++) { $pick[$i] = $values[$rng.Next($values.Count)] }
        $meds[$b] = Get-Median $pick
    }
    $sorted = @($meds | Sort-Object)
    return @($sorted[[int][math]::Floor(0.025 * ($BootstrapResamples - 1))], $sorted[[int][math]::Ceiling(0.975 * ($BootstrapResamples - 1))])
}
function Read-Json([string]$path) { if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { $null } }

# ---- per-run rows ------------------------------------------------------------------------------------------------------
$rows = New-Object System.Collections.Generic.List[object]
foreach ($cell in @($matrix.cells | Where-Object { -not $_.warmup })) {
    $dir = [string]$cell.outDir
    $row = [ordered]@{
        scenario = $null; config = $cell.configId; run = $cell.run; status = $cell.status
        firstVisualP95Ms = $null; finalVisualP50Ms = $null; finalVisualP95Ms = $null; nonRamHitPct = $null
        peakWsGb = $null; minAvailRamGb = $null; gcPct = $null; frameP95Ms = $null; inputP95Ms = $null
        renderedFrameP95Ms = $null; pausedCount = $null; incompleteNavs = $null; sessionErrors = $null; outDir = $dir
    }
    $row.scenario = ([string]$cell.scenario -split '-')[0].ToUpperInvariant()
    if (-not (Test-Path -LiteralPath $dir)) { $row.status = 'missing-dir'; $rows.Add([pscustomobject]$row); continue }
    $summaryPath = Join-Path $dir 'summary.json'
    if ($cell.status -eq 'ok' -and ($Reanalyze -or -not (Test-Path -LiteralPath $summaryPath))) {
        & $cliExe.FullName --perf-analyze $dir *> $null
        if ($LASTEXITCODE -ne 0) { Write-Host "  perf-analyze failed for $dir ($LASTEXITCODE)" -ForegroundColor Yellow }
    }
    $sum = Read-Json $summaryPath
    $grp = if ($sum) { @($sum.groups | Sort-Object { $_.count } -Descending) | Select-Object -First 1 } else { $null }
    if ($grp) {
        $row.firstVisualP95Ms = $grp.firstVisualMs.p95
        $row.finalVisualP50Ms = $grp.finalVisualMs.p50
        $row.finalVisualP95Ms = $grp.finalVisualMs.p95
        $kinds = $grp.kindCounts; $total = 0; $ram = 0
        if ($kinds) { foreach ($p in $kinds.PSObject.Properties) { $total += [int]$p.Value; if ($p.Name -like '*RamHit*') { $ram += [int]$p.Value } } }
        if ($total -gt 0) { $row.nonRamHitPct = 100.0 * ($total - $ram) / $total }
        $row.gcPct = $grp.gcTimePercent
        $row.frameP95Ms = $grp.frameTimeP95Ms
        $row.renderedFrameP95Ms = $grp.renderedFrameMs.p95
        $row.pausedCount = $grp.preload.pausedCount
        $row.incompleteNavs = $grp.incomplete
        $thread = @($grp.rules | Where-Object { $_.rule -eq 'R-THREAD' }) | Select-Object -First 1
        if ($thread -and $thread.evidence -match 't_input P95=([0-9.]+)ms') { $row.inputP95Ms = [double]::Parse($Matches[1], $inv) }
    }
    $proc = Read-Json (Join-Path $dir 'process.json')
    if ($proc) { $row.peakWsGb = [double]$proc.peakWorkingSetBytes / 1GB }
    $sess = Read-Json (Join-Path $dir 'session.json')
    if ($sess) { $row.sessionErrors = @($sess.errors).Count }
    $rows.Add([pscustomobject]$row)
}
if ($rows.Count -eq 0) { throw 'matrix.json has no recorded runs' }
$csvPath = Join-Path $OutDir 'results.csv'
$rows | ForEach-Object {
    $r = $_; $o = [ordered]@{}
    foreach ($p in $r.PSObject.Properties) { $o[$p.Name] = if ($p.Value -is [double]) { Csv $p.Value } else { $p.Value } }
    [pscustomobject]$o
} | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
Write-Host "Wrote $csvPath ($($rows.Count) run row(s))"

# ---- aggregation ------------------------------------------------------------------------------------------------------
$metricField = if ($Metric -eq 'p50') { 'finalVisualP50Ms' } else { 'finalVisualP95Ms' }
$configIds = @($rows | ForEach-Object { $_.config } | Select-Object -Unique)
if ($Baseline -notin $configIds) { throw "Baseline config '$Baseline' not in batch (configs: $($configIds -join ', '))" }
$scenarios = @($rows | ForEach-Object { $_.scenario } | Select-Object -Unique | Sort-Object)
$rng = New-Object System.Random($Seed)
$agg = @{}   # "scenario|config" -> object
foreach ($sc in $scenarios) {
    foreach ($cfg in $configIds) {
        $runs = @($rows | Where-Object { $_.scenario -eq $sc -and $_.config -eq $cfg })
        if ($runs.Count -eq 0) { continue }
        $okRuns = @($runs | Where-Object { $_.status -eq 'ok' -and $null -ne $_.$metricField })
        $vals = [double[]]@($okRuns | ForEach-Object { [double]$_.$metricField })
        $p50s = [double[]]@($okRuns | Where-Object { $null -ne $_.finalVisualP50Ms } | ForEach-Object { [double]$_.finalVisualP50Ms })
        $p95s = [double[]]@($okRuns | Where-Object { $null -ne $_.finalVisualP95Ms } | ForEach-Object { [double]$_.finalVisualP95Ms })
        $ci = Get-Bootstrap95 $vals $rng
        $agg["$sc|$cfg"] = [pscustomobject]@{
            Scenario = $sc; Config = $cfg; Runs = $runs.Count; OkRuns = $okRuns.Count
            Median = (Get-Median $vals); CiLow = $ci[0]; CiHigh = $ci[1]
            MedianP50 = (Get-Median $p50s); MedianP95 = (Get-Median $p95s)
        }
    }
}

function Get-Constraint($cfgRows) {
    $fail = 0; $maxWs = $null; $viol = @(); $na = @()
    $fail = @($cfgRows | Where-Object { $_.status -ne 'ok' -or ($null -ne $_.sessionErrors -and $_.sessionErrors -gt 0) }).Count   # incompleteNavs stays informational (a nav still in flight at scenario end is not a failed image)
    if ($fail -gt 0) { $viol += "failures=$fail" }
    $wsVals = @($cfgRows | Where-Object { $null -ne $_.peakWsGb } | ForEach-Object { [double]$_.peakWsGb })
    if ($wsVals.Count -gt 0) { $maxWs = ($wsVals | Measure-Object -Maximum).Maximum; if ($maxWs -gt $MaxPeakWsGb) { $viol += "peakWS $(F $maxWs 1) GB > $MaxPeakWsGb" } } else { $na += 'peakWS' }
    $na += 'minAvailRam'   # not emitted by --perf-analyze / session files: gap, never invented
    $gc = @($cfgRows | Where-Object { $null -ne $_.gcPct } | ForEach-Object { [double]$_.gcPct })
    $gcMed = Get-Median $gc
    if ($null -eq $gcMed) { $na += 'gc%' } elseif ($gcMed -gt $MaxGcPct) { $viol += "gc $(F $gcMed 1)% > $MaxGcPct" }
    $fr = @($cfgRows | Where-Object { $null -ne $_.frameP95Ms } | ForEach-Object { [double]$_.frameP95Ms })
    $frMed = Get-Median $fr
    if ($null -eq $frMed) { $na += 'frameP95' } elseif ($frMed -gt $MaxFrameP95Ms) { $viol += "frameP95 $(F $frMed 1) ms > $MaxFrameP95Ms" }
    $inp = @($cfgRows | Where-Object { $null -ne $_.inputP95Ms } | ForEach-Object { [double]$_.inputP95Ms })
    $inpMed = Get-Median $inp
    if ($null -eq $inpMed) { $na += 'inputP95' } elseif ($inpMed -gt $MaxInputP95Ms) { $viol += "inputP95 $(F $inpMed 1) ms > $MaxInputP95Ms" }
    $paused = @($cfgRows | Where-Object { $null -ne $_.pausedCount -and $_.pausedCount -gt 0 }).Count
    if ($paused -gt 0) { $viol += "preload paused (memory) in $paused run(s)" }
    [pscustomobject]@{ Valid = ($viol.Count -eq 0); Violations = $viol; NotAvailable = $na; MaxPeakWsGb = $maxWs; GcMedian = $gcMed; FrameMedian = $frMed; InputMedian = $inpMed }
}

$summary = foreach ($cfg in $configIds) {
    $cfgRows = @($rows | Where-Object { $_.config -eq $cfg })
    $con = Get-Constraint $cfgRows
    $ratios = @(); $perScen = [ordered]@{}
    foreach ($sc in $GeoScenarios) {
        $a = $agg["$sc|$cfg"]; $b = $agg["$sc|$Baseline"]
        if ($a -and $b -and $null -ne $a.Median -and $null -ne $b.Median -and $b.Median -gt 0) { $ratios += ($a.Median / $b.Median) }
    }
    $geo = if ($ratios.Count -gt 0) { [math]::Exp((($ratios | ForEach-Object { [math]::Log($_) }) | Measure-Object -Average).Average) } else { $null }
    [pscustomobject]@{
        Config = $cfg; Runs = $cfgRows.Count; Constraint = $con; Geo = $geo; GeoParts = $ratios.Count
        Winner = $(if ($null -ne $geo -and $cfg -ne $Baseline -and $con.Valid -and $geo -le 0.9 -and ($PSBoundParameters.ContainsKey('NoisePct') -and ((1 - $geo) * 100) -ge 2 * $NoisePct)) { 'yes' } else { '' })
    }
}
# Pareto: among valid configs with a geo ratio, non-dominated on (geo ratio, peak WS), both lower-is-better.
$cand = @($summary | Where-Object { $_.Constraint.Valid -and $null -ne $_.Geo -and $null -ne $_.Constraint.MaxPeakWsGb })
$pareto = @($cand | Where-Object {
        $c = $_
        -not ($cand | Where-Object { $_.Config -ne $c.Config -and $_.Geo -le $c.Geo -and $_.Constraint.MaxPeakWsGb -le $c.Constraint.MaxPeakWsGb -and ($_.Geo -lt $c.Geo -or $_.Constraint.MaxPeakWsGb -lt $c.Constraint.MaxPeakWsGb) })
    } | ForEach-Object { $_.Config })

# ---- report ---------------------------------------------------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
function Out-Line([string]$s = '') { [void]$sb.AppendLine($s) }
$firstEnv = @($matrix.sessions)[0].environment
Out-Line "# Tune ranking: $($matrix.created)"
Out-Line ''
Out-Line "- Batch: ``$BatchDir`` | stage ``$(Split-Path -Leaf $matrix.stage)`` (hash $($matrix.stageHash)) | seed $($matrix.seed) | repeat $($matrix.repeat)"
Out-Line "- Fixture $($matrix.fixtureAlias): $($matrix.fixtureFingerprint.Count) files, $($matrix.fixtureFingerprint.Bytes) bytes, fingerprint $($matrix.fixtureFingerprint.Fingerprint); changed mid-batch: $($matrix.fixtureChanged)"
Out-Line "- Commit $($matrix.commit) | CLI $($matrix.cliVersion) | cold disk cache: $($matrix.coldDiskCache) | cooldown $($matrix.cooldownSeconds) s"
if ($firstEnv) { Out-Line "- Environment at start: AC=$($firstEnv.onAcPower), plan=$($firstEnv.powerPlan), free RAM $($firstEnv.freeRamGb) GB, CPU idle $($firstEnv.cpuIdlePercentAvg) %$(if ($firstEnv.warnings) { '; warnings: ' + ($firstEnv.warnings -join '; ') })" }
Out-Line "- Ranking metric: final-visual $Metric per run, median over runs, bootstrap 95 % CI ($BootstrapResamples resamples, seed $Seed); baseline = ``$Baseline``; objective = geometric mean of the ratio over $($GeoScenarios -join ', ')"
if ($matrix.fixtureChanged) { Out-Line ''; Out-Line '**WARNING: the fixture changed mid-batch; all numbers are suspect.**' }
Out-Line ''
Out-Line '## Ranking (valid configs first, best geometric-mean ratio first; ratio < 1 is faster than baseline)'
Out-Line ''
Out-Line '| Config | Runs | Geo ratio | Peak WS GB (max) | GC % (med) | Frame P95 ms | Input P95 ms | Valid | Violations | Not available | Pareto | Winner |'
Out-Line '|---|---|---|---|---|---|---|---|---|---|---|---|'
$ordered = @($summary | Sort-Object @{ Expression = { -not $_.Constraint.Valid } }, @{ Expression = { if ($null -eq $_.Geo) { [double]::MaxValue } else { $_.Geo } } })
foreach ($s in $ordered) {
    $c = $s.Constraint
    Out-Line ("| {0} | {1} | {2}{3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} |" -f $s.Config, $s.Runs, (F $s.Geo 3), $(if ($s.GeoParts -lt $GeoScenarios.Count -and $null -ne $s.Geo) { " (partial $($s.GeoParts)/$($GeoScenarios.Count))" } else { '' }),
        (F $c.MaxPeakWsGb 2), (F $c.GcMedian 1), (F $c.FrameMedian 1), (F $c.InputMedian 1), $(if ($c.Valid) { 'yes' } else { 'NO' }),
        ($c.Violations -join '; '), ($c.NotAvailable -join ', '), $(if ($s.Config -in $pareto) { 'yes' } else { '' }), $s.Winner)
}
Out-Line ''
Out-Line '## Per scenario (final-visual, ms)'
foreach ($sc in $scenarios) {
    Out-Line ''
    Out-Line "### $sc"
    Out-Line ''
    Out-Line "| Config | Runs (ok) | $Metric median | 95 % CI | Ratio vs $Baseline | Median P50 | Median P95 |"
    Out-Line '|---|---|---|---|---|---|---|'
    $b = $agg["$sc|$Baseline"]
    foreach ($cfg in $configIds) {
        $a = $agg["$sc|$cfg"]; if (-not $a) { continue }
        $ratio = if ($b -and $null -ne $a.Median -and $null -ne $b.Median -and $b.Median -gt 0) { $a.Median / $b.Median } else { $null }
        Out-Line ("| {0} | {1} ({2}) | {3} | {4} - {5} | {6} | {7} | {8} |" -f $cfg, $a.Runs, $a.OkRuns, (F $a.Median 2), (F $a.CiLow 2), (F $a.CiHigh 2), (F $ratio 3), (F $a.MedianP50 2), (F $a.MedianP95 2))
    }
}
Out-Line ''
Out-Line '## Pareto: speed (geo ratio) vs peak RAM, valid configs only'
Out-Line ''
Out-Line '| Config | Geo ratio | Peak WS GB (max) | On front |'
Out-Line '|---|---|---|---|'
foreach ($s in @($cand | Sort-Object Geo)) { Out-Line ("| {0} | {1} | {2} | {3} |" -f $s.Config, (F $s.Geo 3), (F $s.Constraint.MaxPeakWsGb 2), $(if ($s.Config -in $pareto) { 'yes' } else { '' })) }
Out-Line ''
Out-Line '## Data gaps'
Out-Line ''
Out-Line '- Minimum available RAM during a run is not emitted by `--perf-analyze` or the session files: the `min available RAM >= 4 GB` constraint is **n/a** (not evaluated).'
Out-Line '- Frame P95 comes from `frameTimeP95Ms` of the analyzer and is n/a for runs without frame-time data; input P95 is read from the R-THREAD evidence text (`t_input P95`).'
Out-Line '- Visual-output identity versus baseline (hash/size check) is not part of this script.'
if ($PSBoundParameters.ContainsKey('NoisePct')) { Out-Line "- Winner rule applied with noise $NoisePct %: >= 10 % better than baseline and >= 2x noise." } else { Out-Line '- Winner column is empty unless -NoisePct (from the S0 A/A runs) is given.' }
$reportPath = Join-Path $OutDir 'report.md'
[System.IO.File]::WriteAllText($reportPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $reportPath"
