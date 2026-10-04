$ErrorActionPreference = 'Stop'
$reviewRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
. (Join-Path $reviewRoot 'tools/Publish-Guard.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('PhotoReview-GuardReview-' + [Guid]::NewGuid().ToString('N'))
$approved = Join-Path $scratch 'approved'
$foreign = Join-Path $scratch 'foreign'
$link = Join-Path $approved 'linked-build'
$target = Join-Path $foreign 'publish'
New-Item -ItemType Directory -Path $approved,$target -Force | Out-Null
try {
 New-Item -ItemType Junction -Path $link -Target $foreign -ErrorAction Stop | Out-Null
 $candidate = Join-Path $link 'publish'
 $refused = $false
 try { Assert-ReleaseDirectoryOwned -Directory $candidate -ApprovedRoots @($approved) } catch { $refused = $true }
 [pscustomobject]@{ Candidate=$candidate; ActualTarget=$target; Refused=$refused; MarkerPresent=(Test-Path (Join-Path $target '.photoreview-publish')); DestructiveActionExecuted=$false } | ConvertTo-Json
 if($refused){throw 'Baseline no longer reproduces ancestor junction bypass.'}
} finally {
 if(Test-Path -LiteralPath $link){[IO.Directory]::Delete($link,$false)}
 $resolvedScratch=[IO.Path]::GetFullPath($scratch)
 $expectedTemp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if(-not $resolvedScratch.StartsWith($expectedTemp,[StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolvedScratch)).StartsWith('PhotoReview-GuardReview-',[StringComparison]::Ordinal)){throw 'Unexpected scratch cleanup target.'}
 Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
