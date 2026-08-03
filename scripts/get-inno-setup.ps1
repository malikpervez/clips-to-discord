param(
    [string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$version = '7.0.2'
$expectedSha256 = '5AD54CA3DEF786F8F4212552E54CC6D8D61329E2D24A1CFEE0571D42C2684FF1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $DestinationDirectory) {
    $DestinationDirectory = Join-Path $repositoryRoot 'artifacts\tools\InnoSetup'
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$isccPath = Join-Path $destination 'ISCC.exe'
if (Test-Path -LiteralPath $isccPath -PathType Leaf) {
    Write-Output $isccPath
    exit 0
}

$toolsDirectory = Split-Path -Parent $destination
[IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
$installerPath = Join-Path $toolsDirectory "innosetup-$version-x64.exe"
$downloadUrl = "https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-$version-x64.exe"

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath
}
$actualSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "Inno Setup download checksum mismatch. Expected $expectedSha256, got $actualSha256."
}

$arguments = @(
    '/PORTABLE=1',
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/CURRENTUSER',
    "/DIR=`"$destination`""
)
$process = Start-Process `
    -FilePath $installerPath `
    -ArgumentList $arguments `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    throw "Inno Setup portable installation failed with exit code $($process.ExitCode)."
}
if (-not (Test-Path -LiteralPath $isccPath -PathType Leaf)) {
    throw "Inno Setup compiler was not created at $isccPath"
}

Write-Output $isccPath
