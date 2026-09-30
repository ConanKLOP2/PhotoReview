# Fetches and verifies the pinned LibRaw 0.22.2 Windows x64 runtime (DLL, corresponding source package, notices).
# PowerShell 5.1 compatible, idempotent, fail-closed:
#   * default mode: if every pin already matches, do nothing (offline friendly); otherwise download, verify the PACKAGE
#     SHA-256 before extracting anything, expand with an entry-path guard, verify the DLL, then install atomically.
#   * -Verify: fast read-only check (no network, no writes); exit 1 when any pin is missing/mismatched.
#   * -PackagePath: use a local package instead of downloading (offline installs and tests); the same checks apply.
# A named mutex serializes concurrent runs (MSBuild builds AnyCPU and x64 of the LibRaw project in parallel).
[CmdletBinding()]
param(
    [switch]$Verify,
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hashPath = Join-Path $repoRoot 'native\libraw.sha256'
$expectedHash = ((Get-Content -LiteralPath $hashPath -TotalCount 1) | Out-String).Trim().ToUpperInvariant()
$packageHashPath = Join-Path $repoRoot 'native\libraw.package.sha256'
$expectedPackageHash = ((Get-Content -LiteralPath $packageHashPath -TotalCount 1) | Out-String).Trim().ToUpperInvariant()
if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
    throw "native/libraw.sha256 must start with a 64-character hex SHA-256, found '$expectedHash'."
}
if ($expectedPackageHash -notmatch '^[0-9A-F]{64}$') {
    throw "native/libraw.package.sha256 must start with a 64-character hex SHA-256, found '$expectedPackageHash'."
}

$nativeDir = Join-Path $repoRoot 'native\x64'
$licenseDir = Join-Path $repoRoot 'native\libraw'
$targetDll = Join-Path $nativeDir 'libraw.dll'
$licenseLgpl = Join-Path $licenseDir 'LICENSE.LGPL'
$licenseCddl = Join-Path $licenseDir 'LICENSE.CDDL'
$sourcePackage = Join-Path $licenseDir 'LibRaw-0.22.2-Win64.zip'
$notice = Join-Path $licenseDir 'NOTICE.txt'

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Test-LibRawPins {
    if (-not (Test-Path -LiteralPath $targetDll -PathType Leaf)) { return $false }
    foreach ($file in @($licenseLgpl, $licenseCddl, $notice, $sourcePackage)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
    }
    return ((Get-Sha256 $targetDll) -eq $expectedHash) -and ((Get-Sha256 $sourcePackage) -eq $expectedPackageHash)
}

function Expand-LibRawPackage {
    # Manual extraction so every entry path is checked against the destination BEFORE it is written (zip-slip guard).
    param([string]$Package, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        foreach ($entry in $zip.Entries) {
            $full = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Package entry '$($entry.FullName)' would extract outside the temp directory; refusing to expand."
            }
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('\', [StringComparison]::Ordinal)) {
                [void][IO.Directory]::CreateDirectory($full)
                continue
            }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $full, $false)
        }
    }
    finally { $zip.Dispose() }
}

function Install-VerifiedFile {
    # Copy beside the target, verify the copy (when a pin exists), then move into place: no unverified file is ever left at the target.
    param([string]$Source, [string]$Destination, [string]$ExpectedSha = '')
    $staging = "$Destination.tmp.$([guid]::NewGuid().ToString('N'))"
    try {
        Copy-Item -LiteralPath $Source -Destination $staging -Force
        if ($ExpectedSha -and (Get-Sha256 $staging) -ne $ExpectedSha) {
            throw "Staged copy of $(Split-Path -Leaf $Destination) does not match its SHA-256 pin; not installing."
        }
        Move-Item -LiteralPath $staging -Destination $Destination -Force
    }
    finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue } }
}

# Serialize concurrent runs for this checkout (session-local named mutex; path hashed to a valid name).
$pathKey = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant()))).Replace('-', '').Substring(0, 16)
$mutex = New-Object System.Threading.Mutex($false, "Local\PhotoReview-fetch-libraw-$pathKey")
$hasMutex = $false
try {
    try { $hasMutex = $mutex.WaitOne([TimeSpan]::FromMinutes(10)) }
    catch [System.Threading.AbandonedMutexException] { $hasMutex = $true }
    if (-not $hasMutex) { throw 'Timed out waiting for another fetch-libraw.ps1 run to finish.' }

    if (Test-LibRawPins) {
        Write-Host 'PASS: LibRaw DLL, corresponding source package, and notices match their pins.' -ForegroundColor Green
        exit 0
    }
    if ($Verify) {
        Write-Error 'LibRaw binary, source package, or notices are missing/mismatched. Run tools/fetch-libraw.ps1 to install the pinned 0.22.2 release.'
        exit 1
    }
    Write-Warning 'LibRaw binary or license files are missing/mismatched; re-fetching pinned 0.22.2 release.'

    if (-not (Test-Path -LiteralPath $nativeDir)) { New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $licenseDir)) { New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null }

    $tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "photoreview-libraw-$([guid]::NewGuid().ToString('N'))"))
    $tempPackage = Join-Path ([IO.Path]::GetTempPath()) "LibRaw-0.22.2-Win64.$([guid]::NewGuid().ToString('N')).zip"
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        if ($PackagePath) {
            Copy-Item -LiteralPath $PackagePath -Destination $tempPackage -Force
        }
        else {
            Write-Host 'Downloading official LibRaw 0.22.2 Windows x64 package...'
            curl.exe -sSL --fail --retry 3 --proto '=https' --proto-redir '=https' `
                'https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip' -o $tempPackage
            if ($LASTEXITCODE -ne 0) { throw "LibRaw download failed (curl exit code $LASTEXITCODE)." }
        }

        # Hash the untrusted package FIRST: nothing is extracted until it matches the pin.
        $downloadedPackageHash = Get-Sha256 $tempPackage
        if ($downloadedPackageHash -ne $expectedPackageHash) {
            throw "Official source package SHA-256 mismatch: $downloadedPackageHash vs $expectedPackageHash."
        }

        Expand-LibRawPackage -Package $tempPackage -Destination $tempRoot
        $packageRoot = Join-Path $tempRoot 'LibRaw-0.22.2'
        $packageDll = Join-Path $packageRoot 'bin\libraw.dll'
        $packageLgpl = Join-Path $packageRoot 'LICENSE.LGPL'
        $packageCddl = Join-Path $packageRoot 'LICENSE.CDDL'
        foreach ($required in @($packageDll, $packageLgpl, $packageCddl)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Official LibRaw package is missing $required." }
        }

        $downloadedHash = Get-Sha256 $packageDll
        if ($downloadedHash -ne $expectedHash) {
            throw "Downloaded libraw.dll SHA-256 mismatch: $downloadedHash vs $expectedHash."
        }

        Install-VerifiedFile -Source $packageDll -Destination $targetDll -ExpectedSha $expectedHash
        Install-VerifiedFile -Source $packageLgpl -Destination $licenseLgpl
        Install-VerifiedFile -Source $packageCddl -Destination $licenseCddl
        Install-VerifiedFile -Source $tempPackage -Destination $sourcePackage -ExpectedSha $expectedPackageHash
        Write-Host 'PASS: Fetched official LibRaw 0.22.2 DLL and complete source package; both hashes match.' -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $tempPackage) { Remove-Item -LiteralPath $tempPackage -Force }
        $tempPath = [IO.Path]::GetFullPath($tempRoot)
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($tempPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $tempPath) -like 'photoreview-libraw-*' -and
            (Test-Path -LiteralPath $tempPath -PathType Container)) {
            Remove-Item -LiteralPath $tempPath -Recurse -Force
        }
    }
}
finally {
    if ($hasMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
