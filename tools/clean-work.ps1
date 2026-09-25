#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Retention cleanup for the gitignored work/ folder (logs, reports, benchmark output).
.DESCRIPTION
  Dry run by default (equivalent to -WhatIf): lists what would be deleted. Pass -Delete to remove.
  Only files under <repo>/work older than -OlderThanDays are touched; work/dotnet-home
  (the local .NET CLI home) is never touched. Compatible with Windows PowerShell 5.1.
#>
param(
    [int]$OlderThanDays = 14,
    [switch]$Delete
)

$ErrorActionPreference = 'Stop'

if ($OlderThanDays -lt 0) { throw 'OlderThanDays must be >= 0.' }

$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'work'
if (-not (Test-Path -LiteralPath $work)) {
    Write-Host "Nothing to do: $work does not exist."
    return
}

$keep = [System.IO.Path]::Combine($work, 'dotnet-home') + [System.IO.Path]::DirectorySeparatorChar
$cutoff = (Get-Date).AddDays(-$OlderThanDays)

$old = @(Get-ChildItem -LiteralPath $work -Recurse -File -Force |
    Where-Object { -not $_.FullName.StartsWith($keep, [System.StringComparison]::OrdinalIgnoreCase) } |
    Where-Object { $_.LastWriteTime -lt $cutoff })

$bytes = ($old | Measure-Object -Property Length -Sum).Sum
if ($null -eq $bytes) { $bytes = 0 }
$mb = [Math]::Round($bytes / 1MB, 1)

foreach ($f in $old) {
    if ($Delete) {
        Remove-Item -LiteralPath $f.FullName -Force
    }
    else {
        Write-Host ("Would delete: {0}" -f $f.FullName)
    }
}

if ($Delete) {
    # Remove directories left empty (deepest first), except the protected one.
    Get-ChildItem -LiteralPath $work -Recurse -Directory -Force |
        Where-Object { -not ($_.FullName + [System.IO.Path]::DirectorySeparatorChar).StartsWith($keep, [System.StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Write-Host ("Deleted {0} file(s), {1} MB (older than {2} days)." -f $old.Count, $mb, $OlderThanDays)
}
else {
    Write-Host ("Dry run: {0} file(s), {1} MB older than {2} days. Re-run with -Delete to remove." -f $old.Count, $mb, $OlderThanDays)
}
