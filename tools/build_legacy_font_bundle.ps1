$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$template = Join-Path $projectRoot "Assets\DaocHangulKdaoc.ttf"
$source = Join-Path $projectRoot "third_party\D2CodingLigature\D2CodingBold-Ver1.3.2-20180524-ligature.ttf"
$extended = Join-Path $projectRoot "Assets\DaocHangulExtended.ttf"
$precomposed = Join-Path $projectRoot "Assets\DaocHangulPrecomposed.ttf"
$bundle = Join-Path $projectRoot "Assets\DaocLegacyFonts.br"
$builder = Join-Path $PSScriptRoot "build_d2_extended_legacy_font.py"
$precomposedBuilder = Join-Path $PSScriptRoot "build_precomposed_font.py"

if (-not (Test-Path -LiteralPath $source)) {
    throw "D2CodingLigature 원본 폰트를 찾지 못했습니다: $source"
}

& py -3 $builder --template $template --source $source --output $extended
if ($LASTEXITCODE -ne 0) {
    throw "확장 한글 폰트 생성에 실패했습니다."
}

& py -3 $precomposedBuilder --template $template --source $source --output $precomposed
if ($LASTEXITCODE -ne 0) {
    throw "완성형 한글 폰트 생성에 실패했습니다."
}

$kdaocBytes = [System.IO.File]::ReadAllBytes($template)
$extendedBytes = [System.IO.File]::ReadAllBytes($extended)
$precomposedBytes = [System.IO.File]::ReadAllBytes($precomposed)
$uncompressed = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new(
    $uncompressed,
    [System.Text.Encoding]::UTF8,
    $true)
$writer.Write([int]$kdaocBytes.Length)
$writer.Write([int]$extendedBytes.Length)
$writer.Write([int]$precomposedBytes.Length)
$writer.Write($kdaocBytes)
$writer.Write($extendedBytes)
$writer.Write($precomposedBytes)
$writer.Dispose()

$output = [System.IO.File]::Create($bundle)
$compressor = [System.IO.Compression.BrotliStream]::new(
    $output,
    [System.IO.Compression.CompressionLevel]::SmallestSize,
    $false)
$uncompressed.Position = 0
$uncompressed.CopyTo($compressor)
$compressor.Dispose()
$uncompressed.Dispose()

Write-Host "Hangul font bundle: $bundle"
Write-Host "KDAOC: $($kdaocBytes.Length) bytes"
Write-Host "Extended: $($extendedBytes.Length) bytes"
Write-Host "Precomposed: $($precomposedBytes.Length) bytes"
Write-Host "Brotli bundle: $((Get-Item -LiteralPath $bundle).Length) bytes"
