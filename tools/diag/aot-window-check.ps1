<#
.SYNOPSIS
  Opens the secondary windows (ZoomPrompt, Settings, Recovery, Diagnostics) of a PUBLISHED PhotoReview.exe (R2R or Native AOT)
  with posted keyboard messages only (PostMessage: NO SendInput, NO SetForegroundWindow) and records the real top-level
  windows the process creates. Intended to run on a private desktop (tools/diag/aot-hidden-run.ps1) so nothing is shown.

.DESCRIPTION
  One fresh process per scenario (own data root, own instance prefix, own perf-trace dir, photo folder copy of -Photos):
    wait for RenderedFrame -> post keys -> wait for a NEW visible top-level window of that process -> record class/title/size
    -> WM_CLOSE it -> check the shell is still alive -> WM_CLOSE the shell, record the exit code.
  Keys: ZoomPrompt = '3' (default CustomZoom shortcut D3); Settings = Tab,Tab,Enter (toolbar button 2);
  Tools menu = Tab x4, Enter, then Down (+Down) and Enter for Recovery / Diagnostics.
  Output: one JSON line per scenario on stdout and in -OutFile (appended).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$PhotoFolder,
    [Parameter(Mandatory)][string]$Label,
    [string[]]$Scenarios = @('Zoom', 'Settings', 'Recovery', 'Diagnostics'),
    [string]$OutFile = '',
    [int]$TimeoutSec = 40
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinProbe
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    public static List<string> Windows(int pid)
    {
        var list = new List<string>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && IsWindowVisible(h))
            {
                var c = new StringBuilder(256); GetClassName(h, c, 256);
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                RECT r; GetWindowRect(h, out r);
                list.Add(h.ToInt64() + "|" + c + "|" + t + "|" + (r.R - r.L) + "x" + (r.B - r.T));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static void Key(IntPtr h, int vk)
    {
        PostMessage(h, 0x0100, (IntPtr)vk, (IntPtr)1);
        PostMessage(h, 0x0101, (IntPtr)vk, (IntPtr)unchecked((int)0xC0000001));
    }
    public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
}
'@

$VK_TAB = 9; $VK_RETURN = 13; $VK_DOWN = 40; $VK_3 = 0x33

function Wait-Menu([int]$procId) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt 8) {
        $m = [WinProbe]::Windows($procId) | Where-Object { $_ -match '\|#32768\|' } | Select-Object -First 1
        if ($m) { Start-Sleep -Milliseconds 300; return [IntPtr][int64]($m.Split('|')[0]) }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

function Invoke-Scenario([string]$name) {
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $root = Join-Path $env:TEMP ("aotchk-$id")
    $photos = Join-Path $root 'photos'
    New-Item -ItemType Directory -Force -Path $photos | Out-Null
    Get-ChildItem -LiteralPath $PhotoFolder -Filter *.jpg -File | Select-Object -First 5 | Copy-Item -Destination $photos
    $trace = Join-Path $root 'perf'
    $first = (Get-ChildItem -LiteralPath $photos -File | Select-Object -First 1).FullName
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.UseShellExecute = $false
    $psi.Arguments = '"' + $first + '"'
    $psi.WorkingDirectory = Split-Path $Exe
    $psi.EnvironmentVariables['PHOTOREVIEW_DATA_ROOT'] = (Join-Path $root 'data')
    $psi.EnvironmentVariables['PHOTOREVIEW_ISOLATE_CONFIG'] = '1'
    $psi.EnvironmentVariables['PHOTOREVIEW_PERF_TRACE'] = $trace
    $psi.EnvironmentVariables['PHOTOREVIEW_SHELL_INSTANCE_PREFIX'] = "AotChk$id"
    $r = [ordered]@{ label = $Label; scenario = $name; exe = $Exe; opened = $false; window = ''; exit = $null; alive = $null; note = '' }
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $main = [IntPtr]::Zero
        while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
            $rendered = $false
            if (Test-Path $trace) {
                foreach ($f in Get-ChildItem $trace -File) {
                    try {
                        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
                        $txt = (New-Object IO.StreamReader $fs).ReadToEnd(); $fs.Dispose()
                        if ($txt -match ',RenderedFrame,') { $rendered = $true }
                    } catch { }
                }
            }
            $w = [WinProbe]::Windows($p.Id)
            if ($rendered -and $w.Count -ge 1) { $main = [IntPtr][int64]($w[0].Split('|')[0]); break }
            Start-Sleep -Milliseconds 100
        }
        if ($main -eq [IntPtr]::Zero) { $r.note = 'main window / RenderedFrame not reached'; return $r }
        $before = [WinProbe]::Windows($p.Id) | ForEach-Object { $_.Split('|')[0] }
        switch ($name) {
            'Zoom'        { [WinProbe]::Key($main, $VK_3) }
            'Settings'    { 1..2 | % { [WinProbe]::Key($main, $VK_TAB); Start-Sleep -Milliseconds 60 }; [WinProbe]::Key($main, $VK_RETURN) }
            'Recovery'    { 1..4 | % { [WinProbe]::Key($main, $VK_TAB); Start-Sleep -Milliseconds 60 }; [WinProbe]::Key($main, $VK_RETURN)
                            $menu = Wait-Menu $p.Id
                            if ($menu -ne [IntPtr]::Zero) { [WinProbe]::Key($menu, $VK_DOWN); Start-Sleep -Milliseconds 150; [WinProbe]::Key($menu, $VK_RETURN) } else { $r.note = 'Tools menu did not open' } }
            'Diagnostics' { 1..4 | % { [WinProbe]::Key($main, $VK_TAB); Start-Sleep -Milliseconds 60 }; [WinProbe]::Key($main, $VK_RETURN)
                            $menu = Wait-Menu $p.Id
                            if ($menu -ne [IntPtr]::Zero) { [WinProbe]::Key($menu, $VK_DOWN); Start-Sleep -Milliseconds 150; [WinProbe]::Key($menu, $VK_DOWN); Start-Sleep -Milliseconds 150; [WinProbe]::Key($menu, $VK_RETURN) } else { $r.note = 'Tools menu did not open' } }
        }
        $t0 = Get-Date
        $new = $null
        while (((Get-Date) - $t0).TotalSeconds -lt 12) {
            $new = [WinProbe]::Windows($p.Id) | Where-Object { $before -notcontains $_.Split('|')[0] -and $_ -notmatch '\|(#32768|SysShadow)\|' } | Select-Object -First 1
            if ($new) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($new) {
            $r.opened = $true; $r.window = $new
            Start-Sleep -Milliseconds 400
            [WinProbe]::Close([IntPtr][int64]($new.Split('|')[0]))
            Start-Sleep -Milliseconds 800
        } else {
            $r.note = 'no new top-level window within 12 s; windows: ' + (([WinProbe]::Windows($p.Id)) -join ' ; ')
        }
        $r.alive = -not $p.HasExited
        [WinProbe]::Close($main)
        if (-not $p.WaitForExit(15000)) { $r.note += ' shell did not exit after WM_CLOSE'; $p.Kill(); $p.WaitForExit(5000) | Out-Null }
        $r.exit = $p.ExitCode
        return $r
    } finally {
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
        Start-Sleep -Milliseconds 200
        Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    }
}

foreach ($s in ($Scenarios -join ',').Split(',')) {
    $res = Invoke-Scenario $s
    $line = ($res | ConvertTo-Json -Compress)
    $line
    if ($OutFile) { Add-Content -Path $OutFile -Value $line -Encoding UTF8 }
}
