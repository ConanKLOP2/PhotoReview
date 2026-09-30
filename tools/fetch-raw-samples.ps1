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
        $cc0Record = @(ConvertFrom-RawPixlsRepositoryJson -Json $mockApi)
        Assert-RawPixlsLicenses -Samples $validSamples -Records $cc0Record
        Assert-RawSampleHash -ExpectedHash ([string]::new('A', 64)) -ActualHash ([string]::new('A', 64)) -FileName 'sample.CR2'

        # Regression: a multi-row manifest / multi-record API must come back FLAT through the same call shape the main
        # path uses (@(Read-RawSampleManifest ...), @(& provider)). A stray unary comma nests each result one level deeper.
        $twoRowManifest = Join-Path $testTempDir 'two-rows.txt'
        Set-Content -LiteralPath $twoRowManifest -Value @(
            "CR2`tCamera`thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2`t$([string]::new('A', 64))`thttps://creativecommons.org/publicdomain/zero/1.0/`tsample.CR2",
            "CR3`tCamera`thttps://raw.pixls.us/getfile.php/130/nice/sample2.CR3`t$([string]::new('B', 64))`thttps://creativecommons.org/publicdomain/zero/1.0/`tsample2.CR3"
        ) -Encoding UTF8
        $twoSamples = @(Read-RawSampleManifest -Path $twoRowManifest)
        if ($twoSamples.Count -ne 2 -or $twoSamples[0] -is [array] -or $twoSamples[1].FileName -ne 'sample2.CR3') {
            throw 'SELF-TEST FAILED: A two-row manifest did not come back as two flat sample objects.'
        }
        $twoApi = [pscustomobject]@{ data = @(
            @('Canon', 'EOS 7D', 'RAW', 17.92, '',
              "<a href='https://creativecommons.org/publicdomain/zero/1.0/' title='Creative Commons 0 - Public Domain' class='cc'>co</a>",
              '2016-12-29', "<a href='https://raw.pixls.us/getfile.php/129/nice/sample.CR2'>sample.CR2</a>", ''),
            @('Canon', 'EOS R6', 'RAW', 20.1, '',
              "<a href='https://creativecommons.org/publicdomain/zero/1.0/' title='Creative Commons 0 - Public Domain' class='cc'>co</a>",
              '2020-09-01', "<a href='https://raw.pixls.us/getfile.php/130/nice/sample2.CR3'>sample2.CR3</a>", '')
        ) } | ConvertTo-Json -Depth 4
        $twoRecords = @(ConvertFrom-RawPixlsRepositoryJson -Json $twoApi)
        if ($twoRecords.Count -ne 2 -or $twoRecords[0] -is [array] -or [string]$twoRecords[1].Id -ne '130') {
            throw 'SELF-TEST FAILED: A two-record repository response did not come back as two flat record objects.'
        }
        $liveShapedProvider = { @(ConvertFrom-RawPixlsRepositoryJson -Json $twoApi) }
        if ((Invoke-RawPixlsLicenseCheck -Samples $twoSamples -RecordsProvider $liveShapedProvider) -ne $true) {
            throw 'SELF-TEST FAILED: Two-row manifest was not license-checked against a two-record response.'
        }

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
                [void]@(Read-RawSampleManifest -Path $manifest)
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
        $nonCc0Record = @(ConvertFrom-RawPixlsRepositoryJson -Json $nonCc0Api)
        $recordRejected = $false
        try {
            Assert-RawPixlsLicenses -Samples $validSamples -Records $nonCc0Record
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

        foreach ($badName in @('..', '.', 'x.')) {
            $badManifest = Join-Path $testTempDir 'badname.txt'
            Set-Content -LiteralPath $badManifest -Value "CR2`tCamera`thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2`t$([string]::new('A', 64))`thttps://creativecommons.org/publicdomain/zero/1.0/`t$badName" -Encoding UTF8
            $nameRejected = $false
            try { [void]@(Read-RawSampleManifest -Path $badManifest) }
            catch { if ($_.Exception.Message -like '*Invalid sample file name*') { $nameRejected = $true } else { throw } }
            if (-not $nameRejected) { throw "SELF-TEST FAILED: Manifest file name '$badName' was accepted." }
        }

        $shiftedApi = [pscustomobject]@{ data = ,@(
            'Canon', 'EOS 7D', 'RAW', 17.92, '', '2016-12-29',
            "<a href='https://creativecommons.org/publicdomain/zero/1.0/' title='Creative Commons 0 - Public Domain' class='cc'>co</a>",
            "<a href='https://raw.pixls.us/getfile.php/129/nice/sample.CR2'>sample.CR2</a>", ''
        ) } | ConvertTo-Json -Depth 4
        $layoutRejected = $false
        try { [void](ConvertFrom-RawPixlsRepositoryJson -Json $shiftedApi) }
        catch { if ($_.Exception.Message -like '*table layout changed*') { $layoutRejected = $true } else { throw } }
        if (-not $layoutRejected) { throw 'SELF-TEST FAILED: A shifted raw.pixls.us column layout was accepted.' }

        # Regression: a one-row manifest must still run the license check (PS 5.1 unrolls a singleton List to a scalar
        # whose .Count is $null, which used to skip the check silently).
        $script:providerCalls = 0
        $provider = { $script:providerCalls++; return $cc0Record }
        $oneRow = Invoke-RawPixlsLicenseCheck -Samples @(Read-RawSampleManifest -Path $validManifest) -RecordsProvider $provider
        if ($oneRow -ne $true -or $script:providerCalls -ne 1) { throw 'SELF-TEST FAILED: One-row manifest skipped the raw.pixls.us license check.' }
        $nonCc0Rejected = $false
        try { [void](Invoke-RawPixlsLicenseCheck -Samples @(Read-RawSampleManifest -Path $validManifest) -RecordsProvider { $nonCc0Record }) }
        catch { if ($_.Exception.Message -like '*is not recorded as CC0 1.0*') { $nonCc0Rejected = $true } else { throw } }
        if (-not $nonCc0Rejected) { throw 'SELF-TEST FAILED: One-row manifest with a non-CC0 record was accepted.' }

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
        return @(ConvertFrom-RawPixlsRepositoryJson -Json $response.Content)
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

    $layoutHint = 'the raw.pixls.us repository table layout changed (expected license anchor in column 6 and file anchor in column 8); refusing to fetch samples.'
    $records = [System.Collections.Generic.List[object]]::new()
    $rowCount = 0
    foreach ($row in $repository.data) {
        $rowCount++
        if ($row.Count -lt 8) { throw 'raw.pixls.us repository API returned a malformed row; refusing to fetch samples.' }
        $licenseHtml = [System.Net.WebUtility]::HtmlDecode([string]$row[5])
        $fileHtml = [System.Net.WebUtility]::HtmlDecode([string]$row[7])
        $licenseHref = [regex]::Match($licenseHtml, '<a\b[^>]*href=[''"]([^''"]+)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $licenseTitle = [regex]::Match($licenseHtml, '\btitle=[''"]([^''"]*)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $fileHref = [regex]::Match($fileHtml, '<a\b[^>]*href=[''"]([^''"]+)[''"]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $licenseHref.Success -or -not $licenseTitle.Success -or -not $fileHref.Success) {
            throw "raw.pixls.us repository API row lacked parseable file/license metadata: $layoutHint"
        }
        if (-not ($licenseHref.Groups[1].Value -match '^https?://')) {
            throw "raw.pixls.us repository API license column did not hold a URL: $layoutHint"
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
    if ($rowCount -gt 0 -and $records.Count -eq 0) {
        throw "raw.pixls.us repository API returned $rowCount rows but none had a raw.pixls.us file URL: $layoutHint"
    }
    return $records.ToArray()
}

function Assert-RawPixlsLicenses {
    param(
        [Parameter(Mandatory = $true)][object[]]$Samples,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Records
    )

    # Index once by (id, file URL): O(samples + records) instead of O(samples x records).
    $recordIndex = @{}
    foreach ($candidate in $Records) {
        $candidateUri = $null
        if (-not [uri]::TryCreate([string]$candidate.FileUrl, [UriKind]::Absolute, [ref]$candidateUri)) { continue }
        $recordIndex[('{0}|{1}' -f $candidate.Id, $candidateUri.AbsoluteUri.ToUpperInvariant())] = $candidate
    }

    foreach ($sample in $Samples) {
        $sampleUri = $null
        if (-not [uri]::TryCreate($sample.Url, [UriKind]::Absolute, [ref]$sampleUri) -or
            -not [string]::Equals($sampleUri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($sampleUri.Host, 'raw.pixls.us', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Sample '$($sample.FileName)' must use an HTTPS raw.pixls.us URL so it can be matched to the source license record."
        }
        $idMatch = [regex]::Match($sampleUri.AbsolutePath, '^/getfile\.php/(?<id>\d+)/nice/', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $idMatch.Success) { throw "Sample '$($sample.FileName)' has no verifiable raw.pixls.us file ID in its URL." }
        $record = $recordIndex[('{0}|{1}' -f $idMatch.Groups['id'].Value, $sampleUri.AbsoluteUri.ToUpperInvariant())]
        if ($null -eq $record) { throw "Sample '$($sample.FileName)' URL was not found in the raw.pixls.us source license records." }

        Assert-RawSampleLicense -LicenseUrl $sample.LicenseUrl -FileName $sample.FileName
        if (-not [string]::Equals($record.LicenseUrl, $sample.LicenseUrl, [StringComparison]::Ordinal) -or
            -not [string]::Equals($record.LicenseLabel, 'Creative Commons 0 - Public Domain', [StringComparison]::Ordinal)) {
            throw "Sample '$($sample.FileName)' is not recorded as CC0 1.0 in the raw.pixls.us source API."
        }
    }
}

function Invoke-RawPixlsLicenseCheck {
    # Runs the live-license check for a parsed manifest. RecordsProvider is injectable so the self-test can prove the
    # check runs (and fails closed) without a network call.
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$Samples,
        [Parameter(Mandatory = $true)][scriptblock]$RecordsProvider
    )

    # @() keeps a singleton (PS 5.1 unrolls it to a scalar with no .Count) array-shaped.
    $sampleList = @($Samples)
    if ($sampleList.Count -gt 0) {
        $repositoryRecords = @(& $RecordsProvider)
        Assert-RawPixlsLicenses -Samples $sampleList -Records $repositoryRecords
        return $true
    }
    return $false
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

        if ([string]::IsNullOrWhiteSpace($fileName) -or $fileName -match '^[.\s]+$' -or $fileName.EndsWith('.', [StringComparison]::Ordinal)) {
            throw "Invalid sample file name '$rawFileName' (empty, '.', '..' or trailing dot would escape or collide with the target directory)."
        }
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

    # Emit the rows plainly. Do NOT use a unary comma here: callers wrap the call in @(), and `,array` inside @() nests
    # the array one level deeper (each 'sample' then becomes an Object[]). @() at the call site is what keeps a one-row
    # manifest array-shaped on Windows PowerShell 5.1.
    return $samples.ToArray()
}

if ($SelfTest) {
    Test-FetchRawSamplesSelfTest
    exit 0
}

if (-not (Test-Path -LiteralPath $SamplesFile)) {
    throw "Samples file not found at: $SamplesFile"
}

$processed = 0
$samples = @(Read-RawSampleManifest -Path $SamplesFile)
[void](Invoke-RawPixlsLicenseCheck -Samples $samples -RecordsProvider { Get-RawPixlsRepositoryRecords })
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
