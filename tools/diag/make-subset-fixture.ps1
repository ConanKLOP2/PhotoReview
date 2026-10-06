<#
.SYNOPSIS
  Builds an NTFS HARD-LINK subset of an existing fixture folder with a target total size, for the device-tuning bench
  (docs/refactoring/perf/PLAN-device-config-bench.md: small / mid / large regimes) at zero extra disk space.

.DESCRIPTION
  HARD LINKS SHARE DATA WITH THE SOURCE. Every file of the subset is a second directory entry of the SAME file in the source
  fixture (same data, same timestamps, same attributes). Consequences: (1) never write, edit, rename-over or "save" a
  file through the subset - that changes the source too; the bench only ever reads fixtures; (2) deleting a file from the
  subset only removes the extra name, the source keeps its data; (3) the subset MUST be on the same volume as the source
  (NTFS cannot hard-link across volumes) - the script refuses otherwise, it never silently copies. Nothing in the source is
  moved, modified, renamed or deleted; the script re-fingerprints the source (Fixture-Fingerprint.ps1) before and after and
  fails if it changed.

  USAGE
    # preview: which files, how many bytes, nothing is created
    .\tools\diag\make-subset-fixture.ps1 -Source F4 -Destination C:\Xiuren\_tune\small -TargetGb 2.5 -DryRun
    # create + register the alias in work\diag\fixtures.local.json (existing keys are kept):
    .\tools\diag\make-subset-fixture.ps1 -Source F4 -Destination C:\Xiuren\_tune\small -TargetGb 2.5 -Alias F-small
    .\tools\diag\make-subset-fixture.ps1 -Source F4 -Destination C:\Xiuren\_tune\mid   -TargetGb 10  -Alias F-mid
    # rebuild an existing subset (only a destination this script created, recognised by its marker file, is emptied):
    .\tools\diag\make-subset-fixture.ps1 -Source F4 -Destination C:\Xiuren\_tune\small -TargetGb 2.5 -Alias F-small -Rebuild

  -Source is a fixture alias (looked up in -FixturesFile, default work\diag\fixtures.local.json) or a folder path (always
  used with -LiteralPath, so the brackets of C:\Xiuren\[[WALLPAPER] are safe). -TargetGb is in GiB (1024^3 bytes).
  Selection: the files (recursively, relative layout preserved) are put in a seeded random order (-Seed, default 1) and taken until the
  running total reaches the target (overshoot < one file). The same seed makes the 2.5 GiB subset a PREFIX of the 10 GiB one
  (small is contained in mid), and the same inputs give the same subset. The mix of JPG/PNG is therefore representative of the source.
  Every subset is verified: each link has the same volume + file index as its source file (so it really is a hard link), the
  destination fingerprint is printed, and a marker file `.hardlink-subset.json` records source, seed and target.
  Refuses: destination inside the source or the source inside the destination, different volume, destination exists and is
  not an empty dir / a previously created subset (without -Rebuild), target larger than the source.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidateRange(0.01, 4096)][double]$TargetGb,
    [string]$Alias,
    [string]$FixturesFile,
    [int]$Seed = 1,
    [switch]$Rebuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$dry = $DryRun -or $WhatIfPreference
$WhatIfPreference = $false
$markerName = '.hardlink-subset.json'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'Fixture-Fingerprint.ps1')

function Resolve-WorkDir {
    $local = Join-Path $repoRoot 'work'
    if (Test-Path -LiteralPath $local) { return $local }
    $common = (& git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $common) {
        $main = Join-Path (Split-Path -Parent $common) 'work'
        if (Test-Path -LiteralPath $main) { return $main }
    }
    return $local
}
if (-not $FixturesFile) { $FixturesFile = Join-Path (Resolve-WorkDir) 'diag\fixtures.local.json' }

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class SubsetFixtureNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Lo; public uint Hi; }
    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint Attributes; public FILETIME Creation, Access, Write; public uint VolumeSerial; public uint SizeHigh, SizeLow;
        public uint Links; public uint IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
    public static void Link(string newFile, string existing)
    {
        if (!CreateHardLinkW(@"\\?\" + newFile, @"\\?\" + existing, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateHardLink failed for " + newFile);
    }
    // "volume:index:links" of a file; two paths with the same volume+index are the same file (hard links).
    public static string Identity(string path)
    {
        using (var fs = new FileStream(@"\\?\" + path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            BY_HANDLE_FILE_INFORMATION i;
            if (!GetFileInformationByHandle(fs.SafeFileHandle, out i)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return i.VolumeSerial.ToString("x") + ":" + i.IndexHigh.ToString("x") + i.IndexLow.ToString("x8") + ":" + i.Links;
        }
    }
}
'@

# ---- resolve source ---------------------------------------------------------------------------------------------------
$sourceAlias = $null
if (-not (Test-Path -LiteralPath $Source)) {
    if (-not (Test-Path -LiteralPath $FixturesFile)) { throw "Source '$Source' is not a folder and $FixturesFile does not exist" }
    $raw = [System.IO.File]::ReadAllText($FixturesFile, [System.Text.Encoding]::UTF8)
    $fx = try { $raw | ConvertFrom-Json } catch { ($raw -replace '(?<!\\)\\(?![\\"])', '\\') | ConvertFrom-Json }
    $entry = $fx.$Source
    if (-not $entry -or -not $entry.path) { throw "Fixture alias '$Source' not found in $FixturesFile" }
    $sourceAlias = $Source
    $Source = [string]$entry.path
}
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source folder does not exist: $Source" }
$srcRoot = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\')
$dstRoot = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')

# ---- guards --------------------------------------------------------------------------------------------------------------
if ((Split-Path -Qualifier $srcRoot) -ne (Split-Path -Qualifier $dstRoot)) {
    throw "Refusing: source ($srcRoot) and destination ($dstRoot) are on different volumes; NTFS hard links need the same volume (no copy fallback by design)."
}
if ($dstRoot -eq $srcRoot -or $dstRoot.StartsWith($srcRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $srcRoot.StartsWith($dstRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing: destination and source must be separate folders (neither inside the other).'
}
$fsType = (New-Object System.IO.DriveInfo((Split-Path -Qualifier $srcRoot))).DriveFormat
if ($fsType -ne 'NTFS') { throw "Refusing: volume is $fsType, hard links need NTFS." }
$marker = Join-Path $dstRoot $markerName
if (Test-Path -LiteralPath $dstRoot) {
    $existing = @(Get-ChildItem -LiteralPath $dstRoot -Force)
    if ($existing.Count -gt 0) {
        if (-not (Test-Path -LiteralPath $marker)) { throw "Refusing: $dstRoot exists, is not empty and was not created by this script (no $markerName)." }
        if (-not $Rebuild) { throw "Refusing: $dstRoot already holds a subset; pass -Rebuild to replace it." }
    }
}

# ---- selection ----------------------------------------------------------------------------------------------------------
$srcFiles = @(Get-ChildItem -LiteralPath $srcRoot -File -Recurse -Force)
$srcBytes = ($srcFiles | Measure-Object -Property Length -Sum).Sum
$target = [int64]($TargetGb * 1GB)
if ($target -gt $srcBytes) { throw "Refusing: target $([math]::Round($TargetGb, 2)) GiB is larger than the source ($([math]::Round($srcBytes / 1GB, 2)) GiB)." }
$rng = New-Object System.Random($Seed)
$order = @($srcFiles | Sort-Object { $_.FullName.ToUpperInvariant() })   # stable base order, then a seeded Fisher-Yates shuffle
for ($i = $order.Count - 1; $i -gt 0; $i--) { $j = $rng.Next($i + 1); $t = $order[$i]; $order[$i] = $order[$j]; $order[$j] = $t }
$picked = New-Object System.Collections.Generic.List[object]
$total = [int64]0
foreach ($f in $order) {
    if ($total -ge $target) { break }
    $picked.Add($f); $total += $f.Length
}
Write-Host ("Source {0}: {1} files, {2:N2} GiB. Subset: {3} files, {4:N2} GiB (target {5:N2} GiB), seed {6}." -f $srcRoot, $srcFiles.Count, ($srcBytes / 1GB), $picked.Count, ($total / 1GB), $TargetGb, $Seed)
if ($dry) {
    Write-Host "DRY RUN: would hard-link $($picked.Count) files into $dstRoot$(if ($Alias) { " and register alias $Alias in $FixturesFile" }). Nothing created."
    return
}

# ---- build ------------------------------------------------------------------------------------------------------------------
$srcBefore = Get-FixtureStat $srcRoot
if (Test-Path -LiteralPath $dstRoot) {
    # Only a folder that carries our marker gets here (guard above). Removing entries only deletes the extra names: the source keeps its data.
    Get-ChildItem -LiteralPath $dstRoot -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $dstRoot | Out-Null
$made = 0
foreach ($f in $picked) {
    $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\')
    $to = Join-Path $dstRoot $rel
    $dir = Split-Path -Parent $to
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [SubsetFixtureNative]::Link($to, $f.FullName)
    $made++
}
$info = [ordered]@{
    createdBy = 'tools/diag/make-subset-fixture.ps1'; created = (Get-Date).ToString('o'); source = $srcRoot; sourceAlias = $sourceAlias
    seed = $Seed; targetGiB = $TargetGb; files = $made; bytes = $total
    warning = 'Files here are NTFS hard links of the source fixture: never write through them.'
}
[System.IO.File]::WriteAllText($marker, ($info | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
(Get-Item -LiteralPath $marker -Force).Attributes = 'Hidden'

# ---- verify -------------------------------------------------------------------------------------------------------------------
$bad = 0
foreach ($f in $picked) {
    $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\')
    $a = ([SubsetFixtureNative]::Identity($f.FullName) -split ':'); $b = ([SubsetFixtureNative]::Identity((Join-Path $dstRoot $rel)) -split ':')
    if ($a[0] -ne $b[0] -or $a[1] -ne $b[1] -or [int]$b[2] -lt 2) { $bad++ }
}
if ($bad -gt 0) { throw "Verification failed: $bad link(s) are not hard links of their source file." }
$srcAfter = Get-FixtureStat $srcRoot
if (-not (Test-FixtureStatEqual $srcBefore $srcAfter)) { throw 'Source fixture fingerprint changed during the build - it must never be touched. Investigate before using it.' }
$dstFp = Get-FixtureStat $dstRoot   # same guard tune-matrix.ps1 uses; the hidden marker file is not counted
Write-Host ("OK: {0} hard links created in {1} (all verified same file as the source; source fingerprint unchanged {2})." -f $made, $dstRoot, $srcAfter.Fingerprint.Substring(0, 12))
Write-Host ("Destination fingerprint: files={0} bytes={1} fp={2}" -f $dstFp.Count, $dstFp.Bytes, $dstFp.Fingerprint.Substring(0, 12))

# ---- register alias (text insert: keeps the user's file, formatting and keys) ---------------------------------------------------
if ($Alias) {
    if ($Alias -notmatch '^[A-Za-z0-9_.-]+$') { throw "Alias '$Alias' must be letters, digits, . _ -" }
    function ConvertTo-JsonString([string]$s) { '"' + ($s -replace '\\', '\\' -replace '"', '\"') + '"' }
    $desc = "Hard-link subset of $(if ($sourceAlias) { $sourceAlias } else { $srcRoot }): $made files, $([math]::Round($total / 1GB, 1)) GiB, seed $Seed (tools/diag/make-subset-fixture.ps1)"
    $line = '  ' + (ConvertTo-JsonString $Alias) + ': { "path": ' + (ConvertTo-JsonString $dstRoot) + ', "files": ' + $made + ', "desc": ' + (ConvertTo-JsonString $desc) + ' }'
    $raw = if (Test-Path -LiteralPath $FixturesFile) { [System.IO.File]::ReadAllText($FixturesFile, [System.Text.Encoding]::UTF8) } else { "{`r`n}" }
    $aliasRegex = New-Object System.Text.RegularExpressions.Regex(('^[ \t]*"' + [regex]::Escape($Alias) + '"[ \t]*:[^\r\n]*$'), 'Multiline')
    $existingLine = $aliasRegex.Match($raw)
    if ($existingLine.Success) {
        $trailingComma = if ($existingLine.Value.TrimEnd().EndsWith(',')) { ',' } else { '' }
        $replacement = $line + $trailingComma
        $raw = $aliasRegex.Replace($raw, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
    }
    else {
        $close = $raw.LastIndexOf('}')
        if ($close -lt 0) { throw "$FixturesFile has no closing brace" }
        $head = $raw.Substring(0, $close).TrimEnd()
        $sep = if ($head.EndsWith('{')) { '' } else { ',' }
        $raw = $head + $sep + "`r`n" + $line + "`r`n" + $raw.Substring($close)
    }
    [System.IO.File]::WriteAllText($FixturesFile, $raw, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Alias $Alias -> $dstRoot registered in $FixturesFile"
}
