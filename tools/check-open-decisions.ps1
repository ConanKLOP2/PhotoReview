#!/usr/bin/env pwsh
<#
.SYNOPSIS
  CI gate: fails if docs/refactoring/OPEN-DECISIONS.md's generated Decided table is out of date.

.DESCRIPTION
  Same pattern as tools/docs-budget.ps1 / tools/check-doc-links.ps1: a read-only check with no
  side effects, wired into .github/workflows/ci.yml's `repo-checks` job and tools/verify-all.ps1,
  needing no .NET build.

  Runs tools/generate-open-decisions.ps1 against a scratch copy of OPEN-DECISIONS.md (never the
  real file -- this check must never itself modify the working tree) and compares the result
  against the committed file. Any difference means either:
    - a PR hand-edited the generated region instead of running the generator, or
    - a PR added/changed a decisions/*.md file (or its frontmatter) without re-running the
      generator afterwards, or
    - a decisions/*.md file is missing frontmatter, or has a duplicate id/order (the generator
      itself throws for these -- surfaced here as a check failure with the generator's error).

  Exits 1 with a diff-style message if the generated region is stale or the generator errors;
  exits 0 if it is up to date.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$realPath = Join-Path $root 'docs\refactoring\OPEN-DECISIONS.md'
$generatorPath = Join-Path $PSScriptRoot 'generate-open-decisions.ps1'

$scratchPath = Join-Path ([System.IO.Path]::GetTempPath()) "OPEN-DECISIONS.$([guid]::NewGuid().ToString('N')).md"
Copy-Item -LiteralPath $realPath -Destination $scratchPath -Force

try {
    try {
        & $generatorPath -TargetPath $scratchPath | Out-Null
    }
    catch {
        Write-Host "[FAIL] tools/generate-open-decisions.ps1 could not run: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }

    $real = [System.IO.File]::ReadAllText($realPath, [System.Text.Encoding]::UTF8)
    $generated = [System.IO.File]::ReadAllText($scratchPath, [System.Text.Encoding]::UTF8)

    if ($real -ne $generated) {
        Write-Host "[FAIL] docs/refactoring/OPEN-DECISIONS.md's generated Decided table is out of date." -ForegroundColor Red
        Write-Host "Run tools/generate-open-decisions.ps1 and commit the result. Diff (committed vs. freshly generated):" -ForegroundColor Red

        $realLines = $real -split "`r?`n"
        $generatedLines = $generated -split "`r?`n"
        $max = [Math]::Max($realLines.Count, $generatedLines.Count)
        for ($i = 0; $i -lt $max; $i++) {
            $a = if ($i -lt $realLines.Count) { $realLines[$i] } else { $null }
            $b = if ($i -lt $generatedLines.Count) { $generatedLines[$i] } else { $null }
            if ($a -ne $b) {
                if ($null -ne $a) { Write-Host "- $a" -ForegroundColor Red }
                if ($null -ne $b) { Write-Host "+ $b" -ForegroundColor Green }
            }
        }
        exit 1
    }

    Write-Host "[PASS] docs/refactoring/OPEN-DECISIONS.md's generated Decided table is up to date" -ForegroundColor Green
    exit 0
}
finally {
    Remove-Item -LiteralPath $scratchPath -Force -ErrorAction SilentlyContinue
}
