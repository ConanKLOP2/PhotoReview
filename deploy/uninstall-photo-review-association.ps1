$ErrorActionPreference = 'Stop'
$progId = 'PhotoReview.App'
$classes = 'HKCU:\Software\Classes'

foreach ($extension in '.jpg', '.jpeg', '.png') {
    $openWith = Join-Path $classes "$extension\OpenWithProgids"
    if (Test-Path -LiteralPath $openWith) {
        Remove-ItemProperty -LiteralPath $openWith -Name $progId -ErrorAction SilentlyContinue
    }
}

$progPath = Join-Path $classes $progId
if (Test-Path -LiteralPath $progPath) {
    Remove-Item -LiteralPath $progPath -Recurse -Force
}

Write-Host 'Photo Review file association removed for the current Windows user.'
Write-Host 'Published files and source files were not deleted.'
