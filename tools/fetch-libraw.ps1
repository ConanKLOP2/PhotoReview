[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hashPath = Join-Path $repoRoot 'native\libraw.sha256'
$expectedHash = ((Get-Content -LiteralPath $hashPath -TotalCount 1) | Out-String).Trim().ToUpperInvariant()
if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
    throw "native/libraw.sha256 must start with a 64-character hex SHA-256, found '$expectedHash'."
}

$nativeDir = Join-Path $repoRoot 'native\x64'
$licenseDir = Join-Path $repoRoot 'native\libraw'
$targetDll = Join-Path $nativeDir 'libraw.dll'
$licenseLgpl = Join-Path $licenseDir 'LICENSE.LGPL'
$licenseCddl = Join-Path $licenseDir 'LICENSE.CDDL'

if (Test-Path -LiteralPath $targetDll) {
    $existingHash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($existingHash -eq $expectedHash -and
        (Test-Path -LiteralPath $licenseLgpl) -and (Test-Path -LiteralPath $licenseCddl)) {
        Write-Host 'PASS: native/x64/libraw.dll is present and SHA-256 matches.' -ForegroundColor Green
        exit 0
    }
    Write-Warning 'LibRaw binary or license files are missing/mismatched; re-fetching pinned 0.22.2 release.'
}

if (-not (Test-Path -LiteralPath $nativeDir)) { New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $licenseDir)) { New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null }

$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "photoreview-libraw-$([guid]::NewGuid().ToString('N'))"))
$tempPackage = Join-Path ([IO.Path]::GetTempPath()) "LibRaw-0.22.2-Win64.$([guid]::NewGuid().ToString('N')).zip"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Write-Host 'Downloading official LibRaw 0.22.2 Windows x64 package...'
    curl.exe -sSL --fail --retry 3 --proto '=https' --proto-redir '=https' `
        'https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip' -o $tempPackage
    if ($LASTEXITCODE -ne 0) { throw "LibRaw download failed (curl exit code $LASTEXITCODE)." }

    Expand-Archive -LiteralPath $tempPackage -DestinationPath $tempRoot
    $packageRoot = Join-Path $tempRoot 'LibRaw-0.22.2'
    $packageDll = Join-Path $packageRoot 'bin\libraw.dll'
    $packageLgpl = Join-Path $packageRoot 'LICENSE.LGPL'
    $packageCddl = Join-Path $packageRoot 'LICENSE.CDDL'
    foreach ($required in @($packageDll, $packageLgpl, $packageCddl)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Official LibRaw package is missing $required." }
    }

    $downloadedHash = (Get-FileHash -LiteralPath $packageDll -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($downloadedHash -ne $expectedHash) {
        throw "Downloaded libraw.dll SHA-256 mismatch: $downloadedHash vs $expectedHash."
    }

    Copy-Item -LiteralPath $packageDll -Destination $targetDll -Force
    Copy-Item -LiteralPath $packageLgpl -Destination $licenseLgpl -Force
    Copy-Item -LiteralPath $packageCddl -Destination $licenseCddl -Force

    $installedHash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($installedHash -ne $expectedHash) { throw "Installed libraw.dll SHA-256 mismatch: $installedHash vs $expectedHash." }
    Write-Host 'PASS: Fetched official LibRaw 0.22.2 DLL and verified its SHA-256.' -ForegroundColor Green
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
