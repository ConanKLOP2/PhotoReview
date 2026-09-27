# Ownership guard for the destructive wipe in verify-all.ps1 Publish-ReleaseDirectory (TOOL-02).
# Dot-source this file; it only defines functions (no side effects) unless run standalone with -SelfTest.
param(
    # Run the in-memory adversarial self-test instead of doing anything (SEC-03). Needs no real
    # directories on disk (it fabricates its own temp fixtures and cleans them up).
    [switch]$SelfTest
)

$script:PublishMarkerName = '.photoreview-publish'

function Get-PathSegments([string]$FullPath) {
    # Component-aware split so containment can be checked segment-by-segment instead of via a raw
    # string prefix (a lexical prefix match can be fooled by sibling names that share characters,
    # e.g. 'C:\approved-root2\publish' vs 'C:\approved-root\publish' -- defensive even though the
    # trailing-separator already appended in the caller happens to block that specific case today).
    $trimmed = $FullPath.TrimEnd([char]92, [char]47)
    return @($trimmed -split '[\\/]+' | Where-Object { $_ -ne '' })
}

function Test-PathUnderRoot {
    param(
        [Parameter(Mandatory)][string]$FullPath,
        [Parameter(Mandatory)][string]$RootFull
    )
    $pathSegs = Get-PathSegments $FullPath
    $rootSegs = Get-PathSegments $RootFull
    # Must be strictly deeper than the root (equal to the root does not count as "under" it).
    if ($pathSegs.Count -le $rootSegs.Count) { return $false }
    for ($i = 0; $i -lt $rootSegs.Count; $i++) {
        if (-not [string]::Equals($pathSegs[$i], $rootSegs[$i], [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

function Resolve-FinalTarget {
    # Follows a chain of reparse points (symlink/junction/mount point) to their final real target,
    # so a symlinked/junctioned 'publish' directory placed at or under an approved root (or made to
    # merely look approved) can't slip an unapproved real directory past the containment check.
    # Filesystem access (Test-Path, Remove-Item, etc.) already transparently follows reparse points,
    # so this only needs to affect the *decision* logic here, not actual I/O elsewhere.
    param([Parameter(Mandatory)][string]$Path)

    $current = $Path
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    for ($hop = 0; $hop -lt 32; $hop++) {
        if (-not $seen.Add($current)) {
            throw "Refusing to wipe '$Path': reparse-point loop detected while resolving '$current'."
        }
        if (-not (Test-Path -LiteralPath $current)) { return $current }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -eq $item -or -not $item.LinkType) { return $current }
        $target = @($item.Target) | Select-Object -First 1
        if ([string]::IsNullOrEmpty($target)) { return $current }
        if (-not [System.IO.Path]::IsPathRooted($target)) {
            $target = Join-Path (Split-Path -Parent $current) $target
        }
        $current = [System.IO.Path]::GetFullPath($target)
    }
    throw "Refusing to wipe '$Path': too many reparse-point hops while resolving the real target (possible loop)."
}

function Assert-ReleaseDirectoryOwned {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$ApprovedRoots
    )
    $full = [System.IO.Path]::GetFullPath($Directory).TrimEnd([char]92, [char]47)
    $leaf = Split-Path -Leaf $full
    if ($leaf -notin @('publish', 'PhotoReview-self-contained')) {
        throw "Refusing to wipe '$Directory': the release directory name must be 'publish' or 'PhotoReview-self-contained'."
    }
    # Nothing exists yet, so nothing can be deleted.
    if (-not (Test-Path -LiteralPath $full)) { return }

    # Resolve any reparse point (symlink/junction/mount point) at or above the target so the
    # containment check below sees where the deletion would *actually* land, not just the name
    # of a link pointing somewhere else entirely.
    $resolved = Resolve-FinalTarget -Path $full

    foreach ($approved in $ApprovedRoots) {
        $rootFull = [System.IO.Path]::GetFullPath($approved).TrimEnd([char]92, [char]47)
        if (Test-PathUnderRoot -FullPath $resolved -RootFull $rootFull) { return }
    }
    # Outside every approved output root: only a directory a previous publish created (marker) may be replaced.
    # Read the marker via the original path -- the filesystem already follows any reparse point transparently,
    # so this checks the same real location that $resolved points at.
    if (Test-Path -LiteralPath (Join-Path $full $script:PublishMarkerName) -PathType Leaf) { return }

    throw "Refusing to wipe '$Directory': it is outside the approved output roots and has no '$($script:PublishMarkerName)' marker left by a previous publish."
}

function Set-ReleaseDirectoryMarker([string]$Directory) {
    if (Test-Path -LiteralPath $Directory -PathType Container) {
        Set-Content -LiteralPath (Join-Path $Directory $script:PublishMarkerName) -Value 'Created by tools/verify-all.ps1; this directory may be wiped by the next publish.'
    }
}

function Invoke-PublishGuardSelfTest {
    # Adversarial, non-destructive proof that Assert-ReleaseDirectoryOwned behaves as intended
    # (SEC-03). Never calls Remove-Item; only ever throws-or-returns against real temp fixtures it
    # creates itself and always cleans up. Exits 0 if every case matches its expectation, 1 otherwise.
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("PublishGuardSelfTest-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        $approvedRoot = Join-Path $tempRoot 'approved-root'
        $siblingRoot = Join-Path $tempRoot 'approved-root2'
        New-Item -ItemType Directory -Path $approvedRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $siblingRoot -Force | Out-Null

        $approvedPublish = Join-Path $approvedRoot 'publish'
        New-Item -ItemType Directory -Path $approvedPublish -Force | Out-Null

        $siblingPublish = Join-Path $siblingRoot 'publish'
        New-Item -ItemType Directory -Path $siblingPublish -Force | Out-Null

        $unapprovedNoMarker = Join-Path $tempRoot 'elsewhere\publish'
        New-Item -ItemType Directory -Path $unapprovedNoMarker -Force | Out-Null

        $unapprovedWithMarker = Join-Path $tempRoot 'elsewhere-marked\publish'
        New-Item -ItemType Directory -Path $unapprovedWithMarker -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $unapprovedWithMarker $script:PublishMarkerName) -Value 'test marker'

        $badLeaf = Join-Path $approvedRoot 'not-a-publish-dir'
        New-Item -ItemType Directory -Path $badLeaf -Force | Out-Null

        $cases = @(
            [pscustomobject]@{
                Name = 'ApprovedRoot_Exact'
                Directory = $approvedPublish
                Roots = @($approvedRoot)
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'SiblingRoot_SharedStringPrefix_NotApproved'
                Directory = $siblingPublish
                Roots = @($approvedRoot)
                ExpectThrow = $true
            },
            [pscustomobject]@{
                Name = 'ApprovedRoot_CaseVariant'
                Directory = $approvedPublish.ToUpperInvariant()
                Roots = @($approvedRoot)
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'ApprovedRoot_TrailingSeparatorAlreadyPresent'
                Directory = $approvedPublish + '\'
                Roots = @($approvedRoot + '\')
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'ApprovedRoot_TrailingSeparator_TargetOnly'
                Directory = $approvedPublish + '\'
                Roots = @($approvedRoot)
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'Unapproved_NoMarker_Refused'
                Directory = $unapprovedNoMarker
                Roots = @($approvedRoot)
                ExpectThrow = $true
            },
            [pscustomobject]@{
                Name = 'Unapproved_WithMarker_Allowed'
                Directory = $unapprovedWithMarker
                Roots = @($approvedRoot)
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'BadLeafName_Refused'
                Directory = $badLeaf
                Roots = @($approvedRoot)
                ExpectThrow = $true
            },
            [pscustomobject]@{
                Name = 'NonexistentTarget_Allowed'
                Directory = (Join-Path $tempRoot 'does-not-exist\publish')
                Roots = @($approvedRoot)
                ExpectThrow = $false
            },
            [pscustomobject]@{
                Name = 'ApprovedRootItself_NotUnderItself_Refused'
                Directory = $approvedRoot
                Roots = @($approvedRoot)
                ExpectThrow = $true  # leaf name of the root itself is not 'publish', so this throws for that reason
            }
        )

        # Symlink/junction case: only added if we can create one without admin rights (a directory
        # junction on NTFS needs no elevation, unlike a symlink). Skipped, not failed, if it can't be
        # created in this environment (still non-destructive either way).
        $junctionCase = $null
        $junctionOutsideTarget = Join-Path $tempRoot 'outside-real-dir'
        New-Item -ItemType Directory -Path $junctionOutsideTarget -Force | Out-Null
        $junctionLink = Join-Path $approvedRoot 'publish-junction'
        try {
            New-Item -ItemType Junction -Path $junctionLink -Target $junctionOutsideTarget -ErrorAction Stop | Out-Null
            $junctionCase = [pscustomobject]@{
                Name = 'JunctionUnderApprovedRoot_PointsOutside_Refused'
                Directory = $junctionLink
                Roots = @($approvedRoot)
                ExpectThrow = $true
            }
        }
        catch {
            Write-Output "JunctionUnderApprovedRoot_PointsOutside_Refused: skipped (could not create a junction in this environment: $($_.Exception.Message))"
        }
        if ($junctionCase) {
            # This directory is literally named 'publish-junction', which fails the leaf-name check
            # before containment is even considered -- rename the leaf via a second junction named
            # exactly 'publish' so the case actually exercises reparse-point resolution.
            Remove-Item -LiteralPath $junctionLink -Force -ErrorAction SilentlyContinue
            $junctionRootDir = Join-Path $approvedRoot 'junction-holder'
            New-Item -ItemType Directory -Path $junctionRootDir -Force | Out-Null
            $junctionPublish = Join-Path $junctionRootDir 'publish'
            New-Item -ItemType Junction -Path $junctionPublish -Target $junctionOutsideTarget -ErrorAction Stop | Out-Null
            $junctionCase.Directory = $junctionPublish
            $cases += $junctionCase
        }

        $allOk = $true
        foreach ($c in $cases) {
            $threw = $false
            $thrownMessage = ''
            try {
                Assert-ReleaseDirectoryOwned -Directory $c.Directory -ApprovedRoots $c.Roots
            }
            catch {
                $threw = $true
                $thrownMessage = $_.Exception.Message
            }

            $ok = ($threw -eq $c.ExpectThrow)
            if (-not $ok) { $allOk = $false }
            $detail = if ($c.ExpectThrow) {
                if ($threw) { "PASS (refused as expected): $thrownMessage" } else { 'FAIL - expected a throw (refusal) but it returned normally' }
            }
            else {
                if (-not $threw) { 'PASS (allowed as expected)' } else { "FAIL - expected no throw but got: $thrownMessage" }
            }
            Write-Output ('{0,-55} {1}' -f $c.Name, $(if ($ok) { 'ok' } else { 'MISMATCH' }))
            Write-Output "  $detail"
        }

        if ($allOk) {
            Write-Output 'SELFTEST PASS: all Assert-ReleaseDirectoryOwned cases matched their expectation'
            exit 0
        }
        else {
            Write-Output 'SELFTEST FAIL: one or more Assert-ReleaseDirectoryOwned cases did not match their expectation'
            exit 1
        }
    }
    finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) { Invoke-PublishGuardSelfTest }
