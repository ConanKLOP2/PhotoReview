[CmdletBinding()]
param(
    # Where both executables and every library they need are published (one folder = one zip).
    [string]$OutputDirectory = '',
    # CI restores the whole solution once; pass this so the two publishes do not restore again.
    [switch]$NoRestore,
    # WP-36: ALSO publish the OPTIONAL Native AOT variant of PhotoReview.exe (no WPF bridge; since WP-35 GĐ5 (#500) every
    # secondary window is a Win32 dialog, so the AOT build opens Settings/Diagnostics/Recovery too) into its own folder. Needs the MSVC C++ build tools; without them the AOT step is skipped with a
    # warning and the normal publish is untouched. The default release (framework-dependent R2R hybrid) never changes.
    [switch]$Aot,
    # Same as -Aot, but missing MSVC or a failed AOT publish FAILS the script (the CI job whose only purpose is the AOT artifact).
    [switch]$RequireAot,
    # Where the AOT variant goes. Default: the sibling folder '<OutputDirectory>-aot' (kept out of the normal release zip).
    [string]$AotOutputDirectory = '',
    # Test hook: path of vswhere.exe to use instead of PATH / the Visual Studio Installer folder.
    [string]$VsWherePath = '',
    # Skip the two normal publishes (CI job that only builds the AOT artifact, and tests). Needs -Aot or -RequireAot.
    [switch]$AotOnly
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
if ($AotOnly -and -not ($Aot -or $RequireAot)) { throw '-AotOnly needs -Aot or -RequireAot.' }

# NO-WPF WP-36: PhotoReview.exe as Native AOT (-p:ShellAot=true in Shell.Win32.csproj: PublishAot + EventSourceSupport, no WPF
# bridge). It is an OPTIONAL second artifact, never part of the normal release folder: the default release stays the framework-dependent
# R2R build, never AOT.
function Find-VsWhere {
    if ($VsWherePath) {
        if (Test-Path -LiteralPath $VsWherePath -PathType Leaf) { return $VsWherePath }
        return $null
    }
    $onPath = Get-Command 'vswhere.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $installer -PathType Leaf) { return $installer }
    return $null
}

function Publish-AotVariant {
    $vswhere = Find-VsWhere
    $msvc = $null
    if ($vswhere) {
        $msvc = & $vswhere -latest -products '*' -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' -property installationPath 2>$null |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($msvc)) {
        $why = 'Native AOT needs the Visual Studio "Desktop development with C++" (MSVC x64/x86 build tools) workload and vswhere.exe; neither was found.'
        if ($RequireAot) { throw "AOT publish required but not possible: $why" }
        Write-Warning "Skipping the optional AOT variant: $why The normal release is unaffected."
        return
    }
    # ILCompiler runs `vswhere.exe` by name: its folder must be on PATH.
    $vswhereDir = Split-Path -Parent $vswhere
    if (-not (($env:PATH -split ';') -contains $vswhereDir)) { $env:PATH = $env:PATH + ';' + $vswhereDir }

    $aotOut = if ([string]::IsNullOrWhiteSpace($AotOutputDirectory)) { $out.TrimEnd('\', '/') + '-aot' } else { [IO.Path]::GetFullPath($AotOutputDirectory) }
    $shellProject = Join-Path $repoRoot 'src\PhotoReview.Shell.Win32\PhotoReview.Shell.Win32.csproj'
    # No --no-restore here: PublishAot needs the RID-specific restore (ILCompiler + runtime packs) the solution restore did not do.
    $aotArgs = @('publish', $shellProject, '-c', 'Release', '-r', 'win-x64', '-p:ShellAot=true', '/nodeReuse:false', '-o', $aotOut)
    Write-Output "dotnet $($aotArgs -join ' ')"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & dotnet @aotArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (AOT) failed (exit $LASTEXITCODE)" }
    }
    finally {
        # The AOT restore drops the WPF bridge from project.assets.json; put the default graph back so a later normal
        # build/publish/test in this checkout is not poisoned.
        & dotnet restore $shellProject -v q /nodeReuse:false | Out-Null
    }
    # The native symbols are ~50 MB: not part of the artifact.
    foreach ($symbols in @(Get-ChildItem -LiteralPath $aotOut -Filter '*.pdb' -File -Recurse)) { [IO.File]::Delete($symbols.FullName) }
    if (-not (Test-Path -LiteralPath (Join-Path $aotOut 'PhotoReview.exe') -PathType Leaf)) { throw "PhotoReview.exe is missing from $aotOut after the AOT publish." }
    $aotBytes = (Get-ChildItem -LiteralPath $aotOut -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Output ('PASS: AOT variant published to {0} ({1:N1} MB, {2:N0} s)' -f $aotOut, ($aotBytes / 1MB), $watch.Elapsed.TotalSeconds)
}

if (-not $AotOnly) {
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
}

# Last on purpose: its restore is RID-specific and must not precede the normal --no-restore publishes above.
if ($Aot -or $RequireAot) { Publish-AotVariant }
