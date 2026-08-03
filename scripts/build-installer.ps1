param(
    [string]$IsccPath,
    [string]$PackageDirectory,
    [string]$OutputDirectory,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$installerScript = Join-Path $repositoryRoot 'installer\ClipsToDiscord.iss'

if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $artifactsDirectory 'ClipsToDiscord-win-x64'
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
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'ISCC.exe was not found. Run scripts\get-inno-setup.ps1 or pass -IsccPath.'
}

$requiredFiles = @('ClipsToDiscord.exe', 'README.txt')
foreach ($fileName in $requiredFiles) {
    $path = Join-Path $PackageDirectory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The portable package is incomplete: $path"
    }
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$resolvedPackage = [IO.Path]::GetFullPath($PackageDirectory)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$setupPath = Join-Path $resolvedOutput 'ClipsToDiscord-Setup.exe'
if (Test-Path -LiteralPath $setupPath) {
    Remove-Item -LiteralPath $setupPath -Force
}

& $IsccPath `
    '/Qp' `
    "/DMyAppVersion=$Version" `
    "/DPackageDir=$resolvedPackage" `
    "/DOutputDir=$resolvedOutput" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup did not create the expected installer: $setupPath"
}

Get-Item -LiteralPath $setupPath | Select-Object FullName, Length
