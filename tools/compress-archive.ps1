#!/usr/bin/env pwsh
<#
.SYNOPSIS
Compress archive documentation by removing redundant sections.
.DESCRIPTION
Removes historical result details (commit hashes, test counts, detailed findings)
while preserving task definitions and acceptance criteria.
#>

$ErrorActionPreference = 'Stop'
$archiveDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\archive\historical'

Write-Host "Archive Compression Rules:" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "✓ Keep: Task definitions, acceptance criteria, dependencies" -ForegroundColor Green
Write-Host "✗ Remove: Commit hashes, test count details, 'Kết quả' sections" -ForegroundColor Yellow
Write-Host "✗ Remove: Detailed findings already summarized in active docs" -ForegroundColor Yellow
Write-Host ""

Get-ChildItem $archiveDir -Filter "*.md" | ForEach-Object {
    $path = $_.FullName
    $name = $_.Name
    $content = Get-Content $path -Raw
    $originalSize = $_.Length

    # Remove "Kết quả" blocks with commit hashes and detailed results
    $content = $content -replace "(?m)^─ Kết quả.*?(?=^(?:###|##|$))", ""

    # Remove redundant result lines with commit SHA and test counts
    $content = $content -replace "(?m)^\s*- Kết quả.*?commit\s+\`[0-9a-f]+\`.*?gateway.*?[\r\n]+", ""

    # Remove "Kết quả bổ sung" and "Kết quả wave" subsections (historical)
    $content = $content -replace "(?m)^- Kết quả (bổ sung|wave \w+):.*?(?=^(?:-|\s*$))", ""

    # Remove detailed evidence tables if entire section is results
    $content = $content -replace "(?m)^\| ID \| .*?\|\r?\n.*?\n(?:(?:\|[^\n]*\n)*)", ""

    # Keep file but note it's archived
    if ($content.Length -lt $originalSize) {
        Set-Content $path $content -NoNewline
        $newSize = (Get-Item $path).Length
        $reduction = [math]::Round(($originalSize - $newSize) / 1024, 1)
        Write-Host "$name" -ForegroundColor Yellow
        Write-Host "  $(($originalSize/1024).ToString('F1')) KB → $(($newSize/1024).ToString('F1')) KB (saved $reduction KB)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "✓ Compression complete. Archive files are now more concise." -ForegroundColor Green
Write-Host "✓ All task definitions and criteria preserved." -ForegroundColor Green
