#!/usr/bin/env pwsh
<#
.SYNOPSIS
  AR07 §3: link-check gate for non-archive Markdown docs.

  Scans every non-archive *.md file for:
    - Markdown links: [text](path)
    - Back-ticked doc paths: `docs/.../something.md`
  and verifies each local (non-http/mailto/#anchor) target resolves relative
  to the referencing file's directory, the repo root, or docs/.

  Exits 1 and lists every broken link if any are found; exits 0 otherwise.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Directories to exclude entirely: docs/archive and docs/refactoring/archive
# are historical/unmaintained, docs/refactoring/arch-review holds planning
# documents that intentionally quote past-broken and glob-style example
# paths as a historical record (not live links); the rest is build output /
# vendor / vcs / worktree noise. Matched against a path RELATIVE to the
# repo root, never the absolute path (the repo itself may live under a
# .claude/worktrees/* checkout).
$ExcludeDirPattern = '(^|[\\/])(docs[\\/]archive|docs[\\/]refactoring[\\/]archive|docs[\\/]refactoring[\\/]arch-review|bin|obj|\.git|node_modules|\.claude[\\/]worktrees)([\\/]|$)'

function Test-Excluded([string]$RelativePath) {
    return $RelativePath -match $ExcludeDirPattern
}

# Strip a trailing #anchor or :line-number suffix from a link target.
function Get-CleanTarget([string]$Target) {
    $t = $Target.Trim()
    # Drop #anchor
    $hashIdx = $t.IndexOf('#')
    if ($hashIdx -ge 0) { $t = $t.Substring(0, $hashIdx) }
    # Drop trailing :NN (line number reference)
    $t = $t -replace ':\d+$', ''
    return $t.Trim()
}

function Test-Skippable([string]$Target) {
    if ([string]::IsNullOrWhiteSpace($Target)) { return $true }
    if ($Target -match '^(https?|mailto):') { return $true }
    if ($Target.StartsWith('#')) { return $true }
    # Glob-style example paths (*, {a,b}, ...) are documentation examples,
    # not resolvable single-file links.
    if ($Target -match '[\*\{\}]' -or $Target -match '…') { return $true }
    return $false
}

function Resolve-MdLink([string]$FileDir, [string]$Target) {
    # Markdown links render on GitHub relative to the file's directory (or the repo root for a leading '/'),
    # so unlike backtick doc mentions they must not fall back to other bases.
    $c = if ($Target.StartsWith('/')) { Join-Path $root $Target.TrimStart('/') } else { Join-Path $FileDir $Target }
    try { return (Test-Path -LiteralPath ([System.IO.Path]::GetFullPath($c))) } catch { return $false }
}

function Resolve-DocLink([string]$FileDir, [string]$Target) {
    # Absolute-looking repo paths (starting with docs/, src/, tools/, tests/, etc.)
    # and relative paths are both tried against three bases per the plan:
    # file dir, repo root, docs/.
    $candidates = @(
        (Join-Path $FileDir $Target),
        (Join-Path $root $Target),
        (Join-Path (Join-Path $root 'docs') $Target)
    )
    foreach ($c in $candidates) {
        try {
            $resolved = [System.IO.Path]::GetFullPath($c)
        } catch {
            continue
        }
        if (Test-Path -LiteralPath $resolved) { return $true }
    }
    return $false
}

# Collect all non-archive markdown files.
$mdFiles = Get-ChildItem -LiteralPath $root -Recurse -Filter '*.md' -File |
    Where-Object {
        $rel = $_.FullName.Substring($root.Length + 1) -replace '\\', '/'
        -not (Test-Excluded $rel)
    }

$MdLinkRegex = [regex]'\[[^\]]*\]\(([^)]+)\)'
$BacktickDocRegex = [regex]'`(docs[\\/][^`]*?\.md)`'

$broken = New-Object System.Collections.Generic.List[object]

foreach ($file in $mdFiles) {
    $fileDir = $file.DirectoryName
    $relFile = $file.FullName.Substring($root.Length + 1) -replace '\\', '/'
    $lineNum = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNum++

        foreach ($m in $MdLinkRegex.Matches($line)) {
            $rawTarget = $m.Groups[1].Value
            $target = Get-CleanTarget $rawTarget
            if (Test-Skippable $target) { continue }
            if (Test-Excluded $target) { continue }
            if (-not (Resolve-MdLink $fileDir $target)) {
                $broken.Add([PSCustomObject]@{
                    File   = $relFile
                    Line   = $lineNum
                    Target = $rawTarget
                })
            }
        }

        foreach ($m in $BacktickDocRegex.Matches($line)) {
            $rawTarget = $m.Groups[1].Value
            $target = Get-CleanTarget $rawTarget
            if (Test-Skippable $target) { continue }
            if (Test-Excluded $target) { continue }
            if (-not (Resolve-DocLink $fileDir $target)) {
                $broken.Add([PSCustomObject]@{
                    File   = $relFile
                    Line   = $lineNum
                    Target = $rawTarget
                })
            }
        }
    }
}

if ($broken.Count -gt 0) {
    Write-Host "`n[FAIL] $($broken.Count) broken doc link(s):`n" -ForegroundColor Red
    foreach ($b in $broken) {
        Write-Host "  $($b.File):$($b.Line) -> $($b.Target)" -ForegroundColor Red
    }
    exit 1
}

Write-Host "[PASS] 0 broken doc links ($($mdFiles.Count) files checked)" -ForegroundColor Green
exit 0
