# Script to fetch and verify camera RAW test samples for PhotoReview
# Follows fetch-native.ps1 style: PowerShell 5.1 compatible, idempotent, SHA-256 pinned, fail-closed on mismatch.
# License trust boundary: normal fetch mode matches every pinned file URL to raw.pixls.us' live repository
# record and requires its CC0 URL and label to agree with the manifest. Self-test injects fixed API records.

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
    New-Item -ItemType Directory -Path $testTempDir -Force | Out-Null
    try {
        $validManifest = Join-Path $testTempDir 'valid.txt'
        Set-Content -LiteralPath $validManifest -Value "CR2`tCamera`thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2`t$([string]::new('A', 64))`thttps://creativecommons.org/publicdomain/zero/1.0/`tsample.CR2" -Encoding UTF8
        # Windows PowerShell 5.1 does not expose .Count on a scalar PSCustomObject;
        # keep singleton manifest results array-shaped for the same check as pwsh 7.
        $validSamples = @(Read-RawSampleManifest -Path $validManifest)
        if ($validSamples.Count -ne 1) { throw 'SELF-TEST FAILED: Valid CC0 manifest row was not accepted.' }
        $mockApi = [pscustomobject]@{ data = ,@(
            'Canon', 'EOS 7D', 'RAW', 17.92, '',
            "<a href='https://creativecommons.org/publicdomain/zero/1.0/' title='Creative Commons 0 - Public Domain' class='cc'>co</a>",
            '2016-12-29', "<a href='https://raw.pixls.us/getfile.php/129/nice/sample.CR2'>sample.CR2</a>", ''
        ) } | ConvertTo-Json -Depth 4
        $cc0Record = ConvertFrom-RawPixlsRepositoryJson -Json $mockApi
        Assert-RawPixlsLicenses -Samples $validSamples -Records $cc0Record
        Assert-RawSampleHash -ExpectedHash ([string]::new('A', 64)) -ActualHash ([string]::new('A', 64)) -FileName 'sample.CR2'

        $hashRejected = $false
        try {
            Assert-RawSampleHash -ExpectedHash ([string]::new('A', 64)) -ActualHash ([string]::new('B', 64)) -FileName 'sample.CR2'
        }
        catch {
            if ($_.Exception.Message -like '*SHA-256 mismatch*') { $hashRejected = $true }
            else { throw "Unexpected hash self-test error: $($_.Exception.Message)" }
        }
        if (-not $hashRejected) { throw 'SELF-TEST FAILED: Script accepted a sample with the wrong SHA-256.' }

        foreach ($case in @(
            @{ Name = 'missing-column'; Row = "CR2`tCamera`thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2`t$([string]::new('A', 64))`tsample.CR2"; Expected = '*at least six tab-separated columns*' },
            @{ Name = 'missing-license'; Row = (@('CR2', 'Camera', 'https://raw.pixls.us/getfile.php/129/nice/sample.CR2', [string]::new('A', 64), '', 'sample.CR2') -join "`t"); Expected = '*must record the exact CC0 1.0 URL*' },
            @{ Name = 'non-CC0-manifest'; Row = "CR2`tCamera`thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2`t$([string]::new('A', 64))`thttps://creativecommons.org/licenses/by/4.0/`tsample.CR2"; Expected = '*must record the exact CC0 1.0 URL*' }
        )) {
            $manifest = Join-Path $testTempDir "$($case.Name).txt"
            Set-Content -LiteralPath $manifest -Value $case.Row -Encoding UTF8
            $failedClosed = $false
            try {
                [void](Read-RawSampleManifest -Path $manifest)
            }
            catch {
                if ($_.Exception.Message -like $case.Expected) {
                    $failedClosed = $true
                } else {
                    throw "Unexpected error while testing $($case.Name) metadata: $($_.Exception.Message)"
                }
            }

            if (-not $failedClosed) {
                throw "SELF-TEST FAILED: Script accepted $($case.Name) manifest metadata."
            }
        }

        $nonCc0Api = [pscustomobject]@{ data = ,@(
            'Canon', 'EOS 7D', 'RAW', 17.92, '',
            "<a href='https://creativecommons.org/licenses/by/4.0/' title='Creative Commons Attribution 4.0' class='cc'>by</a>",
            '2016-12-29', "<a href='https://raw.pixls.us/getfile.php/129/nice/sample.CR2'>sample.CR2</a>", ''
        ) } | ConvertTo-Json -Depth 4
        $nonCc0Record = ConvertFrom-RawPixlsRepositoryJson -Json $nonCc0Api
        $recordRejected = $false
        try {
            Assert-RawPixlsLicenses -Samples $validSamples -Records @($nonCc0Record)
        }
        catch {
            if ($_.Exception.Message -like '*is not recorded as CC0 1.0*') { $recordRejected = $true }
            else { throw "Unexpected API-record self-test error: $($_.Exception.Message)" }
        }
        if (-not $recordRejected) { throw 'SELF-TEST FAILED: Script accepted a non-CC0 source API record.' }

        $missingRecordRejected = $false
        try {
            Assert-RawPixlsLicenses -Samples $validSamples -Records @()
        }
        catch {
            if ($_.Exception.Message -like '*was not found in the raw.pixls.us source license records*') { $missingRecordRejected = $true }
            else { throw "Unexpected missing-record self-test error: $($_.Exception.Message)" }
        }
        if (-not $missingRecordRejected) { throw 'SELF-TEST FAILED: Script accepted a manifest URL missing from source API records.' }

        Write-Host 'PASS: fetch-raw-samples self-test matched injected CC0 source metadata and rejected missing/non-CC0 records, malformed manifest rows, and SHA-256 mismatches.' -ForegroundColor Green
        return $true
    }
    finally {
        if (Test-Path -LiteralPath $testTempDir) {
            Remove-Item -LiteralPath $testTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Assert-RawSampleLicense {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$LicenseUrl,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    # This is a manifest assertion, not a network lookup or legal conclusion. Keep exact-match fail-closed.
    if (-not [string]::Equals($LicenseUrl, 'https://creativecommons.org/publicdomain/zero/1.0/', [StringComparison]::Ordinal)) {
        throw "Sample '$FileName' must record the exact CC0 1.0 URL https://creativecommons.org/publicdomain/zero/1.0/ in the reviewed manifest."
    }
}

function Assert-RawSampleHash {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedHash,
        [Parameter(Mandatory = $true)][string]$ActualHash,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    if (-not [string]::Equals($ActualHash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Downloaded file SHA-256 mismatch for $FileName : got $ActualHash, expected $ExpectedHash"
    }
}

function Get-RawPixlsRepositoryRecords {
    $apiUrl = 'https://raw.pixls.us/json/getrepository.php?set=all'
    try {
        $response = Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec 120
        return ConvertFrom-RawPixlsRepositoryJson -Json $response.Content
    }
    catch {
        throw "Could not fetch/parse raw.pixls.us repository license records; refusing to fetch corpus samples. $($_.Exception.Message)"
    }
}

function ConvertFrom-RawPixlsRepositoryJson {
    param([Parameter(Mandatory = $true)][string]$Json)

    try { $repository = ConvertFrom-Json -InputObject $Json }
    catch { throw "raw.pixls.us repository API response was invalid JSON. $($_.Exception.Message)" }
    if ($null -eq $repository.data) { throw 'raw.pixls.us repository API response has no data array; refusing to fetch samples.' }

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $repository.data) {
        if ($row.Count -lt 8) { throw 'raw.pixls.us repository API returned a malformed row; refusing to fetch samples.' }
        $licenseHtml = [System.Net.WebUtility]::HtmlDecode([string]$row[5])
        $fileHtml = [System.Net.WebUtility]::HtmlDecode([string]$row[7])
        $licenseHref = [regex]::Match($licenseHtml, '<a\b[^>]*href=[''"]([^''"]+)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $licenseTitle = [regex]::Match($licenseHtml, '\btitle=[''"]([^''"]*)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $fileHref = [regex]::Match($fileHtml, '<a\b[^>]*href=[''"]([^''"]+)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $licenseHref.Success -or -not $licenseTitle.Success -or -not $fileHref.Success) {
            throw 'raw.pixls.us repository API row lacked parseable file/license metadata; refusing to fetch samples.'
        }
        $fileUrl = $fileHref.Groups[1].Value
        $idMatch = [regex]::Match($fileUrl, '^https://raw\.pixls\.us/getfile\.php/(?<id>\d+)/nice/', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $idMatch.Success) { continue }
        $records.Add([pscustomobject]@{
            Id = $idMatch.Groups['id'].Value
            FileUrl = $fileUrl
            LicenseUrl = $licenseHref.Groups[1].Value
            LicenseLabel = $licenseTitle.Groups[1].Value
        })
    }
    return $records.ToArray()
}

function Assert-RawPixlsLicenses {
    param(
        [Parameter(Mandatory = $true)][object[]]$Samples,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Records
    )

    foreach ($sample in $Samples) {
        $sampleUri = $null
        if (-not [uri]::TryCreate($sample.Url, [UriKind]::Absolute, [ref]$sampleUri) -or
            -not [string]::Equals($sampleUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($sampleUri.Host, 'raw.pixls.us', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Sample '$($sample.FileName)' must use an HTTPS raw.pixls.us URL so it can be matched to the source license record."
        }
        $idMatch = [regex]::Match($sampleUri.AbsolutePath, '^/getfile\.php/(?<id>\d+)/nice/', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $idMatch.Success) { throw "Sample '$($sample.FileName)' has no verifiable raw.pixls.us file ID in its URL." }
        $record = $Records | Where-Object {
            $_.Id -eq $idMatch.Groups['id'].Value -and
            [string]::Equals(([uri]$_.FileUrl).AbsoluteUri, $sampleUri.AbsoluteUri, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $record) { throw "Sample '$($sample.FileName)' URL was not found in the raw.pixls.us source license records." }

        Assert-RawSampleLicense -LicenseUrl $sample.LicenseUrl -FileName $sample.FileName
        if (-not [string]::Equals($record.LicenseUrl, $sample.LicenseUrl, [StringComparison]::Ordinal) -or
            -not [string]::Equals($record.LicenseLabel, 'Creative Commons 0 - Public Domain', [StringComparison]::Ordinal)) {
            throw "Sample '$($sample.FileName)' is not recorded as CC0 1.0 in the raw.pixls.us source API."
        }
    }
}

function Read-RawSampleManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $samples = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }

        $parts = $trimmed.Split("`t")
        if ($parts.Length -lt 6) {
            throw "Invalid sample entry (expected at least six tab-separated columns, including license URL): $trimmed"
        }

        $format = $parts[0].Trim()
        $camera = $parts[1].Trim()
        $url = $parts[2].Trim()
        $expectedHash = $parts[3].Trim().ToUpperInvariant()
        $license = $parts[4].Trim()
        $rawFileName = $parts[5].Trim()
        $fileName = [string]::Join('_', $rawFileName.Split([System.IO.Path]::GetInvalidFileNameChars()))

        if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
            throw "Invalid SHA-256 hex format for $fileName : found '$expectedHash'"
        }
        Assert-RawSampleLicense -LicenseUrl $license -FileName $fileName

        $samples.Add([pscustomobject]@{
            Format = $format
            Camera = $camera
            Url = $url
            ExpectedHash = $expectedHash
            LicenseUrl = $license
            FileName = $fileName
        })
    }

    return $samples
}

if ($SelfTest) {
    Test-FetchRawSamplesSelfTest
    exit 0
}

if (-not (Test-Path -LiteralPath $SamplesFile)) {
    throw "Samples file not found at: $SamplesFile"
}

$processed = 0
$samples = Read-RawSampleManifest -Path $SamplesFile
if ($samples.Count -gt 0) {
    $repositoryRecords = Get-RawPixlsRepositoryRecords
    Assert-RawPixlsLicenses -Samples $samples -Records $repositoryRecords
}
if (-not (Test-Path -LiteralPath $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

foreach ($sample in $samples) {
    $format = $sample.Format
    $camera = $sample.Camera
    $url = $sample.Url
    $expectedHash = $sample.ExpectedHash
    $fileName = $sample.FileName

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
        try {
            Assert-RawSampleHash -ExpectedHash $expectedHash -ActualHash $downloadedHash -FileName $fileName
        }
        catch {
            Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
            throw
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
