<#
.SYNOPSIS
  Summarize a Process Monitor CSV export for PhotoReview.App.exe: per-source-image
  CreateFile / ReadFile / stat counts, plus separate counters for the app's own
  cache/thumbnail/session/journal files.

.PARAMETER Csv        Path to the Procmon CSV export (Procmon64.exe /OpenLog ... /SaveAs ...csv)
.PARAMETER Out        Path to write the per-file summary CSV
.PARAMETER ShownFiles Optional: ordered list of the first N source image paths that were actually
                       displayed via Next (vs. only preloaded). If omitted, this script has no way
                       to know which files were "shown" vs. "preload only", and the shown/preload
                       split in the printed summary will say so.

.NOTES
  Procmon CSV columns (default export): "Time of Day","Process Name","PID","Operation","Path",
  "Result","Detail". Byte counts are parsed out of Detail via "Length: <n>".
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Csv,
    [Parameter(Mandatory)] [string]$Out,
    [string[]]$ShownFiles = @()
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Csv)) { throw "CSV not found: $Csv" }

$rows = Import-Csv -LiteralPath $Csv
$rows = $rows | Where-Object { $_.'Process Name' -eq 'PhotoReview.App.exe' }

function Get-LengthBytes {
    param([string]$Detail)
    if ($Detail -match 'Length:\s*([\d,]+)') {
        return [int64]($Matches[1] -replace ',', '')
    }
    return 0
}

function Get-Category {
    param([string]$Path)
    if ($Path -match '\\cache\\[^\\]+\.pv4$') { return 'cache-pv4' }
    if ($Path -match '\\thumbnails\\[^\\]+\.png$') { return 'thumbnail-png' }
    if ($Path -match '\\Sessions\\[^\\]+\.json$') { return 'session-json' }
    if ($Path -match 'operations\.jsonl$') { return 'operations-jsonl' }
    if ($Path -match '\.(jpe?g|png|tiff?|bmp)$') { return 'source-image' }
    return 'other'
}

$statOps = @('QueryBasicInformationFile', 'QueryNetworkOpenInformationFile', 'QueryAllInformationFile')

$byPath = @{}
$catCounts = @{
    'cache-pv4' = [ordered]@{ CreateFile = 0; ReadFile = 0; Bytes = 0; Stat = 0 }
    'thumbnail-png' = [ordered]@{ CreateFile = 0; ReadFile = 0; Bytes = 0; Stat = 0 }
    'session-json' = [ordered]@{ CreateFile = 0; ReadFile = 0; Bytes = 0; Stat = 0 }
    'operations-jsonl' = [ordered]@{ CreateFile = 0; ReadFile = 0; Bytes = 0; Stat = 0 }
}

foreach ($r in $rows) {
    $op = $r.Operation
    $path = $r.Path
    if ([string]::IsNullOrWhiteSpace($path)) { continue }
    $cat = Get-Category -Path $path

    if ($cat -eq 'source-image') {
        if (-not $byPath.ContainsKey($path)) {
            $byPath[$path] = [ordered]@{ Path = $path; CreateFile = 0; ReadFile = 0; Bytes = 0; Stat = 0 }
        }
        $entry = $byPath[$path]
        if ($op -eq 'CreateFile') { $entry.CreateFile++ }
        elseif ($op -eq 'ReadFile') { $entry.ReadFile++; $entry.Bytes += (Get-LengthBytes -Detail $r.Detail) }
        elseif ($statOps -contains $op) { $entry.Stat++ }
    }
    elseif ($catCounts.ContainsKey($cat)) {
        $c = $catCounts[$cat]
        if ($op -eq 'CreateFile') { $c.CreateFile++ }
        elseif ($op -eq 'ReadFile') { $c.ReadFile++; $c.Bytes += (Get-LengthBytes -Detail $r.Detail) }
        elseif ($statOps -contains $op) { $c.Stat++ }
    }
}

$perFileRows = $byPath.Values | ForEach-Object { [pscustomobject]$_ }
$perFileRows | Export-Csv -LiteralPath $Out -NoTypeInformation -Encoding UTF8

Write-Output "== Source image file access (PhotoReview.App.exe) =="
Write-Output ("{0,-60} {1,10} {2,10} {3,14} {4,8}" -f 'Path', 'CreateFile', 'ReadFile', 'Bytes', 'Stat')
foreach ($e in ($perFileRows | Sort-Object Path)) {
    Write-Output ("{0,-60} {1,10} {2,10} {3,14} {4,8}" -f $e.Path, $e.CreateFile, $e.ReadFile, $e.Bytes, $e.Stat)
}

Write-Output ""
Write-Output "== App cache / session / journal access =="
foreach ($k in $catCounts.Keys) {
    $c = $catCounts[$k]
    Write-Output ("{0,-18} CreateFile={1,-6} ReadFile={2,-6} Bytes={3,-12} Stat={4}" -f $k, $c.CreateFile, $c.ReadFile, $c.Bytes, $c.Stat)
}

Write-Output ""
if ($ShownFiles.Count -gt 0) {
    $shownSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$ShownFiles, [System.StringComparer]::OrdinalIgnoreCase)
    $shown = @($perFileRows | Where-Object { $shownSet.Contains($_.Path) })
    $preloadOnly = @($perFileRows | Where-Object { -not $shownSet.Contains($_.Path) })

    function Write-Avg {
        param([string]$Name, [object[]]$Set)
        if ($Set.Count -eq 0) { Write-Output "$Name : n=0"; return }
        $avgCreate = ($Set | Measure-Object CreateFile -Average).Average
        $avgRead = ($Set | Measure-Object ReadFile -Average).Average
        $avgBytes = ($Set | Measure-Object Bytes -Average).Average
        $avgStat = ($Set | Measure-Object Stat -Average).Average
        Write-Output ("{0} : n={1} avgCreateFile={2:N2} avgReadFile={3:N2} avgBytes={4:N0} avgStat={5:N2}" -f $Name, $Set.Count, $avgCreate, $avgRead, $avgBytes, $avgStat)
    }
    Write-Output "== Average per image =="
    Write-Avg -Name 'Shown (first N via Next)' -Set $shown
    Write-Avg -Name 'Preload-only'              -Set $preloadOnly
}
else {
    Write-Output "== Average per image =="
    Write-Output "-ShownFiles not provided: cannot split shown-vs-preload-only. Pass the first 10 displayed source paths (in folder order) via -ShownFiles to get this breakdown."
    $all = @($perFileRows)
    if ($all.Count -gt 0) {
        $avgCreate = ($all | Measure-Object CreateFile -Average).Average
        $avgRead = ($all | Measure-Object ReadFile -Average).Average
        $avgBytes = ($all | Measure-Object Bytes -Average).Average
        $avgStat = ($all | Measure-Object Stat -Average).Average
        Write-Output ("All source images : n={0} avgCreateFile={1:N2} avgReadFile={2:N2} avgBytes={3:N0} avgStat={4:N2}" -f $all.Count, $avgCreate, $avgRead, $avgBytes, $avgStat)
    }
}

Write-Output ""
Write-Output "Per-file CSV written to: $Out"
