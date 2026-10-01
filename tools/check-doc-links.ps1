#!/usr/bin/env pwsh
<#
.SYNOPSIS
  AR07 §3: link-check gate for Markdown docs, plus a stale-doc-reference
  scan over source comments (R12).

  Part 1 scans every *.md file for:
    - Markdown links: [text](path)
    - Back-ticked doc paths: `docs/.../something.md`
  and verifies each local (non-http/mailto/#anchor) target resolves relative
  to the referencing file's directory, the repo root, or docs/.

  Part 2 scans every `src/**/*.cs` and `tests/**/*.cs` comment (// line comments,
  /// doc comments, /* */ blocks) for a filename that looks like this repo's real
  doc-naming convention (starts with an uppercase letter, e.g. `AGENTS.md`,
  `AR14-viewmodel-composition.md`, `PERF-DIAGNOSIS-PLAN.md`) and verifies each one
  names a file that still exists somewhere in the repo (docs/ or the repo root).
  This is deliberately conservative: lower-case names (`summary.md`, generated
  output filenames, etc.) are not checked, so false positives should be rare, at
  the cost of not catching every possible stale reference.

  Exits 1 and lists every broken link/reference if any are found; exits 0 otherwise.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Directories to exclude entirely: build output / vendor / vcs / worktree noise.
# Matched against a path RELATIVE to the repo root, never the absolute path
# (the repo itself may live under a .claude/worktrees/* checkout).
$ExcludeDirPattern = '(^|[\\/])(bin|obj|\.git|node_modules|\.claude[\\/]worktrees)([\\/]|$)'

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

# Collect all markdown files.
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

# --- Part 2: stale doc-filename references inside src/**/*.cs and tests/**/*.cs comments (R12) ---

# Every *.md file anywhere in the repo (not just docs/) is a "known" doc for this purpose, so a
# reference to e.g. an analyzer-release file or a tests/Fixtures README still resolves.
$allDocFiles = Get-ChildItem -LiteralPath $root -Recurse -Filter '*.md' -File |
    Where-Object {
        $rel = $_.FullName.Substring($root.Length + 1) -replace '\\', '/'
        -not (Test-Excluded $rel)
    } |
    ForEach-Object { $_.Name }
$knownExact = [System.Collections.Generic.HashSet[string]]::new([string[]]($allDocFiles | ForEach-Object { $_.ToLowerInvariant() }))

function Test-KnownDocName([string]$Name) {
    $lower = $Name.ToLowerInvariant()
    if ($knownExact.Contains($lower)) { return $true }
    foreach ($f in $allDocFiles) {
        if ($f.ToLowerInvariant().EndsWith($lower)) { return $true }
    }
    return $false
}

# Conservative: only names that look like this repo's real doc convention (starts uppercase).
$DocNameRegex = [regex]'\b[A-Z][A-Za-z0-9-]*\.md\b'

$codeFiles = @()
foreach ($sub in @('src', 'tests')) {
    $dir = Join-Path $root $sub
    if (Test-Path -LiteralPath $dir) {
        $codeFiles += Get-ChildItem -LiteralPath $dir -Recurse -Filter '*.cs' -File |
            Where-Object {
                $rel = $_.FullName.Substring($root.Length + 1) -replace '\\', '/'
                $rel -notmatch '([\\/])(bin|obj)([\\/]|$)'
            }
    }
}

$brokenCodeRefs = New-Object System.Collections.Generic.List[object]

foreach ($file in $codeFiles) {
    $relFile = $file.FullName.Substring($root.Length + 1) -replace '\\', '/'
    $lineNum = 0
    $inBlockComment = $false
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNum++
        $commentText = ''

        if ($inBlockComment) {
            $endIdx = $line.IndexOf('*/')
            if ($endIdx -ge 0) {
                $commentText += $line.Substring(0, $endIdx)
                $inBlockComment = $false
            } else {
                $commentText += $line
            }
        }

        if (-not $inBlockComment) {
            $lineComment = [regex]::Match($line, '//.*$')
            if ($lineComment.Success) { $commentText += ' ' + $lineComment.Value }

            $blockStart = $line.IndexOf('/*')
            if ($blockStart -ge 0) {
                $blockEnd = $line.IndexOf('*/', $blockStart)
                if ($blockEnd -ge 0) {
                    $commentText += ' ' + $line.Substring($blockStart, $blockEnd - $blockStart)
                } else {
                    $commentText += ' ' + $line.Substring($blockStart)
                    $inBlockComment = $true
                }
            }
        }

        if ([string]::IsNullOrWhiteSpace($commentText)) { continue }

        foreach ($m in $DocNameRegex.Matches($commentText)) {
            $name = $m.Value
            if (-not (Test-KnownDocName $name)) {
                $brokenCodeRefs.Add([PSCustomObject]@{
                    File   = $relFile
                    Line   = $lineNum
                    Target = $name
                })
            }
        }
    }
}

if ($broken.Count -gt 0 -or $brokenCodeRefs.Count -gt 0) {
    if ($broken.Count -gt 0) {
        Write-Host "`n[FAIL] $($broken.Count) broken doc link(s):`n" -ForegroundColor Red
        foreach ($b in $broken) {
            Write-Host "  $($b.File):$($b.Line) -> $($b.Target)" -ForegroundColor Red
        }
    }
    if ($brokenCodeRefs.Count -gt 0) {
        Write-Host "`n[FAIL] $($brokenCodeRefs.Count) stale doc reference(s) in source comments:`n" -ForegroundColor Red
        foreach ($b in $brokenCodeRefs) {
            Write-Host "  $($b.File):$($b.Line) -> $($b.Target)" -ForegroundColor Red
        }
    }
    exit 1
}

Write-Host "[PASS] 0 broken doc links ($($mdFiles.Count) files checked), 0 stale doc references ($($codeFiles.Count) source files checked)" -ForegroundColor Green
exit 0
