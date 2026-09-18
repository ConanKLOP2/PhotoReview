# Script to verify or fetch native binaries for PhotoReview

$ErrorActionPreference = "Stop"

$expectedHash = "E9BDEC69FA2008EAF557CB68BD483E62A350B740A1588497D14AC157A0DD583D"
$nativeDir = Join-Path $PSScriptRoot "..\native\x64"
$targetDll = Join-Path $nativeDir "turbojpeg.dll"

if (Test-Path $targetDll) {
    $hash = (Get-FileHash $targetDll -Algorithm SHA256).Hash
    if ($hash -eq $expectedHash) {
        Write-Host "PASS: native/x64/turbojpeg.dll is present and SHA-256 matches." -ForegroundColor Green
        exit 0
    } else {
        Write-Warning "MISMATCH: native/x64/turbojpeg.dll hash mismatch ($hash vs $expectedHash). Re-fetching..."
    }
}

if (-not (Test-Path $nativeDir)) {
    New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
}

$tempPkg = Join-Path ([System.IO.Path]::GetTempPath()) "libjpeg-turbo-native-windows.3.0.0.zip"
Write-Host "Downloading libjpeg-turbo 3.0.0 package..."
curl.exe -sL "https://www.nuget.org/api/v2/package/libjpeg-turbo-native-windows/3.0.0" -o $tempPkg

try {
    tar.exe -xf $tempPkg -C $nativeDir --strip-components 3 lib/net6.0/Turbojpeg.dll
    Rename-Item (Join-Path $nativeDir "Turbojpeg.dll") "turbojpeg.dll" -Force -ErrorAction SilentlyContinue

    $hash = (Get-FileHash $targetDll -Algorithm SHA256).Hash
    if ($hash -ne $expectedHash) {
        throw "Downloaded DLL hash mismatch: $hash vs $expectedHash"
    }
    Write-Host "PASS: Fetched and verified native/x64/turbojpeg.dll successfully." -ForegroundColor Green
} finally {
    if (Test-Path $tempPkg) {
        Remove-Item $tempPkg -Force -ErrorAction SilentlyContinue
    }
}
