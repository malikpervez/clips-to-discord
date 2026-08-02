# Setup guide

## Requirements

- Windows 10 or Windows 11 on a 64-bit PC
- A clipping, recording, replay-buffer, or export tool that saves finished `.mp4` files into a folder
- Discord desktop installed
- Permission to create or manage a webhook in the destination Discord server

The release is self-contained. Friends do not need ChatGPT, PowerShell, .NET, or a separate FFmpeg installation.

## 1. Download and extract the app

1. Open the repository's [latest release](https://github.com/malikpervez/clips-to-discord/releases/latest).
2. Download `ClipsToDiscord-win-x64.zip`.
3. Extract the ZIP into a permanent folder, such as `Documents\ClipsToDiscord`.
4. Keep `ClipsToDiscord.exe`, `ffmpeg.exe`, and `FFMPEG-LICENSE.txt` together.
5. Run `ClipsToDiscord.exe`.

The release is not code-signed. Windows SmartScreen may display an unrecognized-app warning. Verify that the download came from this repository's Releases page before choosing **More info → Run anyway**.

## 2. Choose the clips folder

Open the settings for your clipping or recording tool and find its save, export, recordings, highlights, or replay folder. If the location is unclear, create a short test clip and use File Explorer to find the resulting `.mp4`.

Select the folder that directly receives new MP4 files. Do not select the `uploaded` subfolder that Clips to Discord creates.

The app watches only the top level of the selected directory. It does not scan nested folders, and it ignores formats other than `.mp4`.

## 3. Create the Discord webhook

1. In Discord, open the destination server's **Server Settings**.
2. Open **Integrations**, then **Webhooks**.
3. Create a webhook, choose the channel that should receive clips, and copy its URL.
4. Paste the URL into Clips to Discord.
5. Select **Test webhook** and confirm the test message appears in the channel.

Discord's official guides: [Intro to Webhooks](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks) and [Server Integrations Page](https://support.discord.com/hc/en-us/articles/360045093012-Server-Integrations-Page).

Treat the webhook URL like a password. Anyone who has it can post through that webhook.

## 4. Save and use the app

1. Leave **Start with Windows** selected if the app should be available after each sign-in.
2. Select **Save**.
3. The app moves to the Windows notification area.

The first scan records existing clips as a baseline and does not upload them. After setup:

- Discord opens → the clip watcher starts.
- A new `.mp4` finishes writing → the app uploads it.
- Discord confirms the upload → the original moves into an existing case-insensitive `uploaded` folder, or the app creates `uploaded` automatically.
- Discord closes → the clip watcher stops.
- Discord reopens → watching resumes automatically.

Right-click the notification-area icon to view status, reopen settings, open the clips folder, or exit.

## Updating from version 1.0

Exit the old app from its notification-area menu, extract the new release into a permanent folder, and run `ClipsToDiscord.exe`. Version 1.1 copies compatible settings and upload state from `%LOCALAPPDATA%\MomentsToDiscord` into `%LOCALAPPDATA%\ClipsToDiscord` when the new location is empty.

## Updating later releases

Exit the app from its notification-area menu, replace the application files with the newer release files, and reopen `ClipsToDiscord.exe`. Saved settings and upload state remain under `%LOCALAPPDATA%\ClipsToDiscord`.
