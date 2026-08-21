param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$ExpectedVersion,
    [switch]$RequireFfmpeg
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$unpackDirectory = Join-Path $artifactsDirectory 'store-msix-test-unpacked'

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "MSIX package was not found: $PackagePath"
}
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

$requiredFiles = @(
    'ClipsToDiscord.exe',
    'README.txt',
    'THIRD_PARTY_NOTICES.md',
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

$forbiddenNames = @('settings.json', 'activity.json', 'state.json', 'updates.json', 'app.log')
$forbidden = @(Get-ChildItem -LiteralPath $resolvedUnpack -File -Recurse | Where-Object {
    $forbiddenNames -contains $_.Name
})
if ($forbidden.Count -gt 0) {
    throw "Private runtime data leaked into the package: $(($forbidden.Name | Sort-Object -Unique) -join ', ')"
}

Write-Host "Store MSIX structure passed: $PackagePath"
