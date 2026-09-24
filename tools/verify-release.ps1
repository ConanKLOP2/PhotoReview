[CmdletBinding()]
param(
    [string]$ReleaseDirectory = '',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { $ReleaseDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PhotoReview.App\bin\Release\net10.0-windows\publish' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolved = [IO.Path]::GetFullPath($ReleaseDirectory)
$required = @(
    'PhotoReview.App.exe',
    'PhotoReview.App.dll',
    'PhotoReview.App.deps.json',
    'PhotoReview.App.runtimeconfig.json',
    'PhotoReview.Core.dll',
    'PhotoReview.Imaging.dll',
    'PhotoReview.Platform.Windows.dll',
    'PhotoReview.Benchmarking.dll',
    'PhotoReview.PerfAnalysis.dll',
    'PhotoReview.Imaging.TurboJpeg.dll',
    'turbojpeg.dll',
    # Translation catalogs (ADR 0006): English is also embedded, but shipped files let users read/edit them.
    'Languages\en.json',
    'Languages\vi.json')
$required += if ($SelfContained) { @('coreclr.dll', 'hostfxr.dll') } else { @() }
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $resolved $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Missing release file: $_" }
    exit 1
}

$hashFile = Join-Path $repoRoot 'native\turbojpeg.sha256'
$expectedNativeHash = (Get-Content -LiteralPath $hashFile -TotalCount 1).Trim().ToUpperInvariant()
$actualNativeHash = (Get-FileHash -LiteralPath (Join-Path $resolved 'turbojpeg.dll') -Algorithm SHA256).Hash
if ($actualNativeHash -ne $expectedNativeHash) {
    Write-Error "turbojpeg.dll SHA-256 mismatch: expected $expectedNativeHash, found $actualNativeHash"
    exit 1
}

$exe = Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.App.exe')
if ($exe.Length -le 0) { Write-Error 'Release executable is empty.'; exit 1 }
$projectFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PhotoReview.App\PhotoReview.App.csproj'
# The version is computed from git by Directory.Build.targets (no <Version> in the csproj). Ask MSBuild
# for the value the current commit produces, so a stale publish folder from another commit still fails.
$versionJson = & dotnet msbuild $projectFile -nologo -t:PhotoReviewComputeVersion -getProperty:FileVersion -getProperty:InformationalVersion -p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "Could not compute the expected version (dotnet msbuild exit $LASTEXITCODE)." }
$expected = ($versionJson -join "`n" | ConvertFrom-Json).Properties
$expectedFileVersion = [string]$expected.FileVersion
$expectedInformational = [string]$expected.InformationalVersion
# R2-F-18: Directory.Build.targets emits '<base>-nogit' when git or the v2.0.0 tag is unavailable (shallow or --no-tags
# clone, dubious ownership). Both sides would then carry no '+sha' and the commit comparison below would pass on '' == '';
# a release without a real commit identity must fail instead.
if ($expectedInformational -match '-nogit' -or $expectedInformational -notmatch '\+[0-9a-fA-F]+') {
    Write-Error "Expected version '$expectedInformational' has no git commit (nogit build): fetch full history and tags, then rebuild."
    exit 1
}
$expectedCommit = ([string]$expected.InformationalVersion -split '\+', 2)[1] -replace '\.dirty$', ''
$dllInfo = (Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.App.dll')).VersionInfo
$actualFileVersion = $dllInfo.FileVersion
$actualCommit = ([string]$dllInfo.ProductVersion -split '\+', 2)[1] -replace '\.dirty$', ''
if ([string]$dllInfo.ProductVersion -match '-nogit' -or [string]::IsNullOrEmpty($actualCommit)) {
    Write-Error "Release binary version '$($dllInfo.ProductVersion)' has no git commit (nogit build)."
    exit 1
}
if ($actualFileVersion -ne $expectedFileVersion) {
    Write-Error "Release version mismatch: expected $expectedFileVersion, found $actualFileVersion"
    exit 1
}
if ($actualCommit -ne $expectedCommit) {
    Write-Error "Release commit mismatch: expected $expectedCommit, found $actualCommit (stale publish folder?)"
    exit 1
}
Write-Output "PASS: release files present ($resolved)"
Write-Output "Version: $($dllInfo.ProductVersion) (file $actualFileVersion)"
Write-Output "EXE bytes: $($exe.Length)"
Write-Output "EXE SHA256: $((Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash)"
if ($SelfContained) { Write-Output 'PASS: self-contained runtime files present' }
