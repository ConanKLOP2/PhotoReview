# Script to fetch and verify camera RAW test samples for PhotoReview
# Follows fetch-native.ps1 style: PowerShell 5.1 compatible, idempotent, SHA-256 pinned, fail-closed on mismatch.

[CmdletBinding()]
param(
    [string]$SamplesFile = "",
    [string]$TargetDir = "",
    [string]$FormatFilter = "",
    [int]$Limit = 0,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SamplesFile)) {
    $SamplesFile = Join-Path $PSScriptRoot "raw-samples.txt"
}
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $PSScriptRoot "..\tests\Fixtures\raw-corpus"
}


function Test-FetchRawSamplesSelfTest {
    Write-Host "Running fetch-raw-samples self-test..." -ForegroundColor Cyan
    $testTempDir = Join-Path ([System.IO.Path]::GetTempPath()) "photoreview-raw-selftest-$([guid]::NewGuid().ToString('N'))"
    $testSamplesFile = Join-Path $testTempDir "invalid-sample.txt"
    New-Item -ItemType Directory -Path $testTempDir -Force | Out-Null
    try {
        # Entry with intentionally corrupted hash (all zeroes)
        $badLine = "CR2`tTest Camera`thttps://raw.pixls.us/getfile.php/758/nice/Canon - EOS 350D - RAW (3:2).CR2`t0000000000000000000000000000000000000000000000000000000000000000`thttps://creativecommons.org/publicdomain/zero/1.0/`ttest_mismatch.CR2"
        Set-Content -LiteralPath $testSamplesFile -Value $badLine -Encoding UTF8

        $failedClosed = $false
        try {
            & $PSCommandPath -SamplesFile $testSamplesFile -TargetDir $testTempDir -Limit 1
        }
        catch {
            if ($_.Exception.Message -like "*hash mismatch*") {
                $failedClosed = $true
            } else {
                throw "Unexpected error during self-test: $($_.Exception.Message)"
            }
        }

        if (-not $failedClosed) {
            throw "SELF-TEST FAILED: Script did not fail-closed on corrupted SHA-256 hash!"
        }

        $targetFile = Join-Path $testTempDir "test_mismatch.CR2"
        if (Test-Path -LiteralPath $targetFile) {
            throw "SELF-TEST FAILED: Corrupted file was left in target directory!"
        }

        Write-Host "PASS: fetch-raw-samples self-test passed (correctly rejected hash mismatch and cleaned up)." -ForegroundColor Green
        return $true
    }
    finally {
        if (Test-Path -LiteralPath $testTempDir) {
            Remove-Item -LiteralPath $testTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($SelfTest) {
    Test-FetchRawSamplesSelfTest
    exit 0
}

if (-not (Test-Path -LiteralPath $SamplesFile)) {
    throw "Samples file not found at: $SamplesFile"
}

if (-not (Test-Path -LiteralPath $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

$lines = Get-Content -LiteralPath $SamplesFile -Encoding UTF8
$processed = 0

foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
        continue
    }

    $parts = $trimmed.Split("`t")
    if ($parts.Length -lt 6) {
        Write-Warning "Skipping invalid sample entry (less than 6 tab-separated columns): $trimmed"
        continue
    }

    $format = $parts[0].Trim()
    $camera = $parts[1].Trim()
    $url = $parts[2].Trim()
    $expectedHash = $parts[3].Trim().ToUpperInvariant()
    $license = $parts[4].Trim()
    $rawFileName = $parts[5].Trim()
    $fileName = [string]::Join("_", $rawFileName.Split([System.IO.Path]::GetInvalidFileNameChars()))

    if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
        throw "Invalid SHA-256 hex format for $fileName : found '$expectedHash'"
    }

    if ($FormatFilter -and ($format -ne $FormatFilter.ToUpperInvariant())) {
        continue
    }

    $targetPath = Join-Path $TargetDir $fileName

    # Check if target already exists and hash matches (idempotent)
    if (Test-Path -LiteralPath $targetPath) {
        $existingHash = ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash).ToUpperInvariant()
        if ($existingHash -eq $expectedHash) {
            Write-Host "SKIP: $fileName already present and SHA-256 matches." -ForegroundColor Green
            $processed++
            if ($Limit -gt 0 -and $processed -ge $Limit) { break }
            continue
        } else {
            Write-Warning "MISMATCH: $fileName existing hash mismatch ($existingHash vs $expectedHash). Re-fetching..."
            Remove-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Fetching [$format] $camera : $fileName ..."
    $tempFile = Join-Path $TargetDir "$fileName.tmp.$([guid]::NewGuid().ToString('N'))"
    $encodedUrl = $url.Replace(" ", "%20")

    try {
        # Secure HTTPS only, follow redirects, retry 3 times
        curl.exe -sSL --fail --retry 3 --proto "=https" --proto-redir "=https" "$encodedUrl" -o "$tempFile"
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed for $fileName (curl exit code $LASTEXITCODE)"
        }

        $downloadedHash = ((Get-FileHash -LiteralPath $tempFile -Algorithm SHA256).Hash).ToUpperInvariant()
        if ($downloadedHash -ne $expectedHash) {
            Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
            throw "Downloaded file hash mismatch for $fileName : got $downloadedHash, expected $expectedHash"
        }

        Move-Item -LiteralPath $tempFile -Destination $targetPath -Force
        Write-Host "PASS: Fetched and verified $fileName successfully." -ForegroundColor Green
        $processed++
        if ($Limit -gt 0 -and $processed -ge $Limit) { break }
    }
    finally {
        if (Test-Path -LiteralPath $tempFile) {
            Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "Done: $processed samples verified in $TargetDir." -ForegroundColor Green
