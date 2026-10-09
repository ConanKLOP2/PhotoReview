$ErrorActionPreference = 'Stop'
$progId = 'PhotoReview.App'
$classes = 'HKCU:\Software\Classes'

foreach ($extension in '.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff') {
    $openWith = Join-Path $classes "$extension\OpenWithProgids"
    if (Test-Path -LiteralPath $openWith) {
        Remove-ItemProperty -LiteralPath $openWith -Name $progId -ErrorAction SilentlyContinue
    }
}

$progPath = Join-Path $classes $progId
if (Test-Path -LiteralPath $progPath) {
    Remove-Item -LiteralPath $progPath -Recurse -Force
}

# The Explorer folder command "Browse with PhotoReview": only these two keys, never their neighbours.
foreach ($verb in 'Directory\shell\PhotoReview', 'Directory\Background\shell\PhotoReview') {
    $verbPath = Join-Path $classes $verb
    if (Test-Path -LiteralPath $verbPath) {
        Remove-Item -LiteralPath $verbPath -Recurse -Force
    }
}

Write-Host 'Photo Review file association and Explorer folder menu removed for the current Windows user.'
Write-Host 'Published files and source files were not deleted.'
