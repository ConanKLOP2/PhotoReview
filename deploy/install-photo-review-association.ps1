[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    # Skip the Explorer folder command "Browse with PhotoReview" (only the "Open with" association is registered).
    [switch]$NoFolderMenu,

    # Which product the registration is for. 'Wpf' (default, unchanged) = PhotoReview.App.exe with ProgId "PhotoReview.App".
    # 'Shell' = the Win32 + Direct2D PhotoReview.exe with its OWN ProgId "PhotoReview.Shell" and its own folder-menu key
    # "PhotoReview.Shell", so both can be registered side by side and neither overwrites the other (NO-WPF NE-2 = a).
    [ValidateSet('Wpf', 'Shell')]
    [string]$Variant = 'Wpf',

    # Opt-in guard for -Variant Shell: the registration asks for a typed YES unless -Force is given
    # (a non-interactive host must pass -Force). -WhatIf prints what would be written and touches nothing.
    [switch]$Force,

    # Registry root that receives the keys. Default = the real per-user classes. Tests pass a private key.
    [string]$ClassesPath = 'HKCU:\Software\Classes'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Executable not found: $ExePath"
}
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
if ($resolvedExe.IndexOf('"') -ge 0) { throw 'The executable path must not contain a quote.' }

if ($Variant -eq 'Shell') {
    $progId = 'PhotoReview.Shell'
    $friendlyName = 'Photo Review (Win32)'
    $verbKey = 'PhotoReview.Shell'
    $verbText = 'Browse with PhotoReview (Win32)'
}
else {
    $progId = 'PhotoReview.App'
    $friendlyName = 'Photo Review'
    $verbKey = 'PhotoReview'
    $verbText = 'Browse with PhotoReview'
}
$classes = $ClassesPath.TrimEnd('\')
$extensions = '.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff'

if ($Variant -eq 'Shell') {
    # Explicit opt-in. Nothing here ever runs by itself: no installer, no first-run hook, no CI step calls this script.
    Write-Host "This registers '$resolvedExe' under ProgId '$progId' in $classes (current Windows user, no administrator rights)."
    Write-Host 'It adds "Photo Review (Win32)" to Open with for image files; it does NOT change the default app of any file type.'
    if (-not $Force -and -not $WhatIfPreference) {
        if (-not [Environment]::UserInteractive) { throw 'Not an interactive session: pass -Force to confirm the registration.' }
        $answer = Read-Host 'Type YES to continue'
        if ($answer -cne 'YES') { Write-Host 'Cancelled: nothing was written.'; return }
    }
}

if (-not $PSCmdlet.ShouldProcess("$classes\$progId", "Register $resolvedExe")) { return }

New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId" -Name '(Default)' -Value $friendlyName
Set-ItemProperty -Path "$classes\$progId\shell\open\command" -Name '(Default)' -Value ('"{0}" "%1"' -f $resolvedExe)

foreach ($extension in $extensions) {
    New-Item -Path "$classes\$extension\OpenWithProgids" -Force | Out-Null
    New-ItemProperty -Path "$classes\$extension\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
}

Write-Host "$friendlyName was registered in Open with."
Write-Host "To set it as default: Windows Settings > Apps > Default apps > choose .jpg > $friendlyName."

if (-not $NoFolderMenu) {
    # Explorer: right-click a folder (%1) or the empty space inside a folder (%V) -> "Browse with PhotoReview".
    # Same keys and values the app writes itself (Settings > General > Explorer integration); per user, no administrator rights.
    $verbs = @{
        "Directory\shell\$verbKey"            = '"{0}" "%1"'
        "Directory\Background\shell\$verbKey" = '"{0}" "%V"'
    }
    foreach ($verb in $verbs.Keys) {
        $verbPath = "$classes\$verb"
        New-Item -Path "$verbPath\command" -Force | Out-Null
        Set-ItemProperty -Path $verbPath -Name '(Default)' -Value $verbText
        New-ItemProperty -Path $verbPath -Name 'Icon' -Value ('"{0}",0' -f $resolvedExe) -PropertyType String -Force | Out-Null
        Set-ItemProperty -Path "$verbPath\command" -Name '(Default)' -Value ($verbs[$verb] -f $resolvedExe)
    }
    Write-Host "Added `"$verbText`" to the Explorer right-click menu of folders."
}
