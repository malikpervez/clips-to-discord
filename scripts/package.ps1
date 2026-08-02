param(
    [string]$FfmpegPath,
    [string]$FfmpegLicensePath,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'ClipsToDiscord.csproj'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory "publish-$Runtime"
$packageDirectory = Join-Path $artifactsDirectory "ClipsToDiscord-$Runtime"
$zipPath = Join-Path $artifactsDirectory "ClipsToDiscord-$Runtime.zip"

foreach ($target in @($publishDirectory, $packageDirectory)) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsDirectory)
    $resolvedTarget = [IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $resolvedTarget"
    }
    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

[IO.Directory]::CreateDirectory($artifactsDirectory) | Out-Null
dotnet publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

[IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'ClipsToDiscord.exe') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.txt') -Destination $packageDirectory

if ($FfmpegPath) {
    if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
        throw "FFmpeg was not found: $FfmpegPath"
    }
    Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $packageDirectory 'ffmpeg.exe')
}
if ($FfmpegLicensePath) {
    if (-not (Test-Path -LiteralPath $FfmpegLicensePath -PathType Leaf)) {
        throw "FFmpeg license was not found: $FfmpegLicensePath"
    }
    Copy-Item -LiteralPath $FfmpegLicensePath -Destination (Join-Path $packageDirectory 'FFMPEG-LICENSE.txt')
}

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
