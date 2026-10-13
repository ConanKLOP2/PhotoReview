[CmdletBinding()]
param(
    [string]$ReleaseDirectory = '',
    [switch]$SelfContained,
    # Also start PhotoReview.exe --smoke (creates + shows the Win32 window, then exits 0). Needs a desktop session; off by
    # default so the script stays usable on fake folders in tests. CI passes it.
    [switch]$Smoke,
    # WP-36: verify the OPTIONAL Native AOT variant of PhotoReview.exe (tools/publish-release.ps1 -Aot) in this folder too.
    [string]$AotDirectory = '',
    # Verify ONLY the AOT folder (the CI job that publishes just the AOT artifact has no normal release folder).
    [switch]$AotOnly,
    # Plausible size window of the AOT PhotoReview.exe (13.3 MB measured) and of the whole folder without *.pdb (~20 MB measured).
    [long]$AotMinExeBytes = 5MB,
    [long]$AotMaxExeBytes = 40MB,
    [long]$AotMaxFolderBytes = 40MB,
    # Test hook only: skip the "version carries a git commit" check (a runnable stand-in exe such as whoami.exe has none).
    [switch]$AotSkipVersionCheck,
    # Test hook only: skip the "PhotoReview.exe carries an icon resource" check (fake folders use stand-in exes without one).
    [switch]$SkipIconCheck
)

$ErrorActionPreference = 'Stop'

# Shell menus/icon fix: PhotoReview.exe must carry the app icon (RT_GROUP_ICON resource; ApplicationIcon in the Shell.Win32 csproj).
# Pure Win32 through Add-Type (kernel32 LoadLibraryEx data-file + EnumResourceNames), no external tool. Returns the number of
# group-icon resources found (0 = the exe has no icon, e.g. the shell window and the taskbar would show the generic one).
function Get-ExeGroupIconCount {
    param([string]$Path)
    if (-not ('PhotoReviewIconProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PhotoReviewIconProbe {
    delegate bool EnumProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string file, IntPtr handle, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumProc proc, IntPtr param);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    public static int GroupIconCount(string file) {
        IntPtr module = LoadLibraryEx(file, IntPtr.Zero, 0x2); // LOAD_LIBRARY_AS_DATAFILE
        if (module == IntPtr.Zero) return -1;
        int count = 0;
        EnumResourceNames(module, (IntPtr)14, (m, t, n, p) => { count++; return true; }, IntPtr.Zero); // RT_GROUP_ICON
        FreeLibrary(module);
        return count;
    }
}
'@
    }
    return [PhotoReviewIconProbe]::GroupIconCount([IO.Path]::GetFullPath($Path))
}

# WP-36: the AOT folder holds one native PhotoReview.exe and the native/data files, and NO .NET runtime and NO WPF.
function Test-AotRelease {
    param([string]$Dir, [string]$RepoRoot, [bool]$RunSmoke, [string]$ExpectedCommit)
    $aot = [IO.Path]::GetFullPath($Dir)
    if (-not (Test-Path -LiteralPath $aot -PathType Container)) { Write-Error "AOT folder not found: $aot"; exit 1 }
    $aotRequired = @('PhotoReview.exe', 'turbojpeg.dll', 'libraw.dll', 'LibRaw-LICENSE.LGPL', 'LibRaw-LICENSE.CDDL', 'LibRaw-SOURCE.zip',
        'LibRaw-NOTICE.txt', 'THIRD-PARTY-NOTICES.md', 'Languages\en.json', 'Languages\vi.json')
    $aotMissing = @($aotRequired | Where-Object { -not (Test-Path -LiteralPath (Join-Path $aot $_) -PathType Leaf) })
    if ($aotMissing.Count -gt 0) { Write-Error ('Missing AOT file(s): ' + ($aotMissing -join ', ')); exit 1 }
    # A managed or WPF file here means the variant was not really ahead-of-time compiled / still drags the WPF bridge.
    $forbidden = @('PhotoReview.dll', 'PhotoReview.runtimeconfig.json', 'PhotoReview.deps.json', 'PhotoReview.Shell.WpfBridge.dll',
        'PhotoReview.App.WpfWindows.dll', 'PresentationFramework.dll', 'WindowsBase.dll', 'coreclr.dll', 'hostfxr.dll')
    $present = @($forbidden | Where-Object { Test-Path -LiteralPath (Join-Path $aot $_) -PathType Leaf })
    if ($present.Count -gt 0) { Write-Error ('AOT folder must not contain: ' + ($present -join ', ')); exit 1 }
    $aotExe = Get-Item -LiteralPath (Join-Path $aot 'PhotoReview.exe')
    if ($aotExe.Length -lt $AotMinExeBytes -or $aotExe.Length -gt $AotMaxExeBytes) {
        Write-Error "AOT PhotoReview.exe is $($aotExe.Length) bytes, outside the plausible range $AotMinExeBytes..$AotMaxExeBytes."
        exit 1
    }
    $folderBytes = (Get-ChildItem -LiteralPath $aot -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | Measure-Object -Property Length -Sum).Sum
    if ($folderBytes -gt $AotMaxFolderBytes) {
        Write-Error "AOT folder is $folderBytes bytes without *.pdb, above the limit $AotMaxFolderBytes."
        exit 1
    }
    foreach ($native in @(@('turbojpeg.dll', 'turbojpeg.sha256'), @('libraw.dll', 'libraw.sha256'), @('LibRaw-SOURCE.zip', 'libraw.package.sha256'))) {
        $expectedHash = (Get-Content -LiteralPath (Join-Path $RepoRoot ('native\' + $native[1])) -TotalCount 1).Trim().ToUpperInvariant()
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $aot $native[0]) -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) { Write-Error "AOT $($native[0]) SHA-256 mismatch: expected $expectedHash, found $actualHash"; exit 1 }
    }
    # Test stand-ins have no icon (-SkipIconCheck); real publishes must carry one.
    if (-not $SkipIconCheck -and (Get-ExeGroupIconCount -Path $aotExe.FullName) -lt 1) {
        Write-Error 'AOT PhotoReview.exe has no icon resource (RT_GROUP_ICON): ApplicationIcon was not applied.'
        exit 1
    }
    $aotVersion = [string]$aotExe.VersionInfo.ProductVersion
    $aotCommit = ($aotVersion -split '\+', 2)[1]
    if (-not $AotSkipVersionCheck -and ($aotVersion -match '-nogit' -or [string]::IsNullOrEmpty($aotCommit))) {
        Write-Error "AOT PhotoReview.exe version '$aotVersion' has no git commit (nogit build)."
        exit 1
    }
    if ($ExpectedCommit -and (($aotCommit -replace '\.dirty$', '') -ne $ExpectedCommit)) {
        Write-Error "AOT commit mismatch: expected $ExpectedCommit, found $aotCommit (stale AOT folder?)"
        exit 1
    }
    if ($RunSmoke) {
        # The AOT exe must not need an installed .NET: strip every DOTNET_* variable and every PATH entry that looks like a dotnet
        # install, then run --smoke (create + show the window, exit 0) in OUR OWN process only.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $aotExe.FullName
        $psi.Arguments = '--smoke'
        $psi.WorkingDirectory = $aot
        $psi.UseShellExecute = $false
        foreach ($name in @($psi.EnvironmentVariables.Keys | Where-Object { $_ -like 'DOTNET_*' })) { $psi.EnvironmentVariables.Remove($name) }
        $psi.EnvironmentVariables['PATH'] = (($env:PATH -split ';') | Where-Object { $_ -and $_ -notmatch 'dotnet' }) -join ';'
        $aotProcess = [System.Diagnostics.Process]::Start($psi)
        if (-not $aotProcess.WaitForExit(60000)) {
            try { $aotProcess.Kill() } catch { }
            Write-Error 'AOT PhotoReview.exe --smoke did not exit within 60 s.'
            exit 1
        }
        if ($aotProcess.ExitCode -ne 0) { Write-Error "AOT PhotoReview.exe --smoke exited with code $($aotProcess.ExitCode)."; exit 1 }
        Write-Output 'PASS: AOT PhotoReview.exe --smoke exited 0 without a .NET runtime'
    }
    Write-Output ('PASS: AOT folder ok ({0}); PhotoReview.exe {1:N1} MB, folder {2:N1} MB without *.pdb, version {3}' -f $aot, ($aotExe.Length / 1MB), ($folderBytes / 1MB), $aotVersion)
}

if ($AotOnly) {
    if ([string]::IsNullOrWhiteSpace($AotDirectory)) { Write-Error '-AotOnly needs -AotDirectory.'; exit 1 }
    Test-AotRelease -Dir $AotDirectory -RepoRoot (Split-Path -Parent $PSScriptRoot) -RunSmoke ([bool]$Smoke) -ExpectedCommit ''
    exit 0
}

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
    'PhotoReview.Imaging.Wpf.dll',
    'PhotoReview.App.Shared.dll',
    'PhotoReview.App.WpfWindows.dll',
    'PhotoReview.Platform.Windows.dll',
    'PhotoReview.Benchmarking.dll',
    'PhotoReview.PerfAnalysis.dll',
    'PhotoReview.Imaging.TurboJpeg.dll',
    'PhotoReview.Imaging.LibRaw.dll',
    # NO-WPF WP-30: the Win32 + Direct2D shell (PhotoReview.exe) ships in the same folder. WP-35 GĐ5: it no longer loads a WPF
    # bridge (every secondary window is a native Win32 dialog), so PhotoReview.Shell.WpfBridge.dll is NOT part of the package.
    'PhotoReview.exe',
    'PhotoReview.dll',
    'PhotoReview.deps.json',
    'PhotoReview.runtimeconfig.json',
    'PhotoReview.Shell.Interop.dll',
    'PhotoReview.Shell.Rendering.dll',
    'turbojpeg.dll',
    'libraw.dll',
    'LibRaw-LICENSE.LGPL',
    'LibRaw-LICENSE.CDDL',
    'LibRaw-SOURCE.zip',
    'LibRaw-NOTICE.txt',
    # libjpeg-turbo's BSD/IJG/zlib notice and the other third-party notices ship only in this file.
    'THIRD-PARTY-NOTICES.md',
    # Translation catalogs (ADR 0006): English is also embedded, but shipped files let users read/edit them.
    'Languages\en.json',
    'Languages\vi.json')
$required += if ($SelfContained) { @('coreclr.dll', 'hostfxr.dll') } else { @() }
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $resolved $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    # One message: Write-Error is terminating under $ErrorActionPreference='Stop', so a per-file loop would list only the first.
    Write-Error ('Missing release file(s): ' + ($missing -join ', '))
    exit 1
}

$hashFile = Join-Path $repoRoot 'native\turbojpeg.sha256'
$expectedNativeHash = (Get-Content -LiteralPath $hashFile -TotalCount 1).Trim().ToUpperInvariant()
$actualNativeHash = (Get-FileHash -LiteralPath (Join-Path $resolved 'turbojpeg.dll') -Algorithm SHA256).Hash
if ($actualNativeHash -ne $expectedNativeHash) {
    Write-Error "turbojpeg.dll SHA-256 mismatch: expected $expectedNativeHash, found $actualNativeHash"
    exit 1
}

$libRawHashFile = Join-Path $repoRoot 'native\libraw.sha256'
$expectedLibRawHash = (Get-Content -LiteralPath $libRawHashFile -TotalCount 1).Trim().ToUpperInvariant()
$actualLibRawHash = (Get-FileHash -LiteralPath (Join-Path $resolved 'libraw.dll') -Algorithm SHA256).Hash
if ($actualLibRawHash -ne $expectedLibRawHash) {
    Write-Error "libraw.dll SHA-256 mismatch: expected $expectedLibRawHash, found $actualLibRawHash"
    exit 1
}
$libRawPackageHashFile = Join-Path $repoRoot 'native\libraw.package.sha256'
$expectedLibRawPackageHash = (Get-Content -LiteralPath $libRawPackageHashFile -TotalCount 1).Trim().ToUpperInvariant()
$actualLibRawPackageHash = (Get-FileHash -LiteralPath (Join-Path $resolved 'LibRaw-SOURCE.zip') -Algorithm SHA256).Hash
if ($actualLibRawPackageHash -ne $expectedLibRawPackageHash) {
    Write-Error "LibRaw-SOURCE.zip SHA-256 mismatch: expected $expectedLibRawPackageHash, found $actualLibRawPackageHash"
    exit 1
}

# Shipped legal files must be real: an empty or wrong file would still pass the existence check above, so require
# non-empty content carrying the expected marker text (LGPL/CDDL license titles, NOTICE naming the pinned LibRaw version).
$legalMarkers = @(
    @{ File = 'LibRaw-LICENSE.LGPL'; Marker = 'GNU LESSER GENERAL PUBLIC LICENSE' },
    @{ File = 'LibRaw-LICENSE.CDDL'; Marker = 'COMMON DEVELOPMENT AND DISTRIBUTION LICENSE' },
    @{ File = 'LibRaw-NOTICE.txt'; Marker = 'LibRaw 0.22.2' },
    @{ File = 'THIRD-PARTY-NOTICES.md'; Marker = 'libjpeg-turbo' }
)
foreach ($check in $legalMarkers) {
    $legalPath = Join-Path $resolved $check.File
    $legalText = [IO.File]::ReadAllText($legalPath, [Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($legalText)) {
        Write-Error "$($check.File) is empty."
        exit 1
    }
    if ($legalText.IndexOf($check.Marker, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Error "$($check.File) does not contain the expected text '$($check.Marker)'."
        exit 1
    }
}

# Both executables must come with every assembly their dependency manifest names: a missing library would only show up at the
# first use in the shipped app (the shell loads the WPF bridge late, so a smoke run alone would not notice).
foreach ($depsName in 'PhotoReview.App.deps.json', 'PhotoReview.deps.json') {
    try { $deps = [IO.File]::ReadAllText((Join-Path $resolved $depsName), [Text.Encoding]::UTF8) | ConvertFrom-Json }
    catch { Write-Error "$depsName is not valid JSON: $($_.Exception.Message)"; exit 1 }
    $missingLibraries = New-Object System.Collections.Generic.List[string]
    foreach ($target in @($deps.targets.PSObject.Properties)) {
        foreach ($library in @($target.Value.PSObject.Properties)) {
            if ($null -eq $library.Value.runtime) { continue }
            foreach ($assembly in @($library.Value.runtime.PSObject.Properties)) {
                $fileName = [IO.Path]::GetFileName([string]$assembly.Name)
                if (-not (Test-Path -LiteralPath (Join-Path $resolved $fileName) -PathType Leaf) -and -not $missingLibraries.Contains($fileName)) {
                    $missingLibraries.Add($fileName)
                }
            }
        }
    }
    if ($missingLibraries.Count -gt 0) {
        Write-Error ("$depsName names assemblies that are not in the release folder: " + ($missingLibraries -join ', '))
        exit 1
    }
}

$exe = Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.App.exe')
if ($exe.Length -le 0) { Write-Error 'Release executable is empty.'; exit 1 }
$shellExe = Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.exe')
if ($shellExe.Length -le 0) { Write-Error 'Shell executable PhotoReview.exe is empty.'; exit 1 }
if (-not $SkipIconCheck -and (Get-ExeGroupIconCount -Path $shellExe.FullName) -lt 1) {
    Write-Error 'Shell executable PhotoReview.exe has no icon resource (RT_GROUP_ICON): ApplicationIcon was not applied.'
    exit 1
}
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
# Both entry assemblies must carry the same commit: a folder where only one of the two publishes is from this commit is stale.
foreach ($entry in 'PhotoReview.App.dll', 'PhotoReview.dll') {
    $dllInfo = (Get-Item -LiteralPath (Join-Path $resolved $entry)).VersionInfo
    $actualFileVersion = $dllInfo.FileVersion
    $actualCommit = ([string]$dllInfo.ProductVersion -split '\+', 2)[1] -replace '\.dirty$', ''
    if ([string]$dllInfo.ProductVersion -match '-nogit' -or [string]::IsNullOrEmpty($actualCommit)) {
        Write-Error "Release binary $entry version '$($dllInfo.ProductVersion)' has no git commit (nogit build)."
        exit 1
    }
    if ($actualFileVersion -ne $expectedFileVersion) {
        Write-Error "Release version mismatch in ${entry}: expected $expectedFileVersion, found $actualFileVersion"
        exit 1
    }
    if ($actualCommit -ne $expectedCommit) {
        Write-Error "Release commit mismatch in ${entry}: expected $expectedCommit, found $actualCommit (stale publish folder?)"
        exit 1
    }
}
$dllInfo = (Get-Item -LiteralPath (Join-Path $resolved 'PhotoReview.App.dll')).VersionInfo
$actualFileVersion = $dllInfo.FileVersion

if ($Smoke) {
    # Own process only: started here, killed here on timeout. --smoke creates and shows the real window, then exits 0.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $shellExe.FullName
    $psi.Arguments = '--smoke'
    $psi.WorkingDirectory = $resolved
    $psi.UseShellExecute = $false
    $smokeProcess = [System.Diagnostics.Process]::Start($psi)
    if (-not $smokeProcess.WaitForExit(60000)) {
        try { $smokeProcess.Kill() } catch { }
        Write-Error 'PhotoReview.exe --smoke did not exit within 60 s.'
        exit 1
    }
    if ($smokeProcess.ExitCode -ne 0) {
        Write-Error "PhotoReview.exe --smoke exited with code $($smokeProcess.ExitCode)."
        exit 1
    }
    Write-Output 'PASS: PhotoReview.exe --smoke exited 0'
}

if (-not [string]::IsNullOrWhiteSpace($AotDirectory)) {
    Test-AotRelease -Dir $AotDirectory -RepoRoot $repoRoot -RunSmoke ([bool]$Smoke) -ExpectedCommit $expectedCommit
}

Write-Output "PASS: release files present ($resolved)"
Write-Output "Version: $($dllInfo.ProductVersion) (file $actualFileVersion)"
Write-Output "EXE bytes: $($exe.Length)"
Write-Output "EXE SHA256: $((Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash)"
Write-Output "SHELL EXE bytes: $($shellExe.Length)"
Write-Output "SHELL EXE SHA256: $((Get-FileHash -LiteralPath $shellExe.FullName -Algorithm SHA256).Hash)"
if ($SelfContained) { Write-Output 'PASS: self-contained runtime files present' }
