param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$PreviousInstallerPath,
    [string]$ExpectedVersion = '1.6.0',
    [string]$PreviousVersion = '1.3.5'
)

$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath($InstallerPath)
$previousInstaller = [IO.Path]::GetFullPath($PreviousInstallerPath)
foreach ($path in @($installer, $previousInstaller)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer was not found: $path"
    }
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\ClipsToDiscord'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'ClipsToDiscord'
$dataSentinel = Join-Path $dataDirectory "installer-preservation-$([Guid]::NewGuid().ToString('N')).txt"
$legacyStartMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Clips to Discord.lnk'
$legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Clips to Discord.lnk'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ClipCord.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ClipCord.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$mutexReady = Join-Path ([IO.Path]::GetTempPath()) "ClipsToDiscordMutex-$([Guid]::NewGuid().ToString('N')).ready"
$mutexHolder = $null
$inAppRestartProcess = $null

function Get-UninstallEntries {
    @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue |
        Where-Object DisplayName -in @('Clips to Discord', 'ClipCord'))
}

function Invoke-Installer {
    param([Parameter(Mandatory = $true)][string]$Path)

    $process = Start-Process `
        -FilePath $Path `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    $process.ExitCode
}

function Get-DataSnapshot {
    if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
        return @()
    }

    $root = [IO.Path]::GetFullPath($dataDirectory).TrimEnd('\')
    @(Get-ChildItem -LiteralPath $root -Force -Recurse |
        Where-Object FullName -ne $dataSentinel |
        Sort-Object { $_.FullName.Substring($root.Length + 1) } |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($_.PSIsContainer) {
                "D|$relativePath"
            }
            else {
                $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                "F|$relativePath|$($_.Length)|$fileHash"
            }
        })
}

function Assert-InstalledVersion {
    param(
        [Parameter(Mandatory = $true)][string]$SetupVersion,
        [Parameter(Mandatory = $true)][string]$ExecutableVersion,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $installedExe = Join-Path $installDirectory 'ClipsToDiscord.exe'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "Installed executable was not found: $installedExe"
    }
    $fileVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    if ($fileVersion -ne "$ExecutableVersion.0") {
        throw "Installed executable version was $fileVersion instead of $ExecutableVersion.0."
    }
    $entries = @(Get-UninstallEntries)
    if ($entries.Count -ne 1) {
        throw "Expected one uninstall entry, found $($entries.Count)."
    }
    if ($entries[0].DisplayVersion -ne $SetupVersion) {
        throw "Uninstall entry version was $($entries[0].DisplayVersion) instead of $SetupVersion."
    }
    if ($entries[0].DisplayName -ne $DisplayName) {
        throw "Uninstall entry name was $($entries[0].DisplayName) instead of $DisplayName."
    }
    if ([IO.Path]::GetFullPath($entries[0].InstallLocation).TrimEnd('\') -ne
        [IO.Path]::GetFullPath($installDirectory).TrimEnd('\')) {
        throw "Unexpected default installation directory: $($entries[0].InstallLocation)"
    }
}

if (Get-Process -Name 'ClipsToDiscord' -ErrorAction SilentlyContinue) {
    throw 'A ClipsToDiscord process is already running.'
}
foreach ($path in @(
    $installDirectory,
    $legacyStartMenuShortcut,
    $legacyDesktopShortcut,
    $startMenuShortcut,
    $desktopShortcut)) {
    if (Test-Path -LiteralPath $path) {
        throw "Installer smoke test requires a clean runner; found: $path"
    }
}
if (Test-Path -LiteralPath $dataDirectory -PathType Leaf) {
    throw "Application data path is not a directory: $dataDirectory"
}
if ((Get-UninstallEntries).Count -ne 0) {
    throw 'Installer smoke test requires no existing Clips to Discord installation.'
}
$existingRunValue = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).ClipsToDiscord
if ($null -ne $existingRunValue) {
    throw 'Installer smoke test requires no existing ClipsToDiscord startup value.'
}
$dataDirectoryExisted = Test-Path -LiteralPath $dataDirectory -PathType Container
$baselineData = @(Get-DataSnapshot)

try {
    [IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
    [IO.File]::WriteAllText($dataSentinel, 'preserve this data')

    $previousExitCode = Invoke-Installer -Path $previousInstaller
    if ($previousExitCode -ne 0) {
        throw "Previous-version installer exited with code $previousExitCode."
    }
    Assert-InstalledVersion `
        -SetupVersion $PreviousVersion `
        -ExecutableVersion $PreviousVersion `
        -DisplayName 'Clips to Discord'
    if (-not (Test-Path -LiteralPath $legacyStartMenuShortcut -PathType Leaf)) {
        throw "Legacy Start Menu shortcut was not created: $legacyStartMenuShortcut"
    }
    if (Test-Path -LiteralPath $legacyDesktopShortcut) {
        throw 'The unchecked desktop shortcut task was unexpectedly enabled.'
    }
    if ($null -ne (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).ClipsToDiscord) {
        throw 'Installation unexpectedly created the Start with Windows value.'
    }

    $upgradeExitCode = Invoke-Installer -Path $installer
    if ($upgradeExitCode -ne 0) {
        throw "Upgrade installer exited with code $upgradeExitCode."
    }
    Assert-InstalledVersion `
        -SetupVersion $ExpectedVersion `
        -ExecutableVersion $ExpectedVersion `
        -DisplayName 'ClipCord'
    if (-not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
        throw 'Upgrade removed the application-data sentinel.'
    }
    if (-not (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf)) {
        throw "ClipCord Start Menu shortcut was not created: $startMenuShortcut"
    }
    if (Test-Path -LiteralPath $legacyStartMenuShortcut) {
        throw 'Upgrade left the legacy Start Menu shortcut behind.'
    }

    $escapedReadyPath = $mutexReady.Replace("'", "''")
    $holderScript = @"
`$mutex = [Threading.Mutex]::new(`$true, 'Local\ClipsToDiscord_Application')
[IO.File]::WriteAllText('$escapedReadyPath', 'ready')
try { Start-Sleep -Seconds 120 } finally { `$mutex.Dispose() }
"@
    $encodedHolderScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holderScript))
    $mutexHolder = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-EncodedCommand', $encodedHolderScript) `
        -PassThru `
        -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $mutexReady -PathType Leaf) -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $mutexReady -PathType Leaf)) {
        throw 'Mutex holder did not start in time.'
    }

    $blockedExitCode = Invoke-Installer -Path $installer
    if ($blockedExitCode -eq 0) {
        throw 'Silent installation unexpectedly succeeded while the application mutex was held.'
    }
    Assert-InstalledVersion `
        -SetupVersion $ExpectedVersion `
        -ExecutableVersion $ExpectedVersion `
        -DisplayName 'ClipCord'

    if ($null -ne $mutexHolder -and -not $mutexHolder.HasExited) {
        Stop-Process -Id $mutexHolder.Id -Force
        $mutexHolder.WaitForExit()
    }
    $mutexHolder = $null

    if (Get-Process -Name 'ClipsToDiscord' -ErrorAction SilentlyContinue) {
        throw 'Ordinary silent setup unexpectedly launched ClipCord.'
    }
    $installedExe = Join-Path $installDirectory 'ClipsToDiscord.exe'
    if (-not (Test-Path -LiteralPath $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    $portableStartupPath = Join-Path $env:TEMP 'PortableClipCord\ClipsToDiscord.exe'
    New-ItemProperty `
        -Path $runKey `
        -Name 'ClipsToDiscord' `
        -Value "`"$portableStartupPath`"" `
        -PropertyType String `
        -Force | Out-Null
    $inAppUpdateProcess = Start-Process `
        -FilePath $installer `
        -ArgumentList @(
            '/SILENT',
            '/NORESTART',
            '/CLOSEAPPLICATIONS',
            '/CLIPCORDRESTART=1') `
        -PassThru `
        -WindowStyle Hidden
    if (-not $inAppUpdateProcess.WaitForExit(120000)) {
        Stop-Process -Id $inAppUpdateProcess.Id -Force
        $inAppUpdateProcess.WaitForExit()
        throw 'In-app update simulation exceeded its two-minute deadline.'
    }
    if ($inAppUpdateProcess.ExitCode -ne 0) {
        throw "In-app update simulation exited with code $($inAppUpdateProcess.ExitCode)."
    }
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $restartedApps = @(Get-Process -Name 'ClipsToDiscord' -ErrorAction SilentlyContinue)
        if ($restartedApps.Count -eq 1) {
            $inAppRestartProcess = $restartedApps[0]
            break
        }
        if ($restartedApps.Count -gt 1) {
            throw "In-app update launched $($restartedApps.Count) ClipCord processes instead of one."
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $restartDeadline)
    if ($null -eq $inAppRestartProcess) {
        throw 'The dedicated in-app update parameter did not reopen ClipCord.'
    }
    Stop-Process -Id $inAppRestartProcess.Id -Force
    $inAppRestartProcess.WaitForExit()
    $inAppRestartProcess = $null
    $updatedRunValue = (Get-ItemProperty -Path $runKey -ErrorAction Stop).ClipsToDiscord
    if ($updatedRunValue -ne "`"$installedExe`"") {
        throw "In-app update left Start with Windows pointing at '$updatedRunValue'."
    }

    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    $uninstallProcess = Start-Process `
        -FilePath $uninstaller `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($uninstallProcess.ExitCode -ne 0) {
        throw "Uninstaller exited with code $($uninstallProcess.ExitCode)."
    }

    if (Test-Path -LiteralPath $installDirectory) {
        throw "Uninstall left the installation directory behind: $installDirectory"
    }
    if (Test-Path -LiteralPath $startMenuShortcut) {
        throw 'Uninstall left the Start Menu shortcut behind.'
    }
    if ($null -ne (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).ClipsToDiscord) {
        throw 'Uninstall left the ClipsToDiscord startup entry behind.'
    }
    if ((Get-UninstallEntries).Count -ne 0) {
        throw 'Uninstall left its Installed Apps entry behind.'
    }
    if (-not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
        throw 'Uninstall removed the application-data sentinel.'
    }
    $dataAfterUninstall = @(Get-DataSnapshot)
    $dataDifference = @(Compare-Object -ReferenceObject $baselineData -DifferenceObject $dataAfterUninstall)
    if ($dataDifference.Count -ne 0) {
        throw 'Uninstall changed pre-existing application data.'
    }
}
finally {
    if ($null -ne $inAppRestartProcess -and -not $inAppRestartProcess.HasExited) {
        Stop-Process -Id $inAppRestartProcess.Id -Force
        $inAppRestartProcess.WaitForExit()
    }
    if ($null -ne $mutexHolder -and -not $mutexHolder.HasExited) {
        Stop-Process -Id $mutexHolder.Id -Force
        $mutexHolder.WaitForExit()
    }
    if (Test-Path -LiteralPath $mutexReady) {
        Remove-Item -LiteralPath $mutexReady -Force
    }
    if (Test-Path -LiteralPath $dataSentinel) {
        Remove-Item -LiteralPath $dataSentinel -Force
    }
    if (-not $dataDirectoryExisted -and
        (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
        $remainingData = @(Get-ChildItem -LiteralPath $dataDirectory -Force)
        if ($remainingData.Count -eq 0) {
            Remove-Item -LiteralPath $dataDirectory -Force
        }
    }
}

Write-Output 'Installer integration tests passed.'
