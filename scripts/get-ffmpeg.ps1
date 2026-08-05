param(
    [string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$version = '8.1.2'
$archiveName = "ffmpeg-$version-essentials_build.zip"
$downloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$expectedArchiveSha256 = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'
$fallbackArchiveName = 'ClipCord-win-x64-v1.4.0.zip'
$fallbackDownloadUrl = 'https://github.com/malikpervez/clips-to-discord/releases/download/v1.4.0/ClipCord-win-x64.zip'
$expectedFallbackArchiveSha256 = '84CFA2F6792C5A63CCABE5111075EBE5423914F8A90B373D60AA6E3364F35963'
$expectedFfmpegSha256 = '1326DDE4C84FF1F96FE6B8916C5BED29E163E9B5DCCF995F6F3DB069D143EC5E'
$expectedLicenseSha256 = '8CEB4B9EE5ADEDDE47B31E975C1D90C73AD27B6B165A1DCD80C7C545EB65B903'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolsDirectory = Join-Path $repositoryRoot 'artifacts\tools'
if (-not $DestinationDirectory) {
    $DestinationDirectory = Join-Path $toolsDirectory "FFmpeg-$version"
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
    $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "$Label integrity check failed. Expected $ExpectedSha256, got $actualSha256."
    }
}

function Assert-FfmpegBundle {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "FFmpeg bundle directory was not found: $Path"
    }
    $items = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($items.Count -ne 2 -or
        -not (Test-Path -LiteralPath (Join-Path $Path 'ffmpeg.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Path 'FFMPEG-LICENSE.txt') -PathType Leaf)) {
        throw 'FFmpeg bundle integrity check failed. Expected only ffmpeg.exe and FFMPEG-LICENSE.txt.'
    }
    Assert-FileHash `
        -Path (Join-Path $Path 'ffmpeg.exe') `
        -ExpectedSha256 $expectedFfmpegSha256 `
        -Label 'FFmpeg executable'
    Assert-FileHash `
        -Path (Join-Path $Path 'FFMPEG-LICENSE.txt') `
        -ExpectedSha256 $expectedLicenseSha256 `
        -Label 'FFmpeg license'
}

function Invoke-DownloadWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$MaximumAttempts = 4
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Invoke-WebRequest `
                -Uri $Uri `
                -OutFile $OutFile `
                -MaximumRedirection 5 `
                -UseBasicParsing
            return
        }
        catch {
            if ($attempt -eq $MaximumAttempts) {
                throw
            }
            if (Test-Path -LiteralPath $OutFile) {
                Remove-Item -LiteralPath $OutFile -Force
            }
            $delaySeconds = 10 * [Math]::Pow(2, $attempt - 1)
            Write-Warning "$Label download attempt $attempt of $MaximumAttempts failed. Retrying in $delaySeconds seconds."
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) {
    Assert-FfmpegBundle -Path $destination
    Write-Output $destination
    exit 0
}

[IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
$archivePath = Join-Path $toolsDirectory $archiveName
if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    Assert-FileHash -Path $archivePath -ExpectedSha256 $expectedArchiveSha256 -Label 'FFmpeg archive'
}
else {
    $partialPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
    try {
        $primaryAvailable = $true
        try {
            Invoke-DownloadWithRetry -Uri $downloadUrl -OutFile $partialPath -Label 'FFmpeg archive'
        }
        catch {
            $primaryAvailable = $false
            Write-Warning 'The pinned FFmpeg host is unavailable. Falling back to the pinned ClipCord v1.4.0 portable release.'
        }

        if ($primaryAvailable) {
            Assert-FileHash -Path $partialPath -ExpectedSha256 $expectedArchiveSha256 -Label 'Downloaded FFmpeg archive'
            Move-Item -LiteralPath $partialPath -Destination $archivePath
        }
        else {
            if (Test-Path -LiteralPath $partialPath) {
                Remove-Item -LiteralPath $partialPath -Force
            }
            $archivePath = Join-Path $toolsDirectory $fallbackArchiveName
            if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
                Assert-FileHash `
                    -Path $archivePath `
                    -ExpectedSha256 $expectedFallbackArchiveSha256 `
                    -Label 'ClipCord FFmpeg fallback archive'
            }
            else {
                $fallbackPartialPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
                try {
                    Invoke-DownloadWithRetry `
                        -Uri $fallbackDownloadUrl `
                        -OutFile $fallbackPartialPath `
                        -Label 'ClipCord FFmpeg fallback archive'
                    Assert-FileHash `
                        -Path $fallbackPartialPath `
                        -ExpectedSha256 $expectedFallbackArchiveSha256 `
                        -Label 'Downloaded ClipCord FFmpeg fallback archive'
                    Move-Item -LiteralPath $fallbackPartialPath -Destination $archivePath
                }
                finally {
                    if (Test-Path -LiteralPath $fallbackPartialPath) {
                        Remove-Item -LiteralPath $fallbackPartialPath -Force
                    }
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            Remove-Item -LiteralPath $partialPath -Force
        }
    }
}

$stagingDirectory = "$destination.$([Guid]::NewGuid().ToString('N')).staging"
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDirectory
    $ffmpegFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -Filter 'ffmpeg.exe' -File)
    $licenseFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File | Where-Object {
        $_.Name -in @('LICENSE', 'FFMPEG-LICENSE.txt')
    })
    if ($ffmpegFiles.Count -ne 1 -or $licenseFiles.Count -ne 1) {
        throw "FFmpeg archive layout was unexpected (executables=$($ffmpegFiles.Count), licenses=$($licenseFiles.Count))."
    }

    $bundleDirectory = Join-Path $stagingDirectory 'verified-bundle'
    [IO.Directory]::CreateDirectory($bundleDirectory) | Out-Null
    Copy-Item -LiteralPath $ffmpegFiles[0].FullName -Destination (Join-Path $bundleDirectory 'ffmpeg.exe')
    Copy-Item -LiteralPath $licenseFiles[0].FullName -Destination (Join-Path $bundleDirectory 'FFMPEG-LICENSE.txt')
    Assert-FfmpegBundle -Path $bundleDirectory
    Move-Item -LiteralPath $bundleDirectory -Destination $destination
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

Assert-FfmpegBundle -Path $destination
Write-Output $destination
