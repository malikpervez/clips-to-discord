# Clips to Discord

[![Build and test](https://github.com/malikpervez/clips-to-discord/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/malikpervez/clips-to-discord/actions/workflows/build-and-test.yml)

A Windows tray app that uploads new MP4 clips from any chosen folder to a Discord channel through a webhook.

[Download the latest release](https://github.com/malikpervez/clips-to-discord/releases/latest) · [Setup guide](docs/SETUP.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Privacy and security](docs/PRIVACY.md)

## What it does

- Works with any recorder, clipping tool, replay buffer, editor, or export workflow that saves `.mp4` files into a folder.
- Asks for only the clips folder and a Discord webhook URL.
- Encrypts the webhook for the current Windows account using Windows DPAPI.
- Starts the folder watcher when Discord opens and stops it when Discord closes.
- Uploads only new `.mp4` files from the top level of the selected folder.
- Confirms file length and timestamp stability across multiple observations before queueing a clip.
- Uses SHA-256 content identity so folder renames and timestamp rewrites do not cause duplicate uploads.
- Keeps scanning with two upload workers, a short connection timeout, and a bounded per-upload deadline.
- Moves successfully uploaded originals into an `uploaded` subfolder, creating it when needed and recognizing any capitalization.
- Preserves duplicate filenames by adding a unique suffix.
- Uses a configurable compression target and retries smaller targets when Discord rejects a file for size.
- Redacts Discord webhook URLs from all application log messages.
- Can start automatically when the user signs into Windows.

The app is not affiliated with Discord or any recording-software vendor.

## Quick setup (recommended installer)

1. Download `ClipsToDiscord-Setup.exe` from the [latest release](https://github.com/malikpervez/clips-to-discord/releases/latest).
2. Run the installer normally from Downloads, Desktop, or anywhere else. Do not use **Run as administrator**.
3. The installer places the app under `%LOCALAPPDATA%\Programs\ClipsToDiscord`, creates Start Menu and uninstall entries, and launches the installed copy.
4. Select the folder where your clipping or recording tool saves finished MP4 clips.
5. Paste the Discord webhook URL and optionally click **Test webhook**.
6. Click **Save**.

Each release includes `SHA256SUMS.txt` so the installer or portable ZIP can be verified with `Get-FileHash` before it is opened.

The app then lives in the Windows notification area. Existing clips are treated as a baseline on first setup and are not uploaded. New clips are uploaded while Discord desktop is running.

The installer and executable are not code-signed, so Windows SmartScreen may show an unrecognized-app warning. Anyone distributing the app broadly should sign both with a trusted code-signing certificate.

See the [complete setup guide](docs/SETUP.md) for folder-selection and webhook instructions.

### Portable ZIP

`ClipsToDiscord-win-x64.zip` remains available for users who prefer a portable copy. Extract all files together and keep that folder in place if **Start with Windows** is enabled.

## Documentation

- [Installation and first-run setup](docs/SETUP.md)
- [Creating and protecting a Discord webhook](docs/DISCORD-WEBHOOK.md)
- [Troubleshooting and log locations](docs/TROUBLESHOOTING.md)
- [Privacy and security model](docs/PRIVACY.md)
- [Architecture and upload lifecycle](docs/ARCHITECTURE.md)
- [Reliability design and tradeoffs](docs/RELIABILITY.md)
- [Building and contributing](CONTRIBUTING.md)
- [Version history](CHANGELOG.md)

## Webhook safety

A Discord webhook URL grants permission to post to its channel. Never commit one to source control, screenshots, logs, or issues. If a URL is exposed, delete or rotate the webhook in Discord immediately.

The saved URL is encrypted with DPAPI and can only be decrypted by the same Windows user account on the same Windows installation.

Application logging also runs through a centralized webhook redactor. This is defense in depth and does not replace rotating a webhook that has been exposed elsewhere.

## Build from source

Requirements:

- Windows 10 or 11
- .NET 8 SDK or newer

```powershell
dotnet build .\ClipsToDiscord.csproj -c Release
```

Create a self-contained package without FFmpeg:

```powershell
.\scripts\package.ps1
```

Create the per-user installer after preparing the portable layout:

```powershell
.\scripts\build-installer.ps1
```

Create a package with FFmpeg compression support:

```powershell
.\scripts\package.ps1 `
  -FfmpegPath 'C:\path\to\ffmpeg.exe' `
  -FfmpegLicensePath 'C:\path\to\FFmpeg\LICENSE'

.\scripts\build-installer.ps1 -RequireFfmpeg
```

FFmpeg binaries are intentionally not committed to this repository. FFmpeg and its license must be supplied together, and release installer builds should use `-RequireFfmpeg`. If you distribute an FFmpeg binary, comply with that build's license terms.

## Local data

Settings, state, and logs are stored in:

```text
%LOCALAPPDATA%\ClipsToDiscord
```

Version 1.1 automatically copies compatible settings and state from the legacy `%LOCALAPPDATA%\MomentsToDiscord` folder when needed.

The Windows startup entry is stored under the current user's standard `Run` registry key.

## License

The application source is licensed under the MIT License. Bundled FFmpeg builds are governed by their own license; see `THIRD_PARTY_NOTICES.md`.
