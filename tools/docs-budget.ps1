#!/usr/bin/env pwsh
# Runs on Windows PowerShell 5.1 and PowerShell 7 (the dev machine has no pwsh).
param([switch]$Check)

$ErrorActionPreference = 'Stop'

# T0: total of the always-read files. T1: per file, for docs read one at a time per task (AGENTS.md).
$T0BudgetBytes = 16 * 1024
$T1FileBudgetBytes = 24 * 1024

function Get-DocTier {
    param([string]$Path)
    $p = $Path -replace '\\', '/'
    if ($p -match '^(AGENTS|task_on_progress|docs/INDEX)\.md$') { return 'T0' }
    if ($p -match '^docs/(archive|refactoring/archive)/') { return 'T2' }
    if ($p -match '^README\.md$' -or
        $p -match '^docs/[^/]+\.md$' -or
        $p -match '^docs/adr/[^/]+\.md$' -or
        $p -match '^docs/refactoring/([^/]+|arch-review/[^/]+)\.md$') { return 'T1' }
    return 'Other'
}

# Measure the working tree (not HEAD) so staged-new and uncommitted growth count; quotepath=off keeps non-ASCII names intact.
$root = (Get-Location).Path
$docs = @(& git -c core.quotepath=off ls-files | Where-Object { $_ -match '\.(md|txt)$' -and (Test-Path -LiteralPath $_ -PathType Leaf) } | ForEach-Object {
    $content = [System.IO.File]::ReadAllText((Join-Path $root $_), [System.Text.Encoding]::UTF8)
    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($content)
    [pscustomobject]@{
        Path   = $_
        Bytes  = $bytes
        Lines  = @($content -split "`n").Count
        Tier   = Get-DocTier $_
        Tokens = [Math]::Ceiling($bytes / 3)
    }
})

function Format-KB([long]$bytes) { [Math]::Round($bytes / 1024, 1) }

Write-Host "## Documentation Budget Report`n" -ForegroundColor Cyan
foreach ($tier in 'T0', 'T1', 'T2', 'Other') {
    $group = @($docs | Where-Object { $_.Tier -eq $tier })
    if ($group.Count -eq 0) { continue }
    $sum = ($group | Measure-Object -Property Bytes -Sum).Sum
    $budget = switch ($tier) {
        'T0' { "$(Format-KB $T0BudgetBytes) KB total" }
        'T1' { "$(Format-KB $T1FileBudgetBytes) KB per file" }
        default { 'none' }
    }
    $over = if ($tier -eq 'T0' -and $sum -gt $T0BudgetBytes) { ' [OVER]' } else { '' }
    Write-Host "### $tier (Budget: $budget, Used: $(Format-KB $sum) KB, ~$([Math]::Ceiling($sum / 3)) tokens)$over`n"
    Write-Host "File | Bytes | Lines | Tokens"
    Write-Host "---- | ---- | ---- | ----"
    $group | Sort-Object -Property Bytes -Descending | ForEach-Object {
        $flag = if ($tier -eq 'T1' -and $_.Bytes -gt $T1FileBudgetBytes) { ' [OVER]' } else { '' }
        Write-Host "$($_.Path) | $($_.Bytes) ($(Format-KB $_.Bytes) KB) | $($_.Lines) | $($_.Tokens)$flag"
    }
    Write-Host ""
}

$t0 = ($docs | Where-Object { $_.Tier -eq 'T0' } | Measure-Object -Property Bytes -Sum).Sum
if ($null -eq $t0) { $t0 = 0 }
$t1Over = @($docs | Where-Object { $_.Tier -eq 'T1' -and $_.Bytes -gt $T1FileBudgetBytes })
$largestT1 = ($docs | Where-Object { $_.Tier -eq 'T1' } | Measure-Object -Property Bytes -Maximum).Maximum
if ($null -eq $largestT1) { $largestT1 = 0 }
$total = ($docs | Measure-Object -Property Bytes -Sum).Sum

Write-Host "## Summary`n" -ForegroundColor Cyan
Write-Host "T0 total: $(Format-KB $t0) / $(Format-KB $T0BudgetBytes) KB"
Write-Host "T1 files over $(Format-KB $T1FileBudgetBytes) KB: $($t1Over.Count)"
Write-Host "Total docs: $(Format-KB $total) KB (~$([Math]::Ceiling($total / 3)) tokens)"
Write-Host "Cold start (T0 + largest T1): $(Format-KB ($t0 + $largestT1)) KB (~$([Math]::Ceiling(($t0 + $largestT1) / 3)) tokens)`n"

if ($Check) {
    $errors = @()
    if ($t0 -gt $T0BudgetBytes) { $errors += "T0 exceeds $(Format-KB $T0BudgetBytes) KB ($(Format-KB $t0) KB)" }
    foreach ($doc in $t1Over) { $errors += "T1 file over $(Format-KB $T1FileBudgetBytes) KB: $($doc.Path) ($(Format-KB $doc.Bytes) KB)" }
    if ($errors.Count -gt 0) {
        Write-Host "[FAIL] $($errors -join '; ')" -ForegroundColor Red
        exit 1
    }
    Write-Host "[PASS] Budget check passed" -ForegroundColor Green
    exit 0
}
