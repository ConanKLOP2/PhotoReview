[CmdletBinding()]
param(
    [string]$ReleaseDirectory = ''
)

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { $ReleaseDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'outputs\release\PhotoReview-framework-dependent' }
$resolved = [IO.Path]::GetFullPath($ReleaseDirectory)
$required = @('PhotoReview.App.exe', 'PhotoReview.App.dll', 'PhotoReview.App.deps.json', 'PhotoReview.App.runtimeconfig.json')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $resolved $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Missing release file: $_" }
    exit 1
}

$exe = Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.App.exe')
if ($exe.Length -le 0) { Write-Error 'Release executable is empty.'; exit 1 }
Write-Output "PASS: release files present ($resolved)"
Write-Output "EXE bytes: $($exe.Length)"
Write-Output "EXE SHA256: $((Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash)"
