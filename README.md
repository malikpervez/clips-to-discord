# Clips to Discord

A Windows tray app that uploads new MP4 clips from any chosen folder to a Discord channel through a webhook.

[Download the latest release](https://github.com/malikpervez/clips-to-discord/releases/latest) · [Setup guide](docs/SETUP.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Privacy and security](docs/PRIVACY.md)

## What it does

- Works with any recorder, clipping tool, replay buffer, editor, or export workflow that saves `.mp4` files into a folder.
- Asks for only the clips folder and a Discord webhook URL.
- Encrypts the webhook for the current Windows account using Windows DPAPI.
- Starts the folder watcher when Discord opens and stops it when Discord closes.
- Uploads only new `.mp4` files from the top level of the selected folder.
- Moves successfully uploaded originals into an `uploaded` subfolder, creating it when needed and recognizing any capitalization.
- Preserves duplicate filenames by adding a unique suffix.
- Compresses clips rejected for size when `ffmpeg.exe` is bundled beside the app or available on `PATH`.
- Can start automatically when the user signs into Windows.

The app is not affiliated with Discord or any recording-software vendor.

## Quick setup

1. Download `ClipsToDiscord-win-x64.zip` from the [latest release](https://github.com/malikpervez/clips-to-discord/releases/latest).
2. Extract the entire folder somewhere permanent.
3. Run `ClipsToDiscord.exe`.
4. Select the folder where your clipping or recording tool saves finished MP4 clips.
5. Paste the Discord webhook URL and optionally click **Test webhook**.
6. Click **Save**.

Each release includes `SHA256SUMS.txt` so the downloaded ZIP can be verified with `Get-FileHash` before it is opened.

The app then lives in the Windows notification area. Existing clips are treated as a baseline on first setup and are not uploaded. New clips are uploaded while Discord desktop is running.

The executable is not code-signed, so Windows SmartScreen may show an unrecognized-app warning. Anyone distributing the app broadly should sign it with a trusted code-signing certificate.

See the [complete setup guide](docs/SETUP.md) for folder-selection and webhook instructions.

## Documentation

- [Installation and first-run setup](docs/SETUP.md)
- [Creating and protecting a Discord webhook](docs/DISCORD-WEBHOOK.md)
- [Troubleshooting and log locations](docs/TROUBLESHOOTING.md)
- [Privacy and security model](docs/PRIVACY.md)
- [Architecture and upload lifecycle](docs/ARCHITECTURE.md)
- [Building and contributing](CONTRIBUTING.md)
- [Version history](CHANGELOG.md)

## Webhook safety

A Discord webhook URL grants permission to post to its channel. Never commit one to source control, screenshots, logs, or issues. If a URL is exposed, delete or rotate the webhook in Discord immediately.

The saved URL is encrypted with DPAPI and can only be decrypted by the same Windows user account on the same Windows installation.

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

Create a package with FFmpeg compression support:

```powershell
.\scripts\package.ps1 `
  -FfmpegPath 'C:\path\to\ffmpeg.exe' `
  -FfmpegLicensePath 'C:\path\to\FFmpeg\LICENSE'
```

FFmpeg binaries are intentionally not committed to this repository. If you distribute an FFmpeg binary, include its corresponding license and comply with that build's license terms.

## Local data

Settings, state, and logs are stored in:

```text
%LOCALAPPDATA%\ClipsToDiscord
```

Version 1.1 automatically copies compatible settings and state from the legacy `%LOCALAPPDATA%\MomentsToDiscord` folder when needed.

The Windows startup entry is stored under the current user's standard `Run` registry key.

## License

The application source is licensed under the MIT License. Bundled FFmpeg builds are governed by their own license; see `THIRD_PARTY_NOTICES.md`.
