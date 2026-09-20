#!/usr/bin/env pwsh
param([switch]$Check)

$ErrorActionPreference = 'Stop'

$Budget = @{ 'T0' = 12 * 1024; 'T1' = 15 * 1024 }

function Get-DocTier {
    param([string]$Path)
    $rel = $Path -replace '^docs[/\\]refactoring[/\\]', ''

    if ($rel -match '^(AGENTS|task_on_progress|INDEX)\.md$') { return 'T0' }
    if ($rel -match '^(OPTIMIZE-CLEAN|STRUCTURE-OPTIMIZE|TEST-CLEANUP|T89-FIT|DOCS-TOKEN-DIET|OPEN-DECISIONS)' -and $rel -notmatch 'archive') { return 'T1' }
    if ($rel -match '^(adr|APP-MECHANISMS)') { return 'T1' }
    if ($rel -match 'archive|results|diagnosis') { return 'T2' }
    return 'Other'
}

$Docs = @()
$Totals = @{ 'T0' = 0; 'T1' = 0; 'T2' = 0; 'Other' = 0 }

git ls-files | Where-Object { $_ -match '\.(md|txt)$' } | ForEach-Object {
    $Content = & git show "HEAD:$_" 2>$null
    $Bytes = [System.Text.Encoding]::UTF8.GetByteCount($Content)
    $Lines = @($Content -split "`n").Count
    $Tier = Get-DocTier $_
    $Tokens = [Math]::Ceiling($Bytes / 3)

    $Docs += @{
        Path = $_
        Bytes = $Bytes
        Lines = $Lines
        Tier = $Tier
        Tokens = $Tokens
    }

    $Totals[$Tier] += $Bytes
}

Write-Host "## Documentation Budget Report`n" -ForegroundColor Cyan

$ByTier = $Docs | Group-Object -Property Tier | Sort-Object { @{'T0'=0; 'T1'=1; 'T2'=2; 'Other'=3}[$_.Name] }

foreach ($TierGroup in $ByTier) {
    $Tier = $TierGroup.Name
    $TierBytes = ($TierGroup.Group | ForEach-Object { $_.Bytes } | Measure-Object -Sum).Sum
    $TierTokens = [Math]::Ceiling($TierBytes / 3)
    $BudgetKB = if ($Budget.ContainsKey($Tier)) { [Math]::Round($Budget[$Tier] / 1024, 1) } else { "N/A" }
    $Status = if ($Budget.ContainsKey($Tier) -and $TierBytes -gt $Budget[$Tier]) { " [OVER]" } else { "" }

    Write-Host "### $Tier (Budget: ${BudgetKB} KB, Used: $([Math]::Round($TierBytes / 1024, 1)) KB, ~$TierTokens tokens)$Status`n"
    Write-Host "File | Bytes | Lines | Tokens"
    Write-Host "---- | ---- | ---- | ----"

    $TierGroup.Group | Sort-Object -Property Bytes -Descending | ForEach-Object {
        $KB = [Math]::Round($_.Bytes / 1024, 1)
        Write-Host "$($_.Path) | $($_.Bytes) ($KB KB) | $($_.Lines) | $($_.Tokens)"
    }
    Write-Host ""
}

Write-Host "## Summary`n" -ForegroundColor Cyan
Write-Host "Tier | Budget | Used | Status"
Write-Host "---- | ---- | ---- | ----"
foreach ($T in @('T0', 'T1', 'T2', 'Other')) {
    $Budget_KB = if ($Budget.ContainsKey($T)) { "$([Math]::Round($Budget[$T]/1024,1)) KB" } else { "N/A" }
    $Used_KB = [Math]::Round($Totals[$T] / 1024, 1)
    $Status = if ($Budget.ContainsKey($T) -and $Totals[$T] -gt $Budget[$T]) { "[OVER]" } else { "[OK]" }
    Write-Host "$T | $Budget_KB | $Used_KB KB | $Status"
}

$TotalBytes = $Totals.Values | Measure-Object -Sum | Select-Object -ExpandProperty Sum
$TotalTokens = [Math]::Ceiling($TotalBytes / 3)
Write-Host "`nTotal docs: $([Math]::Round($TotalBytes / 1024, 1)) KB (~$TotalTokens tokens)`n"

$ColdStart = $Totals['T0'] + 12 * 1024
$ColdTokens = [Math]::Ceiling($ColdStart / 3)
Write-Host "Cold start (T0 + one T1): $([Math]::Round($ColdStart / 1024, 1)) KB (~$ColdTokens tokens)`n"

if ($Check) {
    $Errors = @()
    if ($Totals['T0'] -gt $Budget['T0']) { $Errors += "T0 exceeds budget" }
    if ($Totals['T1'] -gt $Budget['T1']) { $Errors += "T1 exceeds budget" }

    if ($Errors.Count -gt 0) {
        Write-Host "[FAIL] $($Errors -join ', ')" -ForegroundColor Red
        exit 1
    } else {
        Write-Host "[PASS] Budget check passed" -ForegroundColor Green
        exit 0
    }
}
