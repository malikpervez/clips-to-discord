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
$modelSourcePath = Join-Path $repositoryRoot 'assets\models\modnet-photographic.onnx'
$noticesSourcePath = Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md'
$licensesSourceDirectory = Join-Path $repositoryRoot 'licenses'

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

function Assert-InstallerPayloadEntries {
    [void](Get-OrdinaryFile $installerScript 'The Inno Setup script')
    $scriptLines = @(Get-Content -LiteralPath $installerScript)
    $filesSectionIndex = [Array]::IndexOf($scriptLines, '[Files]')
    if ($filesSectionIndex -lt 0) {
        throw 'The Inno Setup script is missing its [Files] section.'
    }
    $actualEntries = [Collections.Generic.List[string]]::new()
    for ($index = $filesSectionIndex + 1; $index -lt $scriptLines.Count; $index++) {
        $line = $scriptLines[$index].Trim()
        if ($line.StartsWith('[', [StringComparison]::Ordinal)) {
            break
        }
        if ($line.Length -gt 0 -and -not $line.StartsWith(';', [StringComparison]::Ordinal)) {
            $actualEntries.Add($line)
        }
    }
    $expectedEntries = @(
        'Source: "{#PackageDir}\ClipsToDiscord.exe"; DestDir: "{app}"; Flags: ignoreversion',
        'Source: "{#PackageDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion',
        'Source: "{#PackageDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion',
        'Source: "{#PackageDir}\models\modnet-photographic.onnx"; DestDir: "{app}\models"; Flags: ignoreversion',
        'Source: "{#PackageDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion',
        'Source: "{#PackageDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist',
        'Source: "{#PackageDir}\FFMPEG-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist'
    )
    if ($actualEntries.Count -ne $expectedEntries.Count) {
        throw 'The Inno Setup [Files] section contains missing or unexpected payload entries.'
    }
    for ($index = 0; $index -lt $expectedEntries.Count; $index++) {
        if ($actualEntries[$index] -cne $expectedEntries[$index]) {
            throw "The Inno Setup [Files] entry at position $($index + 1) is not the expected exact payload mapping."
        }
    }
}

$modelSource = Get-OrdinaryFile $modelSourcePath 'The MODNet model'
$noticesSource = Get-OrdinaryFile $noticesSourcePath 'The third-party notices file'
$licenseSources = @(Get-RepositoryLicenseFiles)

[void](Get-OrdinaryFile $applicationIconPath 'The application icon')
Assert-InstallerPayloadEntries

if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $artifactsDirectory 'ClipCord-win-x64'
}
if (-not $OutputDirectory) {
    $OutputDirectory = $artifactsDirectory
}
if (-not $Version) {
    $Version = & (Join-Path $PSScriptRoot 'resolve-package-version.ps1') `
        -ProjectPath (Join-Path $repositoryRoot 'ClipsToDiscord.csproj') `
        -Format Core
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

$allowedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
    'models',
    'licenses',
    'ffmpeg.exe',
    'FFMPEG-LICENSE.txt'
) |
    ForEach-Object { [void]$allowedNames.Add($_) }
$packageItems = @(Get-ChildItem -LiteralPath $PackageDirectory -Force)
$unexpectedItems = @($packageItems | Where-Object {
    -not $allowedNames.Contains($_.Name) -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
    ($_.PSIsContainer -and $_.Name -notin @('models', 'licenses')) -or
    (-not $_.PSIsContainer -and $_.Name -in @('models', 'licenses'))
})
if ($unexpectedItems.Count -gt 0) {
    $unexpectedNames = ($unexpectedItems | ForEach-Object Name | Sort-Object) -join ', '
    throw "The portable package contains unexpected or unsafe items: $unexpectedNames"
}

$requiredFiles = @('ClipsToDiscord.exe', 'README.txt', 'THIRD_PARTY_NOTICES.md')
foreach ($fileName in $requiredFiles) {
    $path = Join-Path $PackageDirectory $fileName
    [void](Get-OrdinaryFile $path 'The portable package payload')
}

$packageNotices = Get-OrdinaryFile `
    (Join-Path $PackageDirectory 'THIRD_PARTY_NOTICES.md') `
    'The portable third-party notices file'
if ($packageNotices.Length -ne $noticesSource.Length -or
    (Get-FileHash -LiteralPath $packageNotices.FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $noticesSource.FullName -Algorithm SHA256).Hash) {
    throw 'The portable third-party notices file does not match the repository source.'
}
Assert-ExactFileDirectory `
    (Join-Path $PackageDirectory 'models') `
    @($modelSource) `
    'The portable model directory'
Assert-ExactFileDirectory `
    (Join-Path $PackageDirectory 'licenses') `
    $licenseSources `
    'The portable license directory'

$ffmpegPath = Join-Path $PackageDirectory 'ffmpeg.exe'
$ffmpegLicensePath = Join-Path $PackageDirectory 'FFMPEG-LICENSE.txt'
$hasFfmpeg = Test-Path -LiteralPath $ffmpegPath -PathType Leaf
$hasFfmpegLicense = Test-Path -LiteralPath $ffmpegLicensePath -PathType Leaf
if ($hasFfmpeg -ne $hasFfmpegLicense) {
    throw 'ffmpeg.exe and FFMPEG-LICENSE.txt must either both be present or both be absent.'
}
if ($hasFfmpeg) {
    [void](Get-OrdinaryFile $ffmpegPath 'The portable FFmpeg executable')
    [void](Get-OrdinaryFile $ffmpegLicensePath 'The portable FFmpeg license')
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
