# Script to verify or fetch native binaries for PhotoReview

$ErrorActionPreference = "Stop"

$hashFile = Join-Path $PSScriptRoot "..\native\turbojpeg.sha256"
$expectedHash = (Get-Content -LiteralPath $hashFile -TotalCount 1).Trim().ToUpperInvariant()
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
    tar.exe -xf $tempPkg -C $nativeDir lib/net6.0/Turbojpeg.dll
    Move-Item (Join-Path $nativeDir "lib\net6.0\Turbojpeg.dll") $targetDll -Force
    Remove-Item (Join-Path $nativeDir "lib") -Recurse -Force -ErrorAction SilentlyContinue

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
