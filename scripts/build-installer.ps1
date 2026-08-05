param(
    [string]$IsccPath,
    [string]$PackageDirectory,
    [string]$OutputDirectory,
    [string]$Version,
    [switch]$RequireFfmpeg
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$installerScript = Join-Path $repositoryRoot 'installer\ClipsToDiscord.iss'
$applicationIconPath = Join-Path $repositoryRoot 'assets\ClipsToDiscord.ico'

if (-not (Test-Path -LiteralPath $applicationIconPath -PathType Leaf)) {
    throw "The application icon was not found: $applicationIconPath"
}
$applicationIconItem = Get-Item -LiteralPath $applicationIconPath -Force
if (($applicationIconItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The application icon cannot be a reparse point: $applicationIconPath"
}

if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $artifactsDirectory 'ClipCord-win-x64'
}
if (-not $OutputDirectory) {
    $OutputDirectory = $artifactsDirectory
}
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $repositoryRoot 'ClipsToDiscord.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Installer version must use major.minor.patch format: $Version"
}

if (-not $IsccPath) {
    $IsccPath = & (Join-Path $PSScriptRoot 'get-inno-setup.ps1')
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'The verified Inno Setup compiler was not found.'
}

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "The portable package directory was not found: $PackageDirectory"
}
$packageDirectoryItem = Get-Item -LiteralPath $PackageDirectory -Force
if (($packageDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The portable package directory cannot be a reparse point: $PackageDirectory"
}

$allowedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('ClipsToDiscord.exe', 'README.txt', 'ffmpeg.exe', 'FFMPEG-LICENSE.txt') |
    ForEach-Object { [void]$allowedNames.Add($_) }
$packageItems = @(Get-ChildItem -LiteralPath $PackageDirectory -Force)
$unexpectedItems = @($packageItems | Where-Object {
    $_.PSIsContainer -or
    -not $allowedNames.Contains($_.Name) -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
})
if ($unexpectedItems.Count -gt 0) {
    $unexpectedNames = ($unexpectedItems | ForEach-Object Name | Sort-Object) -join ', '
    throw "The portable package contains unexpected or unsafe items: $unexpectedNames"
}

$requiredFiles = @('ClipsToDiscord.exe', 'README.txt')
foreach ($fileName in $requiredFiles) {
    $path = Join-Path $PackageDirectory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The portable package is incomplete: $path"
    }
}

$ffmpegPath = Join-Path $PackageDirectory 'ffmpeg.exe'
$ffmpegLicensePath = Join-Path $PackageDirectory 'FFMPEG-LICENSE.txt'
$hasFfmpeg = Test-Path -LiteralPath $ffmpegPath -PathType Leaf
$hasFfmpegLicense = Test-Path -LiteralPath $ffmpegLicensePath -PathType Leaf
if ($hasFfmpeg -ne $hasFfmpegLicense) {
    throw 'ffmpeg.exe and FFMPEG-LICENSE.txt must either both be present or both be absent.'
}
if ($RequireFfmpeg -and -not $hasFfmpeg) {
    throw 'The release installer requires ffmpeg.exe and FFMPEG-LICENSE.txt.'
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$resolvedPackage = $PackageDirectory
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$setupPath = Join-Path $resolvedOutput 'ClipCord-Setup.exe'
if (Test-Path -LiteralPath $setupPath) {
    Remove-Item -LiteralPath $setupPath -Force
}

& $IsccPath `
    '/Qp' `
    "/DMyAppVersion=$Version" `
    "/DPackageDir=$resolvedPackage" `
    "/DOutputDir=$resolvedOutput" `
    "/DRepositoryRoot=$repositoryRoot" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup did not create the expected installer: $setupPath"
}

Get-Item -LiteralPath $setupPath | Select-Object FullName, Length
