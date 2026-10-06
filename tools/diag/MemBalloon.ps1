<#
.SYNOPSIS
  Memory balloon for the memory-pressure stage S6 of the device-tuning bench: allocates and TOUCHES N GB of private memory,
  holds it until a stop file appears (or a timeout), then frees it. Not referenced by the product; a diagnostic tool only.

.DESCRIPTION
  USAGE
    # hold 16 GB until the file <stop> appears or 2 hours pass; run it in the background:
    .\tools\diag\MemBalloon.ps1 -SizeGb 16 -StopFile C:\Temp\balloon.stop -TimeoutMinutes 120
    # ... run the tune-matrix batch ...
    New-Item C:\Temp\balloon.stop -ItemType File        # releases the memory (or just wait for the timeout)
    # sanity check without allocating anything:
    .\tools\diag\MemBalloon.ps1 -SizeGb 16 -WhatIf

  SAFETY: never allocates more than (available physical RAM - 3 GB) at start; a bigger -SizeGb is a hard error (use
  -Clamp to shrink to that limit instead). Memory is committed with VirtualAlloc(MEM_COMMIT) and written page by page so it is
  really resident (working set), not just reserved. It is private memory of THIS process only: ending the process (even by
  kill) returns every byte to the OS. It never touches files except reading the stop file path (and writing the optional
  -ReadyFile when the allocation is complete, so a driver script can wait for it with a bounded timeout).
  The OS may page part of the balloon out under pressure; -SizeGb is therefore the committed amount, and the printed
  "resident" figure is the working set after touching.

  S6 HOW-TO (docs: tools\diag\tune\README.md): start the balloon (8 / 16 / 22 GB), wait for the ready file, run the stage with
  tune-matrix.ps1 (it samples minAvailRam), release the balloon, cool down, next size.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateRange(0.1, 512)][double]$SizeGb,
    [string]$StopFile,
    [ValidateRange(1, 1440)][int]$TimeoutMinutes = 60,
    [string]$ReadyFile,
    [double]$HeadroomGb = 3,
    [switch]$Clamp
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MemBalloonNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
    public static ulong AvailPhysBytes()
    {
        var s = new MEMORYSTATUSEX(); s.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (!GlobalMemoryStatusEx(ref s)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return s.ullAvailPhys;
    }
    // Commits `bytes` of private memory in 256 MiB chunks and writes one byte per 4 KiB page. Returns the chunk base addresses.
    public static IntPtr[] AllocateAndTouch(ulong bytes)
    {
        const ulong Chunk = 256UL * 1024 * 1024;
        var list = new System.Collections.Generic.List<IntPtr>();
        ulong left = bytes;
        while (left > 0)
        {
            ulong n = left < Chunk ? left : Chunk;
            IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)n, 0x1000 | 0x2000, 0x04);   // MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE
            if (p == IntPtr.Zero) { Free(list.ToArray()); throw new OutOfMemoryException("VirtualAlloc failed after " + (bytes - left) + " bytes"); }
            list.Add(p);
            for (ulong off = 0; off < n; off += 4096) Marshal.WriteByte(p, (int)off, 1);
            left -= n;
        }
        return list.ToArray();
    }
    public static void Free(IntPtr[] chunks) { foreach (var p in chunks) VirtualFree(p, UIntPtr.Zero, 0x8000); }   // MEM_RELEASE
}
'@

$availGb = [MemBalloonNative]::AvailPhysBytes() / 1GB
$limitGb = [math]::Max(0, $availGb - $HeadroomGb)
$want = $SizeGb
if ($want -gt $limitGb) {
    if ($Clamp) { $want = [math]::Floor($limitGb * 10) / 10; Write-Host "Clamped to $want GB (available $([math]::Round($availGb, 1)) GB - $HeadroomGb GB headroom)." -ForegroundColor Yellow }
    else { throw "Refusing: $SizeGb GB requested but only $([math]::Round($limitGb, 1)) GB is allowed (available $([math]::Round($availGb, 1)) GB - $HeadroomGb GB headroom). Free memory, ask for less, or pass -Clamp." }
}
if ($want -le 0) { throw 'Nothing to allocate after the headroom rule.' }
if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess("$want GB private memory", 'allocate and touch')) {
    Write-Host "WHATIF: would allocate $want GB (available $([math]::Round($availGb, 1)) GB, limit $([math]::Round($limitGb, 1)) GB)."
    return
}
if ($StopFile -and (Test-Path -LiteralPath $StopFile)) { throw "Stop file $StopFile already exists; remove it first (the balloon would exit at once)." }

$bytes = [uint64]([math]::Round($want * 1GB))
Write-Host "Allocating and touching $want GB..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$chunks = [MemBalloonNative]::AllocateAndTouch($bytes)
try {
    $proc = Get-Process -Id $PID
    Write-Host ("Holding {0} GB (working set {1} GB, private {2} GB, took {3} s); available RAM now {4} GB." -f $want,
        [math]::Round($proc.WorkingSet64 / 1GB, 1), [math]::Round($proc.PrivateMemorySize64 / 1GB, 1), [math]::Round($sw.Elapsed.TotalSeconds, 1),
        [math]::Round([MemBalloonNative]::AvailPhysBytes() / 1GB, 1))
    if ($ReadyFile) { [System.IO.File]::WriteAllText($ReadyFile, (Get-Date).ToString('o')) }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $reason = 'timeout'
    while ((Get-Date) -lt $deadline) {
        if ($StopFile -and (Test-Path -LiteralPath $StopFile)) { $reason = 'stop file'; break }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Releasing the balloon ($reason)."
}
finally {
    [MemBalloonNative]::Free($chunks)
    if ($ReadyFile -and (Test-Path -LiteralPath $ReadyFile)) { Remove-Item -LiteralPath $ReadyFile -Force }
}
