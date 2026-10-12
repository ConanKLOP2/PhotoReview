[CmdletBinding()]
param(
    # Where both executables and every library they need are published (one folder = one zip).
    [string]$OutputDirectory = '',
    # CI restores the whole solution once; pass this so the two publishes do not restore again.
    [switch]$NoRestore
)

# NO-WPF WP-30: the release package holds TWO executables in ONE folder (NE-2 = a):
#   PhotoReview.App.exe  - the WPF app (unchanged behaviour, default for existing users)
#   PhotoReview.exe      - the Win32 + Direct2D shell (parallel product, own single-instance prefix)
# Both are framework-dependent ReadyToRun (PublishReadyToRun is set in each csproj), not self-contained. The shared libraries
# (Core, Imaging.*, App.Shared, Platform.Windows, ...) are byte-identical in both publishes (R2R is deterministic), so the
# second publish into the same folder only adds the shell files and overwrites the shared ones with the same bytes.
# One script for ci.yml and release.yml so the two workflows cannot drift on what is shipped.
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'src\PhotoReview.App\bin\Release\net10.0-windows\publish'
}
$out = [IO.Path]::GetFullPath($OutputDirectory)

$projects = @(
    'src\PhotoReview.App\PhotoReview.App.csproj',
    'src\PhotoReview.Shell.Win32\PhotoReview.Shell.Win32.csproj')

foreach ($project in $projects) {
    $projectPath = Join-Path $repoRoot $project
    $publishArgs = @('publish', $projectPath, '-c', 'Release', '--self-contained', 'false', '-o', $out)
    if ($NoRestore) { $publishArgs += '--no-restore' }
    Write-Output "dotnet $($publishArgs -join ' ')"
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)" }
}

foreach ($exe in 'PhotoReview.App.exe', 'PhotoReview.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $out $exe) -PathType Leaf)) { throw "$exe is missing from $out after publish." }
}
$bytes = (Get-ChildItem -LiteralPath $out -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Output ('PASS: both executables published to {0} ({1:N1} MB)' -f $out, ($bytes / 1MB))
