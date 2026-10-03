$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "KoreanInputFontTool.csproj"
$nativeProject = Join-Path $PSScriptRoot "Native\KoreanRenderHook32\KoreanRenderHook32.vcxproj"
$nativeAsset = Join-Path $PSScriptRoot "Assets\KoreanRenderHook32-v18_9.dll"
$compressedNativeAsset = "$nativeAsset.br"
$distRoot = Join-Path $PSScriptRoot "dist"
$output = Join-Path $distRoot "korean-input-font-tool-win-x64"

$resolvedDistRoot = [System.IO.Path]::GetFullPath($distRoot).TrimEnd('\')
$resolvedOutput = [System.IO.Path]::GetFullPath($output).TrimEnd('\')
if (-not $resolvedOutput.StartsWith($resolvedDistRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected publish output path: $resolvedOutput"
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio MSBuild 검색 도구를 찾지 못했습니다: $vswhere"
}
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1
if (-not $msbuild) {
    throw "Visual Studio MSBuild.exe를 찾지 못했습니다."
}

& $msbuild $nativeProject /m /t:Build "/p:Configuration=Release;Platform=Win32" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    throw "KoreanRenderHook32 Release/Win32 build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $nativeAsset -PathType Leaf)) {
    throw "KoreanRenderHook32 output not found: $nativeAsset"
}

$nativeInput = [System.IO.File]::OpenRead($nativeAsset)
try {
    $compressedOutput = [System.IO.File]::Create($compressedNativeAsset)
    try {
        $compressor = [System.IO.Compression.BrotliStream]::new(
            $compressedOutput,
            [System.IO.Compression.CompressionLevel]::SmallestSize,
            $true)
        try {
            $nativeInput.CopyTo($compressor)
        }
        finally {
            $compressor.Dispose()
        }
    }
    finally {
        $compressedOutput.Dispose()
    }
}
finally {
    $nativeInput.Dispose()
}

Write-Host "Compressed native hook: $((Get-Item -LiteralPath $compressedNativeAsset).Length) bytes"

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $output `
    /p:PlatformTarget=x64 `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:DebugType=None `
    /p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Compact Release/x64 executable: $output\KoreanInputFontTool.exe"
Write-Host "Runtime requirement: Microsoft .NET 8 Desktop Runtime (x64)"
