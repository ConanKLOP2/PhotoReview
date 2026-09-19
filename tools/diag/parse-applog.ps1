<#
.SYNOPSIS
  Parse a PhotoReview.App app.log produced with LoggingEnabled=true and summarize
  ShowImage navigation timings by token, plus Explorer/preload counters.

.PARAMETER Log  Path to app.log
.PARAMETER Out  Path to write the per-token CSV

.NOTES
  Log line format (see PhotoReview.App/AppLog.cs):
    yyyy-MM-dd HH:mm:ss.fff [LEVEL] [TnnN] Message

  Percentiles use nearest-rank (ceil(p/100*N)), which is called out explicitly in the
  printed summary because sample sizes here are small (tens, not thousands).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Log,
    [Parameter(Mandatory)] [string]$Out
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Log)) { throw "Log not found: $Log" }

$tsRegex = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<level>[A-Z]+)\] \[T(?<thread>\d+)\] (?<msg>.*)$'

$lines = Get-Content -Path $Log
$parsed = foreach ($line in $lines) {
    if ($line -match $tsRegex) {
        [pscustomobject]@{
            Ts    = [datetime]::ParseExact($Matches.ts, 'yyyy-MM-dd HH:mm:ss.fff', [System.Globalization.CultureInfo]::InvariantCulture)
            Level = $Matches.level
            Thread = $Matches.thread
            Msg   = $Matches.msg
        }
    }
}

# ---------------------------------------------------------------------------
# Group ShowImage lines by token
# ---------------------------------------------------------------------------
$tokens = @{}
foreach ($p in $parsed) {
    $m = $p.Msg
    if ($m -match '^ShowImage (?<phase>start|cache-state|thumbnail-presented|preview-presented|stale-file)\b.*?\btoken=(?<token>\d+)') {
        $token = $Matches.token
        $phase = $Matches.phase
        if (-not $tokens.ContainsKey($token)) {
            $tokens[$token] = [ordered]@{
                Token = $token
                StartTs = $null
                CacheStateTs = $null
                RamReady = $null
                ThumbTs = $null
                PreviewTs = $null
                StaleTs = $null
                Path = $null
                Mode = $null
            }
        }
        $entry = $tokens[$token]
        switch ($phase) {
            'start' {
                $entry.StartTs = $p.Ts
                if ($m -match 'path=(?<path>.+?)(?=(?:\s+(?:ramReady|cacheBytes|mode)=)|$)') { $entry.Path = $Matches.path }
            }
            'cache-state' {
                $entry.CacheStateTs = $p.Ts
                if ($m -match 'ramReady=(?<rr>True|False)') { $entry.RamReady = $Matches.rr }
            }
            'thumbnail-presented' { $entry.ThumbTs = $p.Ts }
            'preview-presented' {
                $entry.PreviewTs = $p.Ts
                if ($m -match 'mode=(?<mode>\w+)') { $entry.Mode = $Matches.mode }
            }
            'stale-file' { $entry.StaleTs = $p.Ts }
        }
    }
}

$rows = foreach ($t in $tokens.Values) {
    $startToCache = if ($t.StartTs -and $t.CacheStateTs) { ($t.CacheStateTs - $t.StartTs).TotalMilliseconds } else { $null }
    $startToThumb = if ($t.StartTs -and $t.ThumbTs) { ($t.ThumbTs - $t.StartTs).TotalMilliseconds } else { $null }
    $startToPreview = if ($t.StartTs -and $t.PreviewTs) { ($t.PreviewTs - $t.StartTs).TotalMilliseconds } else { $null }
    [pscustomobject]@{
        Token           = $t.Token
        Path            = $t.Path
        Mode            = $t.Mode
        RamReady        = $t.RamReady
        StartToCacheMs  = $startToCache
        StartToThumbMs  = $startToThumb
        StartToPreviewMs= $startToPreview
        Stale           = [bool]$t.StaleTs
    }
}

$rows | Sort-Object { [int]$_.Token } | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8

# ---------------------------------------------------------------------------
# Counters: Preload paused, Preload progress, Explorer lines
# ---------------------------------------------------------------------------
$preloadPaused = @($parsed | Where-Object { $_.Msg -like 'Preload paused for memory*' }).Count
$preloadProgress = @($parsed | Where-Object { $_.Msg -like 'Preload progress*' }).Count
$explorerLines = @($parsed | Where-Object { $_.Msg -like 'Explorer *' })
$explorerQueryStart = @($explorerLines | Where-Object { $_.Msg -like 'Explorer query-start*' }).Count
$explorerNativeReadComplete = @($explorerLines | Where-Object { $_.Msg -like 'Explorer native-read-complete*' }).Count
$explorerNativeOrderApplied = @($explorerLines | Where-Object { $_.Msg -like 'Explorer native order applied*' }).Count
$explorerViewFallback = @($explorerLines | Where-Object { $_.Msg -like 'Explorer view fallback*' }).Count

# ---------------------------------------------------------------------------
# T0 -> first preview-presented (approx T0->T2)
# ---------------------------------------------------------------------------
$loadFolderStart = ($parsed | Where-Object { $_.Msg -like 'LoadFolder start*' } | Select-Object -First 1).Ts
$firstPreview = ($rows | Where-Object { $_.StartToPreviewMs -ne $null } | Sort-Object { [int]$_.Token } | Select-Object -First 1)
$t0t2 = $null
if ($loadFolderStart -and $firstPreview) {
    $firstPreviewTs = $tokens[$firstPreview.Token].PreviewTs
    $t0t2 = ($firstPreviewTs - $loadFolderStart).TotalMilliseconds
}

# ---------------------------------------------------------------------------
# Percentile helper: nearest-rank
# ---------------------------------------------------------------------------
function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    $rank = [Math]::Ceiling(($Percentile / 100.0) * $n)
    if ($rank -lt 1) { $rank = 1 }
    if ($rank -gt $n) { $rank = $n }
    return $sorted[$rank - 1]
}

function Write-Group {
    param([string]$Name, [object[]]$Group)
    $vals = $Group | ForEach-Object { $_.StartToPreviewMs } | Where-Object { $_ -ne $null }
    $valsD = @($vals | ForEach-Object { [double]$_ })
    $n = $valsD.Count
    $p50 = Get-Percentile -Values $valsD -Percentile 50
    $p95 = Get-Percentile -Values $valsD -Percentile 95
    $max = if ($n -gt 0) { ($valsD | Measure-Object -Maximum).Maximum } else { $null }
    "{0,-12} n={1,-4} P50={2,-8} P95={3,-8} max={4}" -f $Name, $n, ([math]::Round($p50,1)), ([math]::Round($p95,1)), $max
}

Write-Output "== ShowImage start->preview-presented (ms), nearest-rank percentile =="
Write-Output (Write-Group -Name 'ramReady=True'  -Group ($rows | Where-Object { $_.RamReady -eq 'True' }))
Write-Output (Write-Group -Name 'ramReady=False' -Group ($rows | Where-Object { $_.RamReady -eq 'False' }))
Write-Output (Write-Group -Name 'all'             -Group $rows)
Write-Output ""
Write-Output "Preload paused for memory: $preloadPaused"
Write-Output "Preload progress: $preloadProgress"
Write-Output "Explorer query-start: $explorerQueryStart"
Write-Output "Explorer native-read-complete: $explorerNativeReadComplete"
Write-Output "Explorer native order applied: $explorerNativeOrderApplied"
Write-Output "Explorer view fallback: $explorerViewFallback"
if ($t0t2 -ne $null) { Write-Output ("LoadFolder start -> first preview-presented (T0~T2): {0} ms" -f [math]::Round($t0t2,1)) }
Write-Output ""
Write-Output "CSV written to: $Out"
