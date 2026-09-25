# Ownership guard for the destructive wipe in verify-all.ps1 Publish-ReleaseDirectory (TOOL-02).
# Dot-source this file; it only defines functions (no side effects).

$script:PublishMarkerName = '.photoreview-publish'

function Assert-ReleaseDirectoryOwned {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$ApprovedRoots
    )
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $full = [System.IO.Path]::GetFullPath($Directory).TrimEnd([char]92, [char]47)
    $leaf = Split-Path -Leaf $full
    if ($leaf -notin @('publish', 'PhotoReview-self-contained')) {
        throw "Refusing to wipe '$Directory': the release directory name must be 'publish' or 'PhotoReview-self-contained'."
    }
    # Nothing exists yet, so nothing can be deleted.
    if (-not (Test-Path -LiteralPath $full)) { return }

    foreach ($approved in $ApprovedRoots) {
        $rootFull = [System.IO.Path]::GetFullPath($approved).TrimEnd([char]92, [char]47)
        if ($full.StartsWith($rootFull + $sep, [System.StringComparison]::OrdinalIgnoreCase)) { return }
    }
    # Outside every approved output root: only a directory a previous publish created (marker) may be replaced.
    if (Test-Path -LiteralPath (Join-Path $full $script:PublishMarkerName) -PathType Leaf) { return }

    throw "Refusing to wipe '$Directory': it is outside the approved output roots and has no '$($script:PublishMarkerName)' marker left by a previous publish."
}

function Set-ReleaseDirectoryMarker([string]$Directory) {
    if (Test-Path -LiteralPath $Directory -PathType Container) {
        Set-Content -LiteralPath (Join-Path $Directory $script:PublishMarkerName) -Value 'Created by tools/verify-all.ps1; this directory may be wiped by the next publish.'
    }
}
