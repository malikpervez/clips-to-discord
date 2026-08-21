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
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'store-publish-win-x64'
$layoutDirectory = Join-Path $artifactsDirectory 'store-msix-layout'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $artifactsDirectory 'store'
}
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath $projectPath
    $Version = "{0}.0" -f [string]$project.Project.PropertyGroup.Version
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSIX version must use four numeric parts: $Version"
}
foreach ($part in $Version.Split('.')) {
    if ([int]$part -gt 65535) {
        throw "Each MSIX version part must be between 0 and 65535: $Version"
    }
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
foreach ($requiredPath in @($manifestTemplatePath, $sourceIconPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required Store packaging input was not found: $requiredPath"
    }
}
if ($FfmpegPath) {
    foreach ($requiredPath in @($FfmpegPath, $FfmpegLicensePath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required FFmpeg input was not found: $requiredPath"
        }
        if (((Get-Item -LiteralPath $requiredPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "FFmpeg inputs cannot be reparse points: $requiredPath"
        }
    }
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
Copy-Item -LiteralPath (Join-Path $publishDirectory 'ClipsToDiscord.exe') -Destination $layoutDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.txt') -Destination $layoutDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $layoutDirectory
if ($FfmpegPath) {
    Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $layoutDirectory 'ffmpeg.exe')
    Copy-Item -LiteralPath $FfmpegLicensePath -Destination (Join-Path $layoutDirectory 'FFMPEG-LICENSE.txt')
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
