param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$ExpectedVersion,
    [switch]$RequireFfmpeg
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$modelSourcePath = Join-Path $repositoryRoot 'assets\models\modnet-photographic.onnx'
$noticesSourcePath = Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md'
$licensesSourceDirectory = Join-Path $repositoryRoot 'licenses'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$unpackDirectory = Join-Path $artifactsDirectory 'store-msix-test-unpacked'

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

$modelSource = Get-OrdinaryFile $modelSourcePath 'The repository MODNet model'
$noticesSource = Get-OrdinaryFile $noticesSourcePath 'The repository third-party notices file'
$licenseSources = @(Get-RepositoryLicenseFiles)

[void](Get-OrdinaryFile $PackagePath 'The MSIX package')
if ($ExpectedVersion -and $ExpectedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "ExpectedVersion must use four numeric parts: $ExpectedVersion"
}

$resolvedArtifacts = [IO.Path]::GetFullPath($artifactsDirectory).TrimEnd('\', '/')
$resolvedUnpack = [IO.Path]::GetFullPath($unpackDirectory)
if (-not $resolvedUnpack.StartsWith(
        $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a path outside the artifacts directory: $resolvedUnpack"
}
if (Test-Path -LiteralPath $resolvedUnpack) {
    Remove-Item -LiteralPath $resolvedUnpack -Recurse -Force
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
    throw 'MakeAppx.exe was not found.'
}
& $makeAppxCandidates[0].FullName unpack /o /p ([IO.Path]::GetFullPath($PackagePath)) /d $resolvedUnpack
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx validation failed with exit code $LASTEXITCODE"
}

$manifestPath = Join-Path $resolvedUnpack 'AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath $manifestPath
$namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$namespace.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
$namespace.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
$namespace.AddNamespace('uap11', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/11')
$namespace.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespace)
if ($identity.Name -ne 'DKGLabs.ClipCord') {
    throw "Unexpected Store identity: $($identity.Name)"
}
if ($identity.Publisher -ne 'CN=3BF1D083-8330-4BB1-A011-C31DD2E3487F') {
    throw "Unexpected Store publisher: $($identity.Publisher)"
}
if ($ExpectedVersion -and $identity.Version -ne $ExpectedVersion) {
    throw "Expected MSIX version $ExpectedVersion, found $($identity.Version)."
}
if ($identity.ProcessorArchitecture -ne 'x64') {
    throw "Expected an x64 package, found $($identity.ProcessorArchitecture)."
}

$application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
if ($application.Executable -ne 'ClipsToDiscord.exe' -or
    $application.GetAttribute('RuntimeBehavior', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -ne 'packagedClassicApp' -or
    $application.GetAttribute('TrustLevel', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10') -ne 'mediumIL') {
    throw 'The package must launch ClipCord as a medium-integrity packaged classic app.'
}
$startupTask = $manifest.SelectSingleNode('//uap5:StartupTask', $namespace)
if ($startupTask.TaskId -ne 'ClipCordStartup' -or $startupTask.Enabled -ne 'false') {
    throw 'The packaged startup task declaration is missing or unexpectedly enabled by default.'
}
if (-not $manifest.SelectSingleNode('/f:Package/f:Capabilities/rescap:Capability[@Name="runFullTrust"]', $namespace)) {
    throw 'The package is missing the runFullTrust capability required by ClipCord.'
}
if (-not $manifest.SelectSingleNode('/f:Package/f:Capabilities/uap11:Capability[@Name="graphicsCaptureWithoutBorder"]', $namespace)) {
    throw 'The package is missing the borderless graphics capture capability required by ClipCord 2.0.'
}
$webcamCapabilities = @($manifest.SelectNodes(
    '/f:Package/f:Capabilities/f:DeviceCapability[@Name="webcam"]',
    $namespace))
if ($webcamCapabilities.Count -ne 1) {
    throw "The package must declare the webcam device capability exactly once; found $($webcamCapabilities.Count)."
}
$capabilityChildren = @($manifest.SelectSingleNode('/f:Package/f:Capabilities', $namespace).ChildNodes |
    Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
$runFullTrustIndex = [Array]::FindIndex(
    $capabilityChildren,
    [Predicate[object]] { param($node) $node.LocalName -eq 'Capability' -and $node.NamespaceURI -eq $namespace.LookupNamespace('rescap') -and $node.GetAttribute('Name') -eq 'runFullTrust' })
$webcamIndex = [Array]::FindIndex(
    $capabilityChildren,
    [Predicate[object]] { param($node) $node.LocalName -eq 'DeviceCapability' -and $node.GetAttribute('Name') -eq 'webcam' })
if ($runFullTrustIndex -lt 0 -or $webcamIndex -lt 0 -or $runFullTrustIndex -gt $webcamIndex) {
    throw 'Restricted capabilities must appear before the webcam device capability in the package manifest.'
}

$requiredFiles = @(
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
    'models\modnet-photographic.onnx',
    'Assets\StoreLogo.png',
    'Assets\Square44x44Logo.png',
    'Assets\Square71x71Logo.png',
    'Assets\Square150x150Logo.png',
    'Assets\Square310x310Logo.png',
    'Assets\Wide310x150Logo.png'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedUnpack $relativePath) -PathType Leaf)) {
        throw "Required package payload is missing: $relativePath"
    }
}

$unsafeUnpackedEntries = @(Get-ChildItem -LiteralPath $resolvedUnpack -Force -Recurse | Where-Object {
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
})
if ($unsafeUnpackedEntries.Count -gt 0) {
    throw "The Store package contains reparse-point payload: $(($unsafeUnpackedEntries.FullName | Sort-Object) -join ', ')"
}
Assert-ExactFileDirectory `
    (Join-Path $resolvedUnpack 'models') `
    @($modelSource) `
    'The Store package model directory'
Assert-ExactFileDirectory `
    (Join-Path $resolvedUnpack 'licenses') `
    $licenseSources `
    'The Store package license directory'
$expectedAssetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    'Square44x44Logo.png',
    'StoreLogo.png',
    'Square71x71Logo.png',
    'Square150x150Logo.png',
    'Square310x310Logo.png',
    'Wide310x150Logo.png'
) | ForEach-Object { [void]$expectedAssetNames.Add($_) }
$assetItems = @(Get-ChildItem -LiteralPath (Join-Path $resolvedUnpack 'Assets') -Force)
$unexpectedAssets = @($assetItems | Where-Object {
    $_.PSIsContainer -or
    (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
    $_.Length -le 0 -or
    -not $expectedAssetNames.Contains($_.Name)
})
if ($unexpectedAssets.Count -gt 0 -or $assetItems.Count -ne $expectedAssetNames.Count) {
    throw 'The Store package Assets directory contains missing, unexpected, or unsafe entries.'
}
$unpackedNotices = Get-OrdinaryFile `
    (Join-Path $resolvedUnpack 'THIRD_PARTY_NOTICES.md') `
    'The Store package third-party notices file'
if ($unpackedNotices.Length -ne $noticesSource.Length -or
    (Get-FileHash -LiteralPath $unpackedNotices.FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $noticesSource.FullName -Algorithm SHA256).Hash) {
    throw 'The Store package third-party notices file does not match the repository source.'
}

$allowedTopLevelNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
@(
    '[Content_Types].xml',
    'AppxBlockMap.xml',
    'AppxManifest.xml',
    'AppxSignature.p7x',
    'AppxMetadata',
    'Assets',
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
    'ffmpeg.exe',
    'FFMPEG-LICENSE.txt',
    'models',
    'licenses'
) | ForEach-Object { [void]$allowedTopLevelNames.Add($_) }
$unexpectedTopLevel = @(Get-ChildItem -LiteralPath $resolvedUnpack -Force | Where-Object {
    -not $allowedTopLevelNames.Contains($_.Name) -or
    ($_.PSIsContainer -and $_.Name -notin @('AppxMetadata', 'Assets', 'models', 'licenses')) -or
    (-not $_.PSIsContainer -and $_.Name -in @('AppxMetadata', 'Assets', 'models', 'licenses'))
})
if ($unexpectedTopLevel.Count -gt 0) {
    throw "The Store package contains unexpected top-level payload: $(($unexpectedTopLevel.Name | Sort-Object) -join ', ')"
}

$ffmpegPath = Join-Path $resolvedUnpack 'ffmpeg.exe'
$ffmpegLicensePath = Join-Path $resolvedUnpack 'FFMPEG-LICENSE.txt'
$hasFfmpeg = Test-Path -LiteralPath $ffmpegPath -PathType Leaf
$hasFfmpegLicense = Test-Path -LiteralPath $ffmpegLicensePath -PathType Leaf
if ($hasFfmpeg -ne $hasFfmpegLicense) {
    throw 'ffmpeg.exe and FFMPEG-LICENSE.txt must either both be present or both be absent.'
}
if ($RequireFfmpeg -and -not $hasFfmpeg) {
    throw 'The Store package must contain ffmpeg.exe and FFMPEG-LICENSE.txt.'
}

$forbiddenNames = @(
    'settings.json',
    'capture-settings.json',
    'activity.json',
    'state.json',
    'updates.json',
    'app.log'
)
$forbidden = @(Get-ChildItem -LiteralPath $resolvedUnpack -File -Recurse | Where-Object {
    $forbiddenNames -contains $_.Name
})
if ($forbidden.Count -gt 0) {
    throw "Private runtime data leaked into the package: $(($forbidden.Name | Sort-Object -Unique) -join ', ')"
}

Write-Host "Store MSIX structure passed: $PackagePath"
