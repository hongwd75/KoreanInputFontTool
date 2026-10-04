param(
    [switch]$RebuildNativeHook,
    [string]$OutputDirectoryName = "korean-input-font-tool-win-x64"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "KoreanInputFontTool.csproj"
$nativeProject = Join-Path $PSScriptRoot "Native\KoreanRenderHook32\KoreanRenderHook32.vcxproj"
$nativeHookVersion = "18_35"
$nativeAsset = Join-Path $PSScriptRoot "Assets\KoreanRenderHook32-v$nativeHookVersion.dll"
$compressedNativeAsset = "$nativeAsset.br"
$distRoot = Join-Path $PSScriptRoot "dist"
if ([string]::IsNullOrWhiteSpace($OutputDirectoryName) -or
    [System.IO.Path]::GetFileName($OutputDirectoryName) -ne $OutputDirectoryName) {
    throw "OutputDirectoryName must be a single directory name."
}
$output = Join-Path $distRoot $OutputDirectoryName

$resolvedDistRoot = [System.IO.Path]::GetFullPath($distRoot).TrimEnd('\')
$resolvedOutput = [System.IO.Path]::GetFullPath($output).TrimEnd('\')
if (-not $resolvedOutput.StartsWith($resolvedDistRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected publish output path: $resolvedOutput"
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

if ($RebuildNativeHook) {
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

    Write-Host "Rebuilt native hook compatibility version: $nativeHookVersion"
    Write-Host "Compressed native hook: $((Get-Item -LiteralPath $compressedNativeAsset).Length) bytes"
}
elseif (-not (Test-Path -LiteralPath $compressedNativeAsset -PathType Leaf)) {
    throw "Embedded native hook not found: $compressedNativeAsset. Run with -RebuildNativeHook."
}
else {
    Write-Host "Reusing native hook compatibility version: $nativeHookVersion"
}

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $output `
    /p:PlatformTarget=x64 `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:Optimize=true `
    /p:PublishReadyToRun=false `
    /p:DebugType=None `
    /p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$intermediatePaths = @(
    (Join-Path $PSScriptRoot "bin"),
    (Join-Path $PSScriptRoot "obj"),
    (Join-Path $PSScriptRoot "Native\KoreanRenderHook32\obj")
)
foreach ($intermediatePath in $intermediatePaths) {
    $resolvedIntermediatePath = [System.IO.Path]::GetFullPath($intermediatePath).TrimEnd('\')
    if (-not $resolvedIntermediatePath.StartsWith(
        $resolvedProjectRoot + '\',
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected intermediate output path: $resolvedIntermediatePath"
    }

    if (Test-Path -LiteralPath $resolvedIntermediatePath) {
        Remove-Item -LiteralPath $resolvedIntermediatePath -Recurse -Force
    }
}

Write-Host "Compact Release/x64 executable: $output\KoreanInputFontTool.exe"
Write-Host "Runtime requirement: Microsoft .NET 8 Desktop Runtime (x64)"
