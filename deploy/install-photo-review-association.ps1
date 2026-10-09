param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    # Skip the Explorer folder command "Browse with PhotoReview" (only the "Open with" association is registered).
    [switch]$NoFolderMenu
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Executable not found: $ExePath"
}
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$progId = 'PhotoReview.App'
$classes = 'HKCU:\Software\Classes'

New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId" -Name '(Default)' -Value 'Photo Review'
Set-ItemProperty -Path "$classes\$progId\shell\open\command" -Name '(Default)' -Value ('"{0}" "%1"' -f $resolvedExe)

foreach ($extension in '.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff') {
    New-Item -Path "$classes\$extension\OpenWithProgids" -Force | Out-Null
    New-ItemProperty -Path "$classes\$extension\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
}

Write-Host 'Photo Review was registered in Open with.'
Write-Host 'To set it as default: Windows Settings > Apps > Default apps > choose .jpg > Photo Review.'

if (-not $NoFolderMenu) {
    # Explorer: right-click a folder (%1) or the empty space inside a folder (%V) -> "Browse with PhotoReview".
    # Same keys and values the app writes itself (Settings > General > Explorer integration); per user, no administrator rights.
    $verbs = @{
        'Directory\shell\PhotoReview'            = '"{0}" "%1"'
        'Directory\Background\shell\PhotoReview' = '"{0}" "%V"'
    }
    foreach ($verb in $verbs.Keys) {
        $verbPath = "$classes\$verb"
        New-Item -Path "$verbPath\command" -Force | Out-Null
        Set-ItemProperty -Path $verbPath -Name '(Default)' -Value 'Browse with PhotoReview'
        New-ItemProperty -Path $verbPath -Name 'Icon' -Value ('"{0}",0' -f $resolvedExe) -PropertyType String -Force | Out-Null
        Set-ItemProperty -Path "$verbPath\command" -Name '(Default)' -Value ($verbs[$verb] -f $resolvedExe)
    }
    Write-Host 'Added "Browse with PhotoReview" to the Explorer right-click menu of folders.'
}
