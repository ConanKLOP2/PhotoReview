# WP21-STALL: process CPU + GPU engine utilisation of PhotoReview.exe while keys are posted every -GapMs, with PHOTOREVIEW_GPU_KEEPWARM unset/0.
# Run twice interleaved (-KeepWarm 0 and default) and compare ProcessCpuMs / GpuEngineUtilPct / IdleCpuMs_10s (must stay ~0).
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$WorkDir,
  [string]$KeepWarm = '',      # '' = default, '0' = off
  [int]$Seconds = 20,
  [int]$GapMs = 250,
  [string]$Source = 'C:\Xiuren\[[WALLPAPER]'
)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace Cost -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
'@
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$photos = Join-Path $WorkDir 'photos'
New-Item -ItemType Directory -Force $photos | Out-Null
$jpegs = Get-ChildItem -LiteralPath $Source -Filter *.jpg | Sort-Object Name | Select-Object -Skip 20 -First 6
$i = 0; foreach ($j in $jpegs) { Copy-Item -LiteralPath $j.FullName -Destination (Join-Path $photos ('{0:D2}-{1}' -f $i, $j.Name)); $i++ }
$first = (Get-ChildItem $photos | Sort-Object Name | Select-Object -First 1).FullName
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '"' + $first + '"'
$psi.UseShellExecute = $false
$psi.Environment['PHOTOREVIEW_DATA_ROOT'] = Join-Path $WorkDir 'data'
$psi.Environment['PHOTOREVIEW_ISOLATE_CONFIG'] = '1'
$psi.Environment['PHOTOREVIEW_SHELL_INSTANCE_PREFIX'] = 'PRCost' + [guid]::NewGuid().ToString('N').Substring(0, 8)
if ($KeepWarm -ne '') { $psi.Environment['PHOTOREVIEW_GPU_KEEPWARM'] = $KeepWarm }
$p = [System.Diagnostics.Process]::Start($psi)
try {
  $deadline = (Get-Date).AddSeconds(30)
  while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100; $p.Refresh() }
  Start-Sleep -Seconds 4
  $p.Refresh()
  $h = $p.MainWindowHandle
  $cpu0 = $p.TotalProcessorTime
  $t0 = Get-Date
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $n = 0
  $gpuSamples = New-Object System.Collections.Generic.List[double]
  $nextGpu = 0
  while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    [void][Cost.Native]::PostMessage($h, 0x0100, [IntPtr]0x27, [IntPtr]0x14D0001)
    [void][Cost.Native]::PostMessage($h, 0x0101, [IntPtr]0x27, [IntPtr]0xC14D0001)
    $n++
    if ($sw.Elapsed.TotalSeconds -ge $nextGpu) {
      try {
        $c = (Get-Counter "\GPU Engine(pid_$($p.Id)*)\Utilization Percentage" -ErrorAction Stop).CounterSamples | Measure-Object -Property CookedValue -Sum
        $gpuSamples.Add([double]$c.Sum)
      } catch { }
      $nextGpu += 2
    }
    Start-Sleep -Milliseconds $GapMs
  }
  $p.Refresh()
  $cpu1 = $p.TotalProcessorTime
  $secs = (Get-Date) - $t0
  Start-Sleep -Seconds 6
  $p.Refresh(); $cpu2 = $p.TotalProcessorTime
  Start-Sleep -Seconds 10
  $p.Refresh(); $cpu3 = $p.TotalProcessorTime
  $cpuMs = ($cpu1 - $cpu0).TotalMilliseconds
  $threads = 0
  $gpuAvg = if ($gpuSamples.Count) { ($gpuSamples | Measure-Object -Average).Average } else { -1 }
  [pscustomobject]@{
    KeepWarm = if ($KeepWarm -eq '') { 'default(on)' } else { $KeepWarm }
    Seconds = [math]::Round($secs.TotalSeconds, 1); Keys = $n
    ProcessCpuMs = [math]::Round($cpuMs); CpuPercentOfOneCore = [math]::Round(100 * $cpuMs / $secs.TotalMilliseconds, 2)
    GpuEngineUtilPct = [math]::Round($gpuAvg, 2); GpuSamples = $gpuSamples.Count
    TailCpuMs_6s = [math]::Round(($cpu2 - $cpu1).TotalMilliseconds); IdleCpuMs_10s = [math]::Round(($cpu3 - $cpu2).TotalMilliseconds)
  } | ConvertTo-Json -Compress
} finally {
  if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(15000)) { $p.Kill() } }
}

