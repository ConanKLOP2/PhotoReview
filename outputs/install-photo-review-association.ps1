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

Write-Host 'Đã đăng ký Photo Review trong Open with.'
Write-Host 'Để đặt làm mặc định: Settings > Apps > Default apps > chọn .jpg > Photo Review.'
