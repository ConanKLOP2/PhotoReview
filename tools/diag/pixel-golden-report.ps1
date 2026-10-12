<#
.SYNOPSIS
    Measures every G-PIX case (WPF golden frame vs Win32 shell frame on the WARP offscreen surface) and writes the numbers plus
    one diff image per case (WP-28).

.DESCRIPTION
    NO-WPF-EXEC-PLAN section 7.3 (2). The golden frames are tests/Fixtures/golden/pixel/*.png, recorded from the real WPF
    MainWindow by tools/diag/record-golden.ps1 (PixelGoldenRecordTests). The shell frames are drawn by the shell's own
    viewport layout, composer, D2D renderer and scroll bars (tests/PhotoReview.Shell.Tests/Golden). For each case this writes

        <OutDir>\report.txt            one line per case: picture-region PSNR/SSIM/MAE/max delta, whole-frame PSNR/SSIM,
                                       scroll bar strip PSNR, best-fit shift, edge error; "MISSES A BAR" when it fails
        <OutDir>\<case>-diff.png       expected (WPF) | actual (shell) | difference x4

    The gating test (PixelGoldenTests, Category=Integration) also writes the diff image of a failing case to the same place
    (PHOTOREVIEW_PIXEL_REPORT_DIR or %TEMP%\PhotoReview-pixel-golden) and names it in the failure message. Nothing here
    rewrites a golden file.

.PARAMETER OutDir
    Report folder (default: <repo>\work\pixel-golden-report; git-ignored).

.PARAMETER NoBuild
    Skip building Shell.Tests.

.PARAMETER IncludeHud
    Also run the HUD on/off cases (Category=Native: DirectWrite with the machine's fonts) and print their panel boxes.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/diag/pixel-golden-report.ps1 -IncludeHud
#>
[CmdletBinding()]
param([string]$OutDir = '', [switch]$NoBuild, [switch]$IncludeHud)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'work\pixel-golden-report' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$project = Join-Path $repo 'tests\PhotoReview.Shell.Tests\PhotoReview.Shell.Tests.csproj'

if (-not $NoBuild) {
    & dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

$guard = @('--blame-hang', '--blame-hang-timeout', '120s', '--blame-hang-dump-type', 'none')
$env:PHOTOREVIEW_PIXEL_REPORT_DIR = $OutDir
try {
    $filter = 'FullyQualifiedName~PixelGoldenTests.ReportAllCases'
    if ($IncludeHud) { $filter += '|FullyQualifiedName~PixelGoldenHudTests' }
    & dotnet test $project -c Release --no-build --filter $filter --logger 'console;verbosity=detailed' @guard
    if ($LASTEXITCODE -ne 0) { Write-Warning 'a case missed a bar (see report.txt and the diff images)' }
}
finally {
    Remove-Item Env:\PHOTOREVIEW_PIXEL_REPORT_DIR -ErrorAction SilentlyContinue
}

Write-Host "`nReport: $(Join-Path $OutDir 'report.txt')"
Get-Content (Join-Path $OutDir 'report.txt')
