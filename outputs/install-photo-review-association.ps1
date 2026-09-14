param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$progId = 'PhotoReview.App'
$classes = 'HKCU:\Software\Classes'

New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId" -Name '(Default)' -Value 'Photo Review'
Set-ItemProperty -Path "$classes\$progId\shell\open\command" -Name '(Default)' -Value ('"{0}" "%1"' -f $resolvedExe)

foreach ($extension in '.jpg', '.jpeg', '.png') {
    New-Item -Path "$classes\$extension\OpenWithProgids" -Force | Out-Null
    New-ItemProperty -Path "$classes\$extension\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
}

Write-Host 'Photo Review was registered in Open with.'
Write-Host 'To set it as default: Windows Settings > Apps > Default apps > choose .jpg > Photo Review.'
