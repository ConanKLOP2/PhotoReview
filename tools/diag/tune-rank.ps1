<#
.SYNOPSIS
  Ranks the configs of a tune-matrix.ps1 batch: results.csv (one row per run) + report.md
  (docs/refactoring/perf/PLAN-device-config-bench.md, sections 2-3).

.DESCRIPTION
  USAGE
    .\tools\diag\tune-rank.ps1 -BatchDir <OutRoot>\tune-20261006-193645 -Baseline default [-Metric p95] [-NoisePct 5] [-NoiseConfig default]

  WHAT IS RANKED (S0 finding, docs/refactoring/perf/2026-10-06-s0-noise-floor.md: the P95 of one 40-100 key run varies 3.2x at an
  IDENTICAL config, so the median of run P95s cannot tell configs apart): per (scenario, config) the final-visual latencies of EVERY
  navigation of EVERY ok run are POOLED (read from the run's navs.csv, written by `PhotoReview.Benchmark.Cli --perf-analyze`;
  a run without navs.csv is analysed again to create it) and the P50 and the P95 (nearest rank, like the analyzer) of that pool are
  the ranked numbers, each with a seeded bootstrap 95 % CI over the pooled samples. The per-run P95 (median over runs, from
  summary.json) is kept ONLY as a guard: a config whose run-P95 geometric-mean ratio vs the baseline exceeds -RunP95GuardRatio
  (default 1.5) is flagged and disqualified. Ratios vs the -Baseline config are combined over -GeoScenarios (default S2,S3,S4; scenario =
  prefix of the scenario file name) as a geometric mean, once for P50 and once for P95; -Metric picks which one orders the table.

  A/A NOISE: the config(s) named by -NoiseConfig (default: every id starting with 'default' - the identical-config group of a batch,
  e.g. tools\diag\tune\s0-noise.json or the baseline of a stage) are split into two halves of runs in every balanced way (all splits for
  <= 10 runs, 100 seeded random splits above), the pooled P50/P95 of the halves are compared, and the NOISE of a metric is the median
  relative difference |a-b| / mean(a,b) in percent, averaged over the geo scenarios. -NoisePct overrides it. The estimate is printed and
  written to the report; the half-pool noise matches the noise of comparing two configs that each have about that many runs.

  WINNER RULE (plan section 3): valid (all constraints) AND the -Metric geometric-mean ratio beats the baseline by >= 10 % AND by more than
  2x the A/A noise of that metric AND the OTHER metric does not regress by more than the noise (so a P95 win is not bought with a slower
  P50). Without a noise estimate (-NoisePct absent and no A/A group with >= 2 runs) the Winner column stays empty.

  RESOURCES: runs started by tune-matrix.ps1 carry resources.csv (in-process sampler, 500 ms): the MIN available physical RAM, the peak
  working set, the max GC % and the max CPU % of a run are read from it; the min-available-RAM constraint is evaluated from it (n/a for
  runs without the file). Other inputs per run: summary.json (kindCounts -> non-RamHit share, gcTimePercent, frame/input P95, preload paused
  events), process.json (peak working set) and session.json (errors). Metrics nobody emitted are n/a, never invented.

  Constraints (a config violating any evaluable one is disqualified): zero failed runs, peak WS (max over runs) <= -MaxPeakWsGb, min available
  RAM (min over runs) >= -MinAvailRamGb, GC time % (median) <= -MaxGcPct, frame P95 (median) <= -MaxFrameP95Ms, input P95 (median) <=
  -MaxInputP95Ms, preload-paused-for-memory events = 0, run-P95 guard.
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
    [string[]]$NoiseConfig,
    [double]$RunP95GuardRatio = 1.5,
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
$NoiseConfig = @($NoiseConfig | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if (-not ('TuneStats' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class TuneStats
{
    // Nearest rank on an ASCENDING array: same definition as PerfStats.NearestRank of the analyzer.
    public static double RankOfSorted(double[] sorted, double pct)
    {
        if (sorted.Length == 0) return double.NaN;
        int rank = (int)Math.Ceiling(Math.Min(100.0, Math.Max(0.0, pct)) * sorted.Length / 100.0);
        if (rank < 1) rank = 1; if (rank > sorted.Length) rank = sorted.Length;
        return sorted[rank - 1];
    }
    public static double Percentile(double[] values, double pct)
    {
        var copy = (double[])values.Clone(); Array.Sort(copy); return RankOfSorted(copy, pct);
    }
    // Seeded percentile bootstrap of the pct-percentile of `values` (resampling the pooled samples). Returns { low2.5, high97.5 }.
    public static double[] BootstrapCi(double[] values, double pct, int resamples, int seed)
    {
        if (values.Length == 0) return new double[] { double.NaN, double.NaN };
        if (values.Length == 1) return new double[] { values[0], values[0] };
        var rng = new Random(seed);
        var stats = new double[resamples];
        var pick = new double[values.Length];
        for (int b = 0; b < resamples; b++)
        {
            for (int i = 0; i < pick.Length; i++) pick[i] = values[rng.Next(values.Length)];
            Array.Sort(pick);
            stats[b] = RankOfSorted(pick, pct);
        }
        Array.Sort(stats);
        return new double[] { stats[(int)Math.Floor(0.025 * (resamples - 1))], stats[(int)Math.Ceiling(0.975 * (resamples - 1))] };
    }
}
'@
}

$cliDir = Join-Path $repoRoot 'tools\PhotoReview.Benchmark.Cli\bin\Release'
$cliExe = Get-ChildItem -LiteralPath $cliDir -Filter 'PhotoReview.Benchmark.Cli.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $cliExe -and ($matrix.cells | Where-Object { -not $_.warmup -and $_.status -eq 'ok' -and -not (Test-Path -LiteralPath (Join-Path $_.outDir 'navs.csv')) })) {
    throw "Built PhotoReview.Benchmark.Cli.exe not found under $cliDir (build Release first) and some runs have no summary.json / navs.csv."
}

function F($v, [int]$digits = 1) { if ($null -eq $v -or ($v -is [double] -and [double]::IsNaN($v))) { 'n/a' } else { ([double]$v).ToString("F$digits", $inv) } }
function Csv($v) { if ($null -eq $v -or ($v -is [double] -and [double]::IsNaN($v))) { '' } else { ([IConvertible]$v).ToString($inv) } }
function Get-Median([double[]]$values) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $s = @($values | Sort-Object); $m = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return [double]$s[$m] } else { return ([double]$s[$m - 1] + [double]$s[$m]) / 2 }
}
function Read-Json([string]$path) { if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { $null } }

# navs.csv -> final-visual latencies of the complete navigations of the run's largest group (the scenario group; warm-up files are not in a run dir).
function Read-NavSamples([string]$dir) {
    $path = Join-Path $dir 'navs.csv'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $byGroup = @{}
    foreach ($r in (Import-Csv -LiteralPath $path)) {
        if ($r.complete -ne '1' -or [string]::IsNullOrEmpty($r.finalVisualMs)) { continue }
        if (-not $byGroup.ContainsKey($r.group)) { $byGroup[$r.group] = New-Object System.Collections.Generic.List[double] }
        $byGroup[$r.group].Add([double]::Parse($r.finalVisualMs, $inv))
    }
    if ($byGroup.Count -eq 0) { return , ([double[]]@()) }   # the leading comma stops PowerShell unrolling an empty array into $null
    $best = $byGroup.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending | Select-Object -First 1
    return , ([double[]]$best.Value.ToArray())
}

# resources.csv (in-process sampler of the run) -> min available RAM, peak WS, max GC %, max CPU %, mean page faults/s.
function Read-Resources([string]$dir) {
    $path = Join-Path $dir 'resources.csv'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $rows = @(Import-Csv -LiteralPath $path)
    if ($rows.Count -eq 0) { return $null }
    $avail = @($rows | ForEach-Object { [double]::Parse($_.availPhysMb, $inv) } | Where-Object { -not [double]::IsNaN($_) })
    $rest = @($rows | Select-Object -Skip 1)   # the first row has no interval (rates are 0)
    [pscustomobject]@{
        Samples = $rows.Count
        MinAvailRamGb = $(if ($avail.Count -gt 0) { ($avail | Measure-Object -Minimum).Minimum / 1024 } else { $null })
        PeakWsGb = ($rows | ForEach-Object { [double]::Parse($_.peakWsMb, $inv) } | Measure-Object -Maximum).Maximum / 1024
        MaxGcPct = $(if ($rest.Count -gt 0) { ($rest | ForEach-Object { [double]::Parse($_.gcPct, $inv) } | Measure-Object -Maximum).Maximum } else { $null })
        MaxCpuPct = $(if ($rest.Count -gt 0) { ($rest | ForEach-Object { [double]::Parse($_.cpuPct, $inv) } | Measure-Object -Maximum).Maximum } else { $null })
        PageFaultsPerSec = $(if ($rest.Count -gt 0) { ($rest | ForEach-Object { [double]::Parse($_.pageFaultsPerSec, $inv) } | Measure-Object -Average).Average } else { $null })
    }
}

# ---- per-run rows ------------------------------------------------------------------------------------------------------
$rows = New-Object System.Collections.Generic.List[object]
$samplesByDir = @{}
foreach ($cell in @($matrix.cells | Where-Object { -not $_.warmup })) {
    $dir = [string]$cell.outDir
    $row = [ordered]@{
        scenario = $null; config = $cell.configId; run = $cell.run; status = $cell.status; navSamples = $null
        firstVisualP95Ms = $null; finalVisualP50Ms = $null; finalVisualP95Ms = $null; nonRamHitPct = $null
        peakWsGb = $null; minAvailRamGb = $null; gcPct = $null; maxGcPctSampled = $null; maxCpuPct = $null; pageFaultsPerSec = $null
        frameP95Ms = $null; inputP95Ms = $null
        renderedFrameP95Ms = $null; pausedCount = $null; incompleteNavs = $null; sessionErrors = $null; outDir = $dir
    }
    $row.scenario = ([string]$cell.scenario -split '-')[0].ToUpperInvariant()
    if (-not (Test-Path -LiteralPath $dir)) { $row.status = 'missing-dir'; $rows.Add([pscustomobject]$row); continue }
    $summaryPath = Join-Path $dir 'summary.json'
    $navsPath = Join-Path $dir 'navs.csv'
    if ($cell.status -eq 'ok' -and ($Reanalyze -or -not (Test-Path -LiteralPath $summaryPath) -or -not (Test-Path -LiteralPath $navsPath))) {
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
    $samples = if ($cell.status -eq 'ok') { Read-NavSamples $dir } else { $null }
    if ($null -ne $samples) { $samplesByDir[$dir] = $samples; $row.navSamples = $samples.Count }
    $proc = Read-Json (Join-Path $dir 'process.json')
    if ($proc) { $row.peakWsGb = [double]$proc.peakWorkingSetBytes / 1GB }
    $res = Read-Resources $dir
    if ($res) {
        $row.minAvailRamGb = $res.MinAvailRamGb; $row.maxGcPctSampled = $res.MaxGcPct; $row.maxCpuPct = $res.MaxCpuPct; $row.pageFaultsPerSec = $res.PageFaultsPerSec
        if ($null -eq $row.peakWsGb -or $res.PeakWsGb -gt $row.peakWsGb) { $row.peakWsGb = $res.PeakWsGb }
    }
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
$noSamples = @($rows | Where-Object { $_.status -eq 'ok' -and $null -eq $_.navSamples })
if ($noSamples.Count -gt 0) { Write-Host "WARNING: $($noSamples.Count) ok run(s) have no per-navigation samples (navs.csv); they are left out of the pooled statistics." -ForegroundColor Yellow }

# ---- aggregation (pooled per-navigation latencies) -----------------------------------------------------------------------------
$configIds = @($rows | ForEach-Object { $_.config } | Select-Object -Unique)
if ($Baseline -notin $configIds) { throw "Baseline config '$Baseline' not in batch (configs: $($configIds -join ', '))" }
$scenarios = @($rows | ForEach-Object { $_.scenario } | Select-Object -Unique | Sort-Object)
$agg = @{}   # "scenario|config" -> object
$cellSeed = $Seed
foreach ($sc in $scenarios) {
    foreach ($cfg in $configIds) {
        $runs = @($rows | Where-Object { $_.scenario -eq $sc -and $_.config -eq $cfg })
        if ($runs.Count -eq 0) { continue }
        $okRuns = @($runs | Where-Object { $_.status -eq 'ok' -and $samplesByDir.ContainsKey($_.outDir) -and $samplesByDir[$_.outDir].Count -gt 0 })
        $pool = New-Object System.Collections.Generic.List[double]
        foreach ($r in $okRuns) { $pool.AddRange($samplesByDir[$r.outDir]) }
        $arr = $pool.ToArray()
        $p50 = [TuneStats]::Percentile($arr, 50); $p95 = [TuneStats]::Percentile($arr, 95)
        $ci50 = [TuneStats]::BootstrapCi($arr, 50, $BootstrapResamples, $cellSeed++)
        $ci95 = [TuneStats]::BootstrapCi($arr, 95, $BootstrapResamples, $cellSeed++)
        $runP95s = [double[]]@($runs | Where-Object { $_.status -eq 'ok' -and $null -ne $_.finalVisualP95Ms } | ForEach-Object { [double]$_.finalVisualP95Ms })
        $agg["$sc|$cfg"] = [pscustomobject]@{
            Scenario = $sc; Config = $cfg; Runs = $runs.Count; OkRuns = $okRuns.Count; N = $arr.Count
            P50 = $p50; P50Low = $ci50[0]; P50High = $ci50[1]; P95 = $p95; P95Low = $ci95[0]; P95High = $ci95[1]
            RunP95Median = (Get-Median $runP95s); RunP95Min = $(if ($runP95s.Count) { ($runP95s | Measure-Object -Minimum).Minimum } else { $null }); RunP95Max = $(if ($runP95s.Count) { ($runP95s | Measure-Object -Maximum).Maximum } else { $null })
        }
    }
}
function Get-Agg($sc, $cfg) { $agg["$sc|$cfg"] }
function Get-Value($a, [string]$metric) { if (-not $a) { return $null }; if ($metric -eq 'p50') { $a.P50 } else { $a.P95 } }

# ---- A/A noise ---------------------------------------------------------------------------------------------------------------------
$aaIds = if ($NoiseConfig.Count -gt 0) { @($NoiseConfig | Where-Object { $_ -in $configIds }) } else { @($configIds | Where-Object { $_ -like 'default*' }) }
$noiseRows = New-Object System.Collections.Generic.List[object]
$noiseRng = New-Object System.Random($Seed + 7)
foreach ($sc in $GeoScenarios) {
    foreach ($cfg in $aaIds) {
        $runs = @($rows | Where-Object { $_.scenario -eq $sc -and $_.config -eq $cfg -and $_.status -eq 'ok' -and $samplesByDir.ContainsKey($_.outDir) -and $samplesByDir[$_.outDir].Count -gt 0 })
        $n = $runs.Count
        if ($n -lt 2) { continue }
        $half = [int][math]::Floor($n / 2)
        $splits = New-Object System.Collections.Generic.List[object]
        if ($n -le 10) {
            for ($mask = 1; $mask -lt (1 -shl $n); $mask++) {
                $bits = 0; for ($i = 0; $i -lt $n; $i++) { if ($mask -band (1 -shl $i)) { $bits++ } }
                if ($bits -ne $half) { continue }
                if ($n % 2 -eq 0 -and -not ($mask -band 1)) { continue }   # even n: A and B are mirror images; keep one of each pair
                $splits.Add($mask)
            }
        }
        else {
            for ($s = 0; $s -lt 100; $s++) {
                $perm = @(0..($n - 1) | Sort-Object { $noiseRng.Next() }); $mask = 0
                foreach ($i in $perm[0..($half - 1)]) { $mask = $mask -bor (1 -shl $i) }
                $splits.Add($mask)
            }
        }
        $d50 = New-Object System.Collections.Generic.List[double]; $d95 = New-Object System.Collections.Generic.List[double]
        foreach ($mask in $splits) {
            $pa = New-Object System.Collections.Generic.List[double]; $pb = New-Object System.Collections.Generic.List[double]
            for ($i = 0; $i -lt $n; $i++) {
                if ($mask -band (1 -shl $i)) { $pa.AddRange($samplesByDir[$runs[$i].outDir]) } else { $pb.AddRange($samplesByDir[$runs[$i].outDir]) }   # not `$t = if ... { $emptyList }`: PowerShell unrolls an empty list to $null
            }
            $a = $pa.ToArray(); $b = $pb.ToArray()
            foreach ($pair in @(@(50, $d50), @(95, $d95))) {
                $x = [TuneStats]::Percentile($a, $pair[0]); $y = [TuneStats]::Percentile($b, $pair[0])
                if (($x + $y) -gt 0) { $pair[1].Add(200.0 * [math]::Abs($x - $y) / ($x + $y)) }
            }
        }
        $noiseRows.Add([pscustomobject]@{ Scenario = $sc; Config = $cfg; Runs = $n; Splits = $splits.Count; P50Pct = (Get-Median $d50.ToArray()); P95Pct = (Get-Median $d95.ToArray()) })
    }
}
$noiseEst = @{ p50 = $null; p95 = $null }
foreach ($m in 'p50', 'p95') {
    $vals = @($noiseRows | ForEach-Object { if ($m -eq 'p50') { $_.P50Pct } else { $_.P95Pct } } | Where-Object { $null -ne $_ })
    if ($vals.Count -gt 0) { $noiseEst[$m] = ($vals | Measure-Object -Average).Average }
}
$noiseGiven = $PSBoundParameters.ContainsKey('NoisePct')
$noiseMetric = if ($noiseGiven) { $NoisePct } else { $noiseEst[$Metric] }
$noiseOther = if ($noiseGiven) { $NoisePct } else { $noiseEst[$(if ($Metric -eq 'p95') { 'p50' } else { 'p95' })] }
if ($noiseGiven) { Write-Host "A/A noise: $NoisePct % (given with -NoisePct); estimated from the batch: P50 $(F $noiseEst.p50 1) %, P95 $(F $noiseEst.p95 1) %" }
elseif ($null -ne $noiseMetric) { Write-Host "A/A noise estimate (id(s) $($aaIds -join ', '), $($noiseRows.Count) scenario x config group(s)): P50 $(F $noiseEst.p50 1) %, P95 $(F $noiseEst.p95 1) % -> winner rule uses $(F $noiseMetric 1) % ($Metric)" }
else { Write-Host 'A/A noise: n/a (no A/A group with >= 2 runs and no -NoisePct); the Winner column will stay empty.' -ForegroundColor Yellow }

# ---- constraints + summary ---------------------------------------------------------------------------------------------------------
function Get-GeoRatio([string]$cfg, [scriptblock]$value) {
    $ratios = @()
    foreach ($sc in $GeoScenarios) {
        $a = Get-Agg $sc $cfg; $b = Get-Agg $sc $Baseline
        $va = if ($a) { & $value $a } else { $null }; $vb = if ($b) { & $value $b } else { $null }
        if ($null -ne $va -and $null -ne $vb -and -not [double]::IsNaN($va) -and -not [double]::IsNaN($vb) -and $vb -gt 0) { $ratios += ($va / $vb) }
    }
    if ($ratios.Count -eq 0) { return @($null, 0) }
    return @([math]::Exp((($ratios | ForEach-Object { [math]::Log($_) }) | Measure-Object -Average).Average), $ratios.Count)
}

function Get-Constraint($cfgRows, $guardGeo) {
    $maxWs = $null; $viol = @(); $na = @()
    $fail = @($cfgRows | Where-Object { $_.status -ne 'ok' -or ($null -ne $_.sessionErrors -and $_.sessionErrors -gt 0) }).Count   # incompleteNavs stays informational (a nav still in flight at scenario end is not a failed image)
    if ($fail -gt 0) { $viol += "failures=$fail" }
    $wsVals = @($cfgRows | Where-Object { $null -ne $_.peakWsGb } | ForEach-Object { [double]$_.peakWsGb })
    if ($wsVals.Count -gt 0) { $maxWs = ($wsVals | Measure-Object -Maximum).Maximum; if ($maxWs -gt $MaxPeakWsGb) { $viol += "peakWS $(F $maxWs 1) GB > $MaxPeakWsGb" } } else { $na += 'peakWS' }
    $avail = @($cfgRows | Where-Object { $null -ne $_.minAvailRamGb } | ForEach-Object { [double]$_.minAvailRamGb })
    $minAvail = $null
    if ($avail.Count -gt 0) { $minAvail = ($avail | Measure-Object -Minimum).Minimum; if ($minAvail -lt $MinAvailRamGb) { $viol += "minAvailRAM $(F $minAvail 1) GB < $MinAvailRamGb" } } else { $na += 'minAvailRam' }
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
    if ($null -ne $guardGeo -and $guardGeo -gt $RunP95GuardRatio) { $viol += "run-P95 guard $(F $guardGeo 2)x baseline > $RunP95GuardRatio" }
    [pscustomobject]@{ Valid = ($viol.Count -eq 0); Violations = $viol; NotAvailable = $na; MaxPeakWsGb = $maxWs; MinAvailRamGb = $minAvail; GcMedian = $gcMed; FrameMedian = $frMed; InputMedian = $inpMed }
}

$summary = foreach ($cfg in $configIds) {
    $cfgRows = @($rows | Where-Object { $_.config -eq $cfg })
    $g50 = Get-GeoRatio $cfg { param($a) $a.P50 }
    $g95 = Get-GeoRatio $cfg { param($a) $a.P95 }
    $gGuard = Get-GeoRatio $cfg { param($a) $a.RunP95Median }
    $con = Get-Constraint $cfgRows $gGuard[0]
    $geo = if ($Metric -eq 'p50') { $g50[0] } else { $g95[0] }
    $geoOther = if ($Metric -eq 'p50') { $g95[0] } else { $g50[0] }
    $win = ''
    if ($null -ne $geo -and $cfg -ne $Baseline -and $con.Valid -and $null -ne $noiseMetric -and $null -ne $noiseOther -and $null -ne $geoOther) {
        $gain = (1 - $geo) * 100
        if ($gain -ge 10 -and $gain -gt 2 * $noiseMetric -and $geoOther -le (1 + $noiseOther / 100)) { $win = 'yes' }
    }
    [pscustomobject]@{
        Config = $cfg; Runs = $cfgRows.Count; Constraint = $con; Geo = $geo; GeoOther = $geoOther; Geo50 = $g50[0]; Geo95 = $g95[0]; GuardGeo = $gGuard[0]
        GeoParts = $(if ($Metric -eq 'p50') { $g50[1] } else { $g95[1] }); Winner = $win
    }
}
# Pareto: among valid configs with a geo ratio, non-dominated on (geo ratio, peak WS), both lower-is-better.
$cand = @($summary | Where-Object { $_.Constraint.Valid -and $null -ne $_.Geo -and $null -ne $_.Constraint.MaxPeakWsGb })
$pareto = @($cand | Where-Object {
        $c = $_
        -not ($cand | Where-Object { $_.Config -ne $c.Config -and $_.Geo -le $c.Geo -and $_.Constraint.MaxPeakWsGb -le $c.Constraint.MaxPeakWsGb -and ($_.Geo -lt $c.Geo -or $_.Constraint.MaxPeakWsGb -lt $c.Constraint.MaxPeakWsGb) })
    } | ForEach-Object { $_.Config })

# ---- report ---------------------------------------------------------------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
function Out-Line([string]$s = '') { [void]$sb.AppendLine($s) }
$firstEnv = @($matrix.sessions)[0].environment
Out-Line "# Tune ranking: $($matrix.created)"
Out-Line ''
Out-Line "- Batch: ``$BatchDir`` | stage ``$(Split-Path -Leaf $matrix.stage)`` (hash $($matrix.stageHash)) | seed $($matrix.seed) | repeat $($matrix.repeat)"
Out-Line "- Fixture $($matrix.fixtureAlias): $($matrix.fixtureFingerprint.Count) files, $($matrix.fixtureFingerprint.Bytes) bytes, fingerprint $($matrix.fixtureFingerprint.Fingerprint); changed mid-batch: $($matrix.fixtureChanged)"
Out-Line "- Commit $($matrix.commit) | CLI $($matrix.cliVersion) | cold disk cache: $($matrix.coldDiskCache) | cooldown $($matrix.cooldownSeconds) s"
if ($firstEnv) { Out-Line "- Environment at start: AC=$($firstEnv.onAcPower), plan=$($firstEnv.powerPlan), free RAM $($firstEnv.freeRamGb) GB, CPU idle $($firstEnv.cpuIdlePercentAvg) %$(if ($firstEnv.warnings) { '; warnings: ' + ($firstEnv.warnings -join '; ') })" }
Out-Line "- Ranking: P50 and P95 of the POOLED per-navigation final-visual latencies of all ok runs of a config (bootstrap 95 % CI over the pooled samples, $BootstrapResamples resamples, seed $Seed); table ordered by the $Metric geometric-mean ratio vs baseline ``$Baseline`` over $($GeoScenarios -join ', '); run-P95 is a guard only (> $RunP95GuardRatio x baseline disqualifies)"
if ($matrix.fixtureChanged) { Out-Line ''; Out-Line '**WARNING: the fixture changed mid-batch; all numbers are suspect.**' }
Out-Line ''
Out-Line '## A/A noise'
Out-Line ''
if ($noiseRows.Count -gt 0) {
    Out-Line "Identical-config group(s) $($aaIds -join ', '): runs split into two halves, relative difference of the pooled percentile of the halves (median over splits). Estimate: **P50 $(F $noiseEst.p50 1) %, P95 $(F $noiseEst.p95 1) %**$(if ($noiseGiven) { "; overridden by -NoisePct $NoisePct %" })."
    Out-Line ''
    Out-Line '| Scenario | Config | Runs | Splits | P50 diff % | P95 diff % |'
    Out-Line '|---|---|---|---|---|---|'
    foreach ($n in $noiseRows) { Out-Line ("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $n.Scenario, $n.Config, $n.Runs, $n.Splits, (F $n.P50Pct 1), (F $n.P95Pct 1)) }
}
else {
    Out-Line "No A/A estimate: no identical-config group with >= 2 runs (looked for: $(if ($aaIds.Count) { $aaIds -join ', ' } else { 'ids starting with default / -NoiseConfig' }))$(if ($noiseGiven) { "; -NoisePct $NoisePct % was given" })."
}
Out-Line ''
Out-Line '## Ranking (valid configs first, best geometric-mean ratio first; ratio < 1 is faster than baseline)'
Out-Line ''
Out-Line "| Config | Runs | Geo $Metric ratio | Geo other-metric ratio | Run-P95 guard | Peak WS GB (max) | Min avail RAM GB | GC % (med) | Frame P95 ms | Input P95 ms | Valid | Violations | Not available | Pareto | Winner |"
Out-Line '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|'
$ordered = @($summary | Sort-Object @{ Expression = { -not $_.Constraint.Valid } }, @{ Expression = { if ($null -eq $_.Geo) { [double]::MaxValue } else { $_.Geo } } })
foreach ($s in $ordered) {
    $c = $s.Constraint
    Out-Line ("| {0} | {1} | {2}{3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} |" -f $s.Config, $s.Runs, (F $s.Geo 3), $(if ($s.GeoParts -lt $GeoScenarios.Count -and $null -ne $s.Geo) { " (partial $($s.GeoParts)/$($GeoScenarios.Count))" } else { '' }),
        (F $s.GeoOther 3), (F $s.GuardGeo 2), (F $c.MaxPeakWsGb 2), (F $c.MinAvailRamGb 1), (F $c.GcMedian 1), (F $c.FrameMedian 1), (F $c.InputMedian 1), $(if ($c.Valid) { 'yes' } else { 'NO' }),
        ($c.Violations -join '; '), ($c.NotAvailable -join ', '), $(if ($s.Config -in $pareto) { 'yes' } else { '' }), $s.Winner)
}
Out-Line ''
Out-Line '## Per scenario (pooled final-visual latency, ms)'
foreach ($sc in $scenarios) {
    Out-Line ''
    Out-Line "### $sc"
    Out-Line ''
    Out-Line "| Config | Runs (ok) | Pooled N | P50 | P50 95 % CI | P50 ratio | P95 | P95 95 % CI | P95 ratio | Run-P95 median (min-max) |"
    Out-Line '|---|---|---|---|---|---|---|---|---|---|'
    $b = Get-Agg $sc $Baseline
    foreach ($cfg in $configIds) {
        $a = Get-Agg $sc $cfg; if (-not $a) { continue }
        $r50 = if ($b -and $b.P50 -gt 0 -and -not [double]::IsNaN($a.P50)) { $a.P50 / $b.P50 } else { $null }
        $r95 = if ($b -and $b.P95 -gt 0 -and -not [double]::IsNaN($a.P95)) { $a.P95 / $b.P95 } else { $null }
        Out-Line ("| {0} | {1} ({2}) | {3} | {4} | {5} - {6} | {7} | {8} | {9} - {10} | {11} | {12} ({13} - {14}) |" -f $cfg, $a.Runs, $a.OkRuns, $a.N, (F $a.P50 2), (F $a.P50Low 2), (F $a.P50High 2), (F $r50 3),
            (F $a.P95 2), (F $a.P95Low 2), (F $a.P95High 2), (F $r95 3), (F $a.RunP95Median 2), (F $a.RunP95Min 2), (F $a.RunP95Max 2))
    }
}
Out-Line ''
Out-Line "## Pareto: speed (geo $Metric ratio) vs peak RAM, valid configs only"
Out-Line ''
Out-Line '| Config | Geo ratio | Peak WS GB (max) | On front |'
Out-Line '|---|---|---|---|'
foreach ($s in @($cand | Sort-Object Geo)) { Out-Line ("| {0} | {1} | {2} | {3} |" -f $s.Config, (F $s.Geo 3), (F $s.Constraint.MaxPeakWsGb 2), $(if ($s.Config -in $pareto) { 'yes' } else { '' })) }
Out-Line ''
Out-Line '## Data notes'
Out-Line ''
Out-Line '- Pooled statistics treat every navigation as one sample; navigations of one run are correlated (same cache state), so the bootstrap CI is optimistic: it covers sampling noise inside the runs, the A/A noise above covers run-to-run noise.'
Out-Line '- Min available RAM / peak WS / max GC % / max CPU % come from resources.csv (in-process sampler, 500 ms). A constraint listed under "Not available" had no data for that config.'
Out-Line '- Frame P95 comes from `frameTimeP95Ms` of the analyzer and is n/a for runs without frame-time data; input P95 is read from the R-THREAD evidence text (`t_input P95`).'
Out-Line '- Visual-output identity versus baseline (hash/size check) is not part of this script.'
if ($noiseGiven) { Out-Line "- Winner rule applied with the given noise $NoisePct %: >= 10 % better than baseline, > 2x noise, other metric not worse than the noise." }
elseif ($null -ne $noiseMetric) { Out-Line "- Winner rule applied with the A/A noise of the batch ($(F $noiseMetric 1) % for $Metric): >= 10 % better than baseline, > 2x noise, other metric ($(F $noiseOther 1) % noise) not worse than that." }
else { Out-Line '- Winner column is empty: no A/A noise (give -NoisePct or include an identical-config group with id default*).' }
$reportPath = Join-Path $OutDir 'report.md'
[System.IO.File]::WriteAllText($reportPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $reportPath"
