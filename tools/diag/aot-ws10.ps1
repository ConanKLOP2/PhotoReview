<#
.SYNOPSIS
  Working set 10 s after the first image of a PUBLISHED PhotoReview.exe (one fresh process per run, interleaved over -Exes).
  Run on a private desktop via tools/diag/aot-hidden-run.ps1. Prints one JSON line per run: label, working set / private bytes (MB)
  10 s after RenderedFrame, and the time to RenderedFrame measured from the process start (ms).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Exes,     # label=path
    [Parameter(Mandatory)][string]$PhotoFolder,
    [int]$Runs = 3
)
$ErrorActionPreference = 'Stop'
foreach ($run in 1..$Runs) {
    foreach ($spec in ($Exes -join ';').Split(';')) {
        $label, $exe = $spec.Trim().Split('=', 2)
        $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
        $root = Join-Path $env:TEMP "aotws-$id"; $photos = Join-Path $root 'photos'; $trace = Join-Path $root 'perf'
        New-Item -ItemType Directory -Force $photos | Out-Null
        Get-ChildItem -LiteralPath $PhotoFolder -Filter *.jpg -File | Select-Object -First 10 | Copy-Item -Destination $photos
        $first = (Get-ChildItem $photos -File | Select-Object -First 1).FullName
        $psi = New-Object Diagnostics.ProcessStartInfo $exe
        $psi.UseShellExecute = $false; $psi.Arguments = '"' + $first + '"'; $psi.WorkingDirectory = Split-Path $exe
        $psi.EnvironmentVariables['PHOTOREVIEW_DATA_ROOT'] = Join-Path $root 'data'
        $psi.EnvironmentVariables['PHOTOREVIEW_ISOLATE_CONFIG'] = '1'
        $psi.EnvironmentVariables['PHOTOREVIEW_PERF_TRACE'] = $trace
        $psi.EnvironmentVariables['PHOTOREVIEW_SHELL_INSTANCE_PREFIX'] = "AotWs$id"
        $p = [Diagnostics.Process]::Start($psi)
        try {
            $deadline = (Get-Date).AddSeconds(40); $rendered = $false
            while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not $rendered) {
                Start-Sleep -Milliseconds 50
                if (Test-Path $trace) {
                    foreach ($f in Get-ChildItem $trace -File) {
                        try {
                            $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
                            $t = (New-Object IO.StreamReader $fs).ReadToEnd(); $fs.Dispose()
                            if ($t -match ',RenderedFrame,') { $rendered = $true }
                        } catch { }
                    }
                }
            }
            $ms = [int]((Get-Date) - $p.StartTime).TotalMilliseconds
            Start-Sleep -Seconds 10
            $p.Refresh()
            $line = [ordered]@{ run = $run; label = $label; ok = $rendered; renderedFromStartMs = $ms; wsMb = [math]::Round($p.WorkingSet64 / 1MB, 1); privateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1); peakWsMb = [math]::Round($p.PeakWorkingSet64 / 1MB, 1) }
            $line | ConvertTo-Json -Compress
            [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
        } finally {
            if (-not $p.HasExited) { try { $p.Kill() } catch { } }
            Start-Sleep -Milliseconds 200
            Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
        }
    }
}

