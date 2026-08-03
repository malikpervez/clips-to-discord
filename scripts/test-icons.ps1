param(
    [Parameter(Mandatory = $true)][string]$ApplicationPath,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$IconPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Assert-BrandedIcon {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Label was not found: $resolvedPath"
    }

    $icon = [Drawing.Icon]::ExtractAssociatedIcon($resolvedPath)
    if ($null -eq $icon) {
        throw "$Label does not contain an associated icon."
    }

    $bitmap = $icon.ToBitmap()
    try {
        $coralPixels = 0
        $violetPixels = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -ge 180 -and $pixel.G -le 170 -and $pixel.B -le 180) {
                    $coralPixels++
                }
                if ($pixel.R -ge 80 -and $pixel.R -le 200 -and $pixel.G -le 140 -and $pixel.B -ge 160) {
                    $violetPixels++
                }
            }
        }

        if ($coralPixels -lt 20 -or $violetPixels -lt 20) {
            throw "$Label does not contain the expected branded icon colors (coral=$coralPixels, violet=$violetPixels)."
        }

        Write-Output "$Label contains the branded icon (coral=$coralPixels, violet=$violetPixels)."
    }
    finally {
        $bitmap.Dispose()
        $icon.Dispose()
    }
}

Assert-BrandedIcon -Path $ApplicationPath -Label 'Application executable'
Assert-BrandedIcon -Path $InstallerPath -Label 'Installer executable'

$resolvedIconPath = [IO.Path]::GetFullPath($IconPath)
$iconBytes = [IO.File]::ReadAllBytes($resolvedIconPath)
if ($iconBytes.Length -lt 6 -or
    [BitConverter]::ToUInt16($iconBytes, 0) -ne 0 -or
    [BitConverter]::ToUInt16($iconBytes, 2) -ne 1) {
    throw "The icon has an invalid ICO header: $resolvedIconPath"
}

$entryCount = [BitConverter]::ToUInt16($iconBytes, 4)
if ($iconBytes.Length -lt 6 + ($entryCount * 16)) {
    throw "The icon directory is truncated: $resolvedIconPath"
}

$availableSizes = [Collections.Generic.HashSet[int]]::new()
for ($index = 0; $index -lt $entryCount; $index++) {
    $entryOffset = 6 + ($index * 16)
    $width = if ($iconBytes[$entryOffset] -eq 0) { 256 } else { [int]$iconBytes[$entryOffset] }
    $height = if ($iconBytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$iconBytes[$entryOffset + 1] }
    if ($width -ne $height) {
        throw "The icon contains a non-square frame: ${width}x${height}."
    }
    [void]$availableSizes.Add($width)
}

$requiredSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$missingSizes = @($requiredSizes | Where-Object { -not $availableSizes.Contains($_) })
if ($missingSizes.Count -gt 0) {
    throw "The icon is missing required frame sizes: $($missingSizes -join ', ')."
}
Write-Output "ICO contains all required frame sizes: $($requiredSizes -join ', ')."
