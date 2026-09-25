# Script to verify or fetch native binaries for PhotoReview

$ErrorActionPreference = "Stop"

$hashFile = Join-Path $PSScriptRoot "..\native\turbojpeg.sha256"
# The pin must be a real SHA-256: an empty file would throw an opaque null-method error, and a truncated or
# hand-edited value would silently turn the integrity check into "always re-download, always mismatch".
$expectedHash = ((Get-Content -LiteralPath $hashFile -TotalCount 1) | Out-String).Trim().ToUpperInvariant()
if ($expectedHash -notmatch '^[0-9A-F]{64}$') { throw "native/turbojpeg.sha256 must start with a 64-character hex SHA-256, found '$expectedHash'." }
$nativeDir = Join-Path $PSScriptRoot "..\native\x64"
$targetDll = Join-Path $nativeDir "turbojpeg.dll"

# -LiteralPath everywhere: a checkout under a folder such as C:\Photos\[new] treats [..] as a wildcard otherwise
# (Test-Path says "missing", Remove-Item -ErrorAction SilentlyContinue silently deletes nothing).
if (Test-Path -LiteralPath $targetDll) {
    $hash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash
    if ($hash -eq $expectedHash) {
        Write-Host "PASS: native/x64/turbojpeg.dll is present and SHA-256 matches." -ForegroundColor Green
        exit 0
    } else {
        Write-Warning "MISMATCH: native/x64/turbojpeg.dll hash mismatch ($hash vs $expectedHash). Re-fetching..."
    }
}

if (-not (Test-Path -LiteralPath $nativeDir)) {
    New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
}

$tempPkg = Join-Path ([System.IO.Path]::GetTempPath()) "libjpeg-turbo-native-windows.3.0.0.zip"
Write-Host "Downloading libjpeg-turbo 3.0.0 package..."
try {
    # https only, also across redirects: the pinned SHA-256 below is the integrity guarantee, this stops a downgrade to http.
    curl.exe -sSL --fail --retry 3 --proto "=https" --proto-redir "=https" "https://www.nuget.org/api/v2/package/libjpeg-turbo-native-windows/3.0.0" -o $tempPkg
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit code $LASTEXITCODE)." }
    tar.exe -xf $tempPkg -C $nativeDir lib/net6.0/Turbojpeg.dll
    if ($LASTEXITCODE -ne 0) { throw "Extracting Turbojpeg.dll failed (tar exit code $LASTEXITCODE)." }
    Move-Item -LiteralPath (Join-Path $nativeDir "lib\net6.0\Turbojpeg.dll") -Destination $targetDll -Force
    Remove-Item -LiteralPath (Join-Path $nativeDir "lib") -Recurse -Force -ErrorAction SilentlyContinue

    $hash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash
    if ($hash -ne $expectedHash) {
        Remove-Item -LiteralPath $targetDll -Force -ErrorAction SilentlyContinue
        throw "Downloaded DLL hash mismatch: $hash vs $expectedHash"
    }
    Write-Host "PASS: Fetched and verified native/x64/turbojpeg.dll successfully." -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $tempPkg) {
        Remove-Item -LiteralPath $tempPkg -Force -ErrorAction SilentlyContinue
    }
}
