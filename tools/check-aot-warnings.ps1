#!/usr/bin/env pwsh
<#
.SYNOPSIS
  WP-34: AOT gate. Publishes PhotoReview.exe (src/PhotoReview.Shell.Win32) as Native AOT (-p:ShellAot=true) and counts the
  ILC / trim / AOT warnings (IL2xxx, IL3xxx, ILCxxxx). Fails when the count is above -MaxWarnings (default 0).

.DESCRIPTION
  The IL2xxx/IL3xxx analyzers (IsAotCompatible) run at build time on every AOT-ready project, but only the real ILC publish sees the
  whole dependency graph (framework + packages) of the executable, so this script is the authoritative "0 warnings" gate.

  Needs the MSVC toolchain (Visual Studio Build Tools "Desktop development with C++" + Windows SDK). ILCompiler locates it by calling
  vswhere BY NAME, so the Visual Studio Installer folder is added to PATH for the publish. Without the toolchain the script prints
  [SKIP] and exits 0 (use -RequireToolchain to make that an error, e.g. on a CI runner that must have it).

  PublishAot is set in the shell csproj (not on the command line: a global PublishAot flows into netstandard analyzer projects and is
  rejected by NETSDK1207); EventSourceSupport must stay true under AOT (PhotoReviewPerf events). Every process started here is
  started by this script and is the only one it may stop on timeout.

.PARAMETER Project         Executable project to publish (default src/PhotoReview.Shell.Win32).
.PARAMETER OutputDirectory Publish output (default: a fresh temp folder, removed at the end unless -KeepOutput).
.PARAMETER MaxWarnings     Accepted number of distinct warnings (default 0).
.PARAMETER TimeoutMinutes  Upper bound for the publish (default 15).
.PARAMETER RequireToolchain Fail instead of skipping when MSVC is missing.
.PARAMETER KeepOutput      Keep the publish output folder.
#>
[CmdletBinding()]
param(
    [string]$Project = 'src/PhotoReview.Shell.Win32/PhotoReview.Shell.Win32.csproj',
    [string]$OutputDirectory,
    [int]$MaxWarnings = 0,
    [int]$TimeoutMinutes = 15,
    [switch]$RequireToolchain,
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# --- toolchain probe -------------------------------------------------------------------------------------------------------
$installerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installerDir 'vswhere.exe'
$vcPath = $null
if (Test-Path -LiteralPath $vswhere) {
    $vcPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null |
        Select-Object -First 1
}
if (-not $vcPath) {
    $message = 'MSVC (Desktop development with C++) toolchain not found; the AOT warning gate needs it.'
    if ($RequireToolchain) { Write-Host "[FAIL] $message"; exit 1 }
    Write-Host "[SKIP] $message"
    exit 0
}

# --- publish ---------------------------------------------------------------------------------------------------------------
$ownsOutput = $false
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('wp34-aot-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $ownsOutput = $true
}
$logFile = Join-Path ([IO.Path]::GetTempPath()) ('wp34-aot-log-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
$errFile = $logFile + '.err'
$projectPath = Join-Path $root $Project

$env:PATH = $installerDir + ';' + $env:PATH
$arguments = @(
    'publish', "`"$projectPath`"", '-c', 'Release', '-r', 'win-x64', '-p:ShellAot=true',
    '-o', "`"$OutputDirectory`"", '/nodeReuse:false', '-v:minimal', '--nologo'
) -join ' '

Write-Host "Publishing AOT: dotnet $arguments"
$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $root -NoNewWindow -PassThru `
    -RedirectStandardOutput $logFile -RedirectStandardError $errFile
$null = $process.Handle   # cache the handle: without it ExitCode is empty after WaitForExit(ms) on Windows PowerShell 5.1
if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    try { $process.Kill($true) } catch { }
    Write-Host "[FAIL] AOT publish exceeded $TimeoutMinutes minutes (killed the publish process tree started here)."
    exit 1
}
$process.WaitForExit()
$exit = $process.ExitCode

# --- count -----------------------------------------------------------------------------------------------------------------
$lines = @()
foreach ($file in @($logFile, $errFile)) { if (Test-Path -LiteralPath $file) { $lines += Get-Content -LiteralPath $file } }
$pattern = '(warning|error)\s+(IL\d{4}|ILC\d*|IL\d+)\b'
$distinct = $lines | Where-Object { $_ -match $pattern } | ForEach-Object { $_.Trim() } | Sort-Object -Unique
$count = @($distinct).Count

if ($distinct) { $distinct | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" } }
$exe = Join-Path $OutputDirectory 'PhotoReview.exe'
$size = if (Test-Path -LiteralPath $exe) { [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1) } else { 0 }

if ($ownsOutput -and -not $KeepOutput -and (Test-Path -LiteralPath $OutputDirectory)) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
if ($exit -eq 0 -and $count -le $MaxWarnings) { Remove-Item -LiteralPath $logFile, $errFile -Force -ErrorAction SilentlyContinue } else { Write-Host "Full publish log kept: $logFile (+ .err)" }

if ($exit -ne 0) {
    Write-Host "[FAIL] dotnet publish exited with $exit ($count distinct ILC/trim warnings seen)."
    exit 1
}
if ($count -gt $MaxWarnings) {
    Write-Host "[FAIL] $count distinct ILC/trim/AOT warnings (allowed: $MaxWarnings)."
    exit 1
}
Write-Host "[PASS] AOT publish succeeded with $count ILC/trim/AOT warnings (allowed: $MaxWarnings); PhotoReview.exe $size MB."
exit 0
