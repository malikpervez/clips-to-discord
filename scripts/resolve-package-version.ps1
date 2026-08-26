[CmdletBinding(DefaultParameterSetName = 'Project')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Value')]
    [string]$ProjectVersion,
    [Parameter(ParameterSetName = 'Project')]
    [string]$ProjectPath,
    [ValidateSet('Core', 'Store')]
    [string]$Format = 'Core'
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'Project') {
    if (-not $ProjectPath) {
        $ProjectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ClipsToDiscord.csproj'
    }
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "The project file was not found: $ProjectPath"
    }
    $projectItem = Get-Item -LiteralPath $ProjectPath -Force
    if (($projectItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $projectItem.Length -le 0) {
        throw "The project file must be a non-empty ordinary file: $ProjectPath"
    }

    [xml]$project = Get-Content -LiteralPath $projectItem.FullName
    $versionNodes = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($versionNodes.Count -ne 1) {
        throw "The project must declare exactly one Version value: $ProjectPath"
    }
    $ProjectVersion = [string]$versionNodes[0]
}

$projectVersionValue = $ProjectVersion.Trim()
$semanticVersionPattern = '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
$versionMatch = [regex]::Match(
    $projectVersionValue,
    $semanticVersionPattern,
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $versionMatch.Success) {
    throw "Project version must be three-part SemVer with optional prerelease or build metadata: $projectVersionValue"
}

$numericParts = [Collections.Generic.List[int]]::new(3)
foreach ($groupName in @('major', 'minor', 'patch')) {
    [long]$part = 0
    $partText = $versionMatch.Groups[$groupName].Value
    if (-not [long]::TryParse($partText, [ref]$part) -or $part -gt 65535) {
        throw "Each package version part must be between 0 and 65535: $projectVersionValue"
    }
    $numericParts.Add([int]$part)
}
if ($numericParts[0] -eq 0) {
    throw "The package major version must be greater than zero: $projectVersionValue"
}

$coreVersion = '{0}.{1}.{2}' -f $numericParts[0], $numericParts[1], $numericParts[2]
if ($Format -eq 'Store') {
    return "$coreVersion.0"
}
return $coreVersion
