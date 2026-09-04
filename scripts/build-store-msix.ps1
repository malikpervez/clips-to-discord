param(
    [string]$FfmpegPath,
    [string]$FfmpegLicensePath,
    [string]$Version,
    [string]$IdentityName = 'DKGLabs.ClipCord',
    [string]$Publisher = 'CN=3BF1D083-8330-4BB1-A011-C31DD2E3487F',
    [string]$PublisherDisplayName = 'DKG Labs',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'ClipsToDiscord.csproj'
$manifestTemplatePath = Join-Path $repositoryRoot 'store\Package.appxmanifest.template'
$sourceIconPath = Join-Path $repositoryRoot 'assets\app-icon.png'
$modelSourcePath = Join-Path $repositoryRoot 'assets\models\modnet-photographic.onnx'
$noticesSourcePath = Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md'
$licensesSourceDirectory = Join-Path $repositoryRoot 'licenses'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'store-publish-win-x64'
$layoutDirectory = Join-Path $artifactsDirectory 'store-msix-layout'

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

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $artifactsDirectory 'store'
}
if (-not $Version) {
    $Version = & (Join-Path $PSScriptRoot 'resolve-package-version.ps1') `
        -ProjectPath $projectPath `
        -Format Store
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSIX version must use four numeric parts: $Version"
}
$versionParts = $Version.Split('.')
foreach ($part in $versionParts) {
    if ([int]$part -gt 65535) {
        throw "Each MSIX version part must be between 0 and 65535: $Version"
    }
}
if ([int]$versionParts[0] -eq 0) {
    throw "The MSIX major version must be greater than zero: $Version"
}
if ([int]$versionParts[3] -ne 0) {
    throw "The fourth MSIX version part is reserved for Microsoft Store use and must be zero: $Version"
}
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
    throw "The Store identity name contains unsupported characters: $IdentityName"
}
if ([string]::IsNullOrWhiteSpace($Publisher) -or
    [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'Publisher identity values cannot be empty.'
}
if ([bool]$FfmpegPath -ne [bool]$FfmpegLicensePath) {
    throw 'FfmpegPath and FfmpegLicensePath must either both be supplied or both be omitted.'
}
[void](Get-OrdinaryFile $manifestTemplatePath 'The Store manifest template')
[void](Get-OrdinaryFile $sourceIconPath 'The Store source icon')
if ($FfmpegPath) {
    [void](Get-OrdinaryFile $FfmpegPath 'FFmpeg')
    [void](Get-OrdinaryFile $FfmpegLicensePath 'The FFmpeg license')
}

function Resolve-ContainedPath([string]$Root, [string]$Path) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside $resolvedRoot`: $resolvedPath"
    }
    return $resolvedPath
}

foreach ($target in @($publishDirectory, $layoutDirectory)) {
    $resolvedTarget = Resolve-ContainedPath $artifactsDirectory $target
    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$packagePath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "ClipCord_$($Version)_x64.msix"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

[IO.Directory]::CreateDirectory($layoutDirectory) | Out-Null
$assetsDirectory = Join-Path $layoutDirectory 'Assets'
[IO.Directory]::CreateDirectory($assetsDirectory) | Out-Null
$publishedExecutable = Get-OrdinaryFile `
    (Join-Path $publishDirectory 'ClipsToDiscord.exe') `
    'The published ClipCord executable'
$readmeSource = Get-OrdinaryFile (Join-Path $repositoryRoot 'README.txt') 'The Store README'
$publishedModelDirectory = Join-Path $publishDirectory 'models'
$publishedLicenseDirectory = Join-Path $publishDirectory 'licenses'
Assert-ExactFileDirectory $publishedModelDirectory @($modelSource) 'The published model directory'
Assert-ExactFileDirectory $publishedLicenseDirectory $licenseSources 'The published license directory'

Copy-Item -LiteralPath $publishedExecutable.FullName -Destination $layoutDirectory
Copy-Item -LiteralPath $readmeSource.FullName -Destination $layoutDirectory
Copy-Item -LiteralPath $noticesSource.FullName -Destination $layoutDirectory
if ($FfmpegPath) {
    Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $layoutDirectory 'ffmpeg.exe')
    Copy-Item -LiteralPath $FfmpegLicensePath -Destination (Join-Path $layoutDirectory 'FFMPEG-LICENSE.txt')
}
$layoutModelDirectory = Join-Path $layoutDirectory 'models'
$layoutLicenseDirectory = Join-Path $layoutDirectory 'licenses'
[IO.Directory]::CreateDirectory($layoutModelDirectory) | Out-Null
[IO.Directory]::CreateDirectory($layoutLicenseDirectory) | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $publishedModelDirectory $modelSource.Name) `
    -Destination $layoutModelDirectory
foreach ($license in $licenseSources) {
    Copy-Item `
        -LiteralPath (Join-Path $publishedLicenseDirectory $license.Name) `
        -Destination $layoutLicenseDirectory
}

Add-Type -AssemblyName System.Drawing
function New-SquareLogo([int]$Side, [string]$Destination) {
    $source = [Drawing.Image]::FromFile($sourceIconPath)
    try {
        $bitmap = [Drawing.Bitmap]::new($Side, $Side, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, 0, 0, $Side, $Side)
            }
            finally { $graphics.Dispose() }
            $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    finally { $source.Dispose() }
}

function New-WideLogo([string]$Destination) {
    $bitmap = [Drawing.Bitmap]::new(310, 150, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::FromArgb(255, 17, 24, 39))
            $source = [Drawing.Image]::FromFile($sourceIconPath)
            try {
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($source, 92, 12, 126, 126)
            }
            finally { $source.Dispose() }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

New-SquareLogo 44 (Join-Path $assetsDirectory 'Square44x44Logo.png')
New-SquareLogo 50 (Join-Path $assetsDirectory 'StoreLogo.png')
New-SquareLogo 71 (Join-Path $assetsDirectory 'Square71x71Logo.png')
New-SquareLogo 150 (Join-Path $assetsDirectory 'Square150x150Logo.png')
New-SquareLogo 310 (Join-Path $assetsDirectory 'Square310x310Logo.png')
New-WideLogo (Join-Path $assetsDirectory 'Wide310x150Logo.png')

$manifest = Get-Content -LiteralPath $manifestTemplatePath -Raw
$xmlEscapedIdentity = [Security.SecurityElement]::Escape($IdentityName)
$xmlEscapedPublisher = [Security.SecurityElement]::Escape($Publisher)
$xmlEscapedPublisherDisplayName = [Security.SecurityElement]::Escape($PublisherDisplayName)
$manifest = $manifest.Replace('@@IDENTITY_NAME@@', $xmlEscapedIdentity)
$manifest = $manifest.Replace('@@PUBLISHER@@', $xmlEscapedPublisher)
$manifest = $manifest.Replace('@@PUBLISHER_DISPLAY_NAME@@', $xmlEscapedPublisherDisplayName)
$manifest = $manifest.Replace('@@VERSION@@', $Version)
[IO.File]::WriteAllText(
    (Join-Path $layoutDirectory 'AppxManifest.xml'),
    $manifest,
    [Text.UTF8Encoding]::new($false))

$allowedLayoutNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    'AppxManifest.xml',
    'Assets',
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
    'models',
    'licenses'
) | ForEach-Object { [void]$allowedLayoutNames.Add($_) }
if ($FfmpegPath) {
    [void]$allowedLayoutNames.Add('ffmpeg.exe')
    [void]$allowedLayoutNames.Add('FFMPEG-LICENSE.txt')
}
$layoutItems = @(Get-ChildItem -LiteralPath $layoutDirectory -Force)
$unexpectedLayoutItems = @($layoutItems | Where-Object {
    -not $allowedLayoutNames.Contains($_.Name) -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
    ($_.PSIsContainer -and $_.Name -notin @('Assets', 'models', 'licenses')) -or
    (-not $_.PSIsContainer -and $_.Name -in @('Assets', 'models', 'licenses'))
})
if ($unexpectedLayoutItems.Count -gt 0 -or $layoutItems.Count -ne $allowedLayoutNames.Count) {
    throw "The Store layout contains missing, unexpected, or unsafe entries: $(($unexpectedLayoutItems.Name | Sort-Object) -join ', ')"
}
foreach ($requiredFile in @(
        'AppxManifest.xml',
        'ClipsToDiscord.exe',
        'README.txt',
        'THIRD_PARTY_NOTICES.md')) {
    [void](Get-OrdinaryFile (Join-Path $layoutDirectory $requiredFile) 'A required Store payload file')
}
if ($FfmpegPath) {
    [void](Get-OrdinaryFile (Join-Path $layoutDirectory 'ffmpeg.exe') 'The Store FFmpeg executable')
    [void](Get-OrdinaryFile (Join-Path $layoutDirectory 'FFMPEG-LICENSE.txt') 'The Store FFmpeg license')
}
if ((Get-FileHash -LiteralPath (Join-Path $layoutDirectory 'THIRD_PARTY_NOTICES.md') -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $noticesSource.FullName -Algorithm SHA256).Hash) {
    throw 'The Store third-party notices file does not match the repository source.'
}
Assert-ExactFileDirectory $layoutModelDirectory @($modelSource) 'The Store model directory'
Assert-ExactFileDirectory $layoutLicenseDirectory $licenseSources 'The Store license directory'
$expectedAssetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    'Square44x44Logo.png',
    'StoreLogo.png',
    'Square71x71Logo.png',
    'Square150x150Logo.png',
    'Square310x310Logo.png',
    'Wide310x150Logo.png'
) | ForEach-Object { [void]$expectedAssetNames.Add($_) }
$assetItems = @(Get-ChildItem -LiteralPath $assetsDirectory -Force)
$unexpectedAssets = @($assetItems | Where-Object {
    $_.PSIsContainer -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
    $_.Length -le 0 -or
    -not $expectedAssetNames.Contains($_.Name)
})
if ($unexpectedAssets.Count -gt 0 -or $assetItems.Count -ne $expectedAssetNames.Count) {
    throw 'The Store Assets directory contains missing, unexpected, or unsafe entries.'
}

$makeAppxCandidates = @(Get-ChildItem `
    -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter makeappx.exe `
    -File `
    -Recurse `
    -ErrorAction SilentlyContinue | Where-Object {
        $_.FullName -match '\\x64\\makeappx\.exe$'
    } | Sort-Object FullName -Descending)
if ($makeAppxCandidates.Count -eq 0) {
    throw 'MakeAppx.exe was not found. Install the Windows SDK packaging tools.'
}
$makeAppxPath = $makeAppxCandidates[0].FullName
& $makeAppxPath pack /o /d $layoutDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE"
}

Get-Item -LiteralPath $packagePath | Select-Object FullName, Length
