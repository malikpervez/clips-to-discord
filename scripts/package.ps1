param(
    [string]$FfmpegPath,
    [string]$FfmpegLicensePath,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'ClipsToDiscord.csproj'
$modelSourcePath = Join-Path $repositoryRoot 'assets\models\modnet-photographic.onnx'
$noticesSourcePath = Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md'
$licensesSourceDirectory = Join-Path $repositoryRoot 'licenses'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
if ($Runtime -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw "Runtime contains unsafe path characters: $Runtime"
}
$publishDirectory = Join-Path $artifactsDirectory "publish-$Runtime"
$packageDirectory = Join-Path $artifactsDirectory "ClipCord-$Runtime"
$zipPath = Join-Path $artifactsDirectory "ClipCord-$Runtime.zip"

function Get-OrdinaryFile([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0) {
        throw "$Description must be a non-empty ordinary file: $Path"
    }
    return $item
}

function Get-RepositoryLicenseFiles {
    if (-not (Test-Path -LiteralPath $licensesSourceDirectory -PathType Container)) {
        throw "The repository license directory was not found: $licensesSourceDirectory"
    }
    $directory = Get-Item -LiteralPath $licensesSourceDirectory -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The repository license directory cannot be a reparse point: $licensesSourceDirectory"
    }
    $items = @(Get-ChildItem -LiteralPath $licensesSourceDirectory -Force)
    if ($items.Count -eq 0) {
        throw 'The repository license directory cannot be empty.'
    }
    $unsafe = @($items | Where-Object {
        $_.PSIsContainer -or
        (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        $_.Length -le 0
    })
    if ($unsafe.Count -gt 0) {
        throw "The repository license directory contains an unexpected or unsafe entry: $(($unsafe.Name | Sort-Object) -join ', ')"
    }
    return @($items | Sort-Object Name)
}

function Assert-ExactFileDirectory(
    [string]$Directory,
    [array]$ExpectedSourceFiles,
    [string]$Description) {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Description was not found: $Directory"
    }
    $directoryItem = Get-Item -LiteralPath $Directory -Force
    if (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description cannot be a reparse point: $Directory"
    }
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    $expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($source in $ExpectedSourceFiles) { [void]$expectedNames.Add($source.Name) }
    $unexpected = @($items | Where-Object {
        $_.PSIsContainer -or
        (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        -not $expectedNames.Contains($_.Name)
    })
    if ($unexpected.Count -gt 0 -or $items.Count -ne $ExpectedSourceFiles.Count) {
        throw "$Description contains missing, unexpected, or unsafe entries."
    }
    foreach ($source in $ExpectedSourceFiles) {
        $candidate = Get-OrdinaryFile (Join-Path $Directory $source.Name) "$Description file"
        if ($candidate.Name -cne $source.Name -or
            $candidate.Length -ne $source.Length -or
            (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash -cne
                (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash) {
            throw "$Description file does not exactly match the repository source: $($source.Name)"
        }
    }
}

$modelSource = Get-OrdinaryFile $modelSourcePath 'The MODNet model'
$noticesSource = Get-OrdinaryFile $noticesSourcePath 'The third-party notices file'
$licenseSources = @(Get-RepositoryLicenseFiles)

if ([bool]$FfmpegPath -ne [bool]$FfmpegLicensePath) {
    throw 'FfmpegPath and FfmpegLicensePath must either both be supplied or both be omitted.'
}
if ($FfmpegPath) {
    [void](Get-OrdinaryFile $FfmpegPath 'FFmpeg')
    [void](Get-OrdinaryFile $FfmpegLicensePath 'The FFmpeg license')
}

foreach ($target in @($publishDirectory, $packageDirectory)) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsDirectory).TrimEnd('\', '/')
    $resolvedTarget = [IO.Path]::GetFullPath($target)
    $artifactsPrefix = $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
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
$publishedExecutable = Get-OrdinaryFile `
    (Join-Path $publishDirectory 'ClipsToDiscord.exe') `
    'The published ClipCord executable'
$readmeSource = Get-OrdinaryFile (Join-Path $repositoryRoot 'README.txt') 'The portable README'
$publishedModelDirectory = Join-Path $publishDirectory 'models'
$publishedLicenseDirectory = Join-Path $publishDirectory 'licenses'
Assert-ExactFileDirectory $publishedModelDirectory @($modelSource) 'The published model directory'
Assert-ExactFileDirectory $publishedLicenseDirectory $licenseSources 'The published license directory'

Copy-Item -LiteralPath $publishedExecutable.FullName -Destination $packageDirectory
Copy-Item -LiteralPath $readmeSource.FullName -Destination $packageDirectory
Copy-Item -LiteralPath $noticesSource.FullName -Destination $packageDirectory
$packageModelDirectory = Join-Path $packageDirectory 'models'
$packageLicenseDirectory = Join-Path $packageDirectory 'licenses'
[IO.Directory]::CreateDirectory($packageModelDirectory) | Out-Null
[IO.Directory]::CreateDirectory($packageLicenseDirectory) | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $publishedModelDirectory $modelSource.Name) `
    -Destination $packageModelDirectory
foreach ($license in $licenseSources) {
    Copy-Item `
        -LiteralPath (Join-Path $publishedLicenseDirectory $license.Name) `
        -Destination $packageLicenseDirectory
}

if ($FfmpegPath) {
    Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $packageDirectory 'ffmpeg.exe')
}
if ($FfmpegLicensePath) {
    Copy-Item -LiteralPath $FfmpegLicensePath -Destination (Join-Path $packageDirectory 'FFMPEG-LICENSE.txt')
}

$allowedRootNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
    'models',
    'licenses'
) | ForEach-Object { [void]$allowedRootNames.Add($_) }
if ($FfmpegPath) {
    [void]$allowedRootNames.Add('ffmpeg.exe')
    [void]$allowedRootNames.Add('FFMPEG-LICENSE.txt')
}
$packageItems = @(Get-ChildItem -LiteralPath $packageDirectory -Force)
$unexpectedPackageItems = @($packageItems | Where-Object {
    -not $allowedRootNames.Contains($_.Name) -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
    ($_.PSIsContainer -and $_.Name -notin @('models', 'licenses')) -or
    (-not $_.PSIsContainer -and $_.Name -in @('models', 'licenses'))
})
if ($unexpectedPackageItems.Count -gt 0 -or $packageItems.Count -ne $allowedRootNames.Count) {
    throw "The portable package contains missing, unexpected, or unsafe entries: $(($unexpectedPackageItems.Name | Sort-Object) -join ', ')"
}
foreach ($requiredFile in @(
        'ClipsToDiscord.exe',
        'README.txt',
        'THIRD_PARTY_NOTICES.md')) {
    [void](Get-OrdinaryFile (Join-Path $packageDirectory $requiredFile) 'A required portable payload file')
}
if ((Get-FileHash -LiteralPath (Join-Path $packageDirectory 'THIRD_PARTY_NOTICES.md') -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $noticesSource.FullName -Algorithm SHA256).Hash) {
    throw 'The portable third-party notices file does not match the repository source.'
}
if ($FfmpegPath) {
    [void](Get-OrdinaryFile (Join-Path $packageDirectory 'ffmpeg.exe') 'The portable FFmpeg executable')
    [void](Get-OrdinaryFile (Join-Path $packageDirectory 'FFMPEG-LICENSE.txt') 'The portable FFmpeg license')
}
Assert-ExactFileDirectory $packageModelDirectory @($modelSource) 'The portable model directory'
Assert-ExactFileDirectory $packageLicenseDirectory $licenseSources 'The portable license directory'

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
