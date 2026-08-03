param(
    [string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$version = '7.0.2'
$expectedInstallerSha256 = '5AD54CA3DEF786F8F4212552E54CC6D8D61329E2D24A1CFEE0571D42C2684FF1'
$expectedCompilerFileCount = 132
$expectedCompilerTreeSha256 = 'A33F9522DE575D86EAE8397EFAF2F1BFA76B3FA13F654568C1F5B749F795812A'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $DestinationDirectory) {
    $DestinationDirectory = Join-Path $repositoryRoot 'artifacts\tools\InnoSetup'
}

function Get-CompilerTreeIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    # Bind every file's relative path, length, and SHA-256 into one deterministic tree digest.
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force |
        Sort-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
    $manifestLines = @($files | ForEach-Object {
        $relativePath = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$relativePath|$($_.Length)|$fileHash"
    })
    $payload = [Text.Encoding]::UTF8.GetBytes(($manifestLines -join "`n"))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha256.ComputeHash($payload)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }

    [pscustomobject]@{
        FileCount = $files.Count
        Sha256 = $digest
    }
}

function Assert-CompilerTree {
    param([Parameter(Mandatory = $true)][string]$Path)

    $identity = Get-CompilerTreeIdentity -Path $Path
    if ($identity.FileCount -ne $expectedCompilerFileCount -or
        $identity.Sha256 -ne $expectedCompilerTreeSha256) {
        throw "Inno Setup compiler integrity check failed. Expected $expectedCompilerFileCount files and $expectedCompilerTreeSha256, got $($identity.FileCount) files and $($identity.Sha256). Remove the compiler directory and run this script again."
    }
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$isccPath = Join-Path $destination 'ISCC.exe'
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "The Inno Setup destination is not a directory: $destination"
    }
    Assert-CompilerTree -Path $destination
    Write-Output $isccPath
    exit 0
}

$toolsDirectory = Split-Path -Parent $destination
[IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
$installerPath = Join-Path $toolsDirectory "innosetup-$version-x64.exe"
$downloadUrl = "https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-$version-x64.exe"

if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
    $actualInstallerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    if ($actualInstallerSha256 -ne $expectedInstallerSha256) {
        throw "Cached Inno Setup installer checksum mismatch. Expected $expectedInstallerSha256, got $actualInstallerSha256."
    }
}
else {
    $partialPath = "$installerPath.$([Guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest `
            -Uri $downloadUrl `
            -OutFile $partialPath `
            -MaximumRedirection 5 `
            -UseBasicParsing
        $actualInstallerSha256 = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash
        if ($actualInstallerSha256 -ne $expectedInstallerSha256) {
            throw "Downloaded Inno Setup installer checksum mismatch. Expected $expectedInstallerSha256, got $actualInstallerSha256."
        }
        Move-Item -LiteralPath $partialPath -Destination $installerPath
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            Remove-Item -LiteralPath $partialPath -Force
        }
    }
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

Assert-CompilerTree -Path $destination
Write-Output $isccPath
