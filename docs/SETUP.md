# Setup guide

## Requirements

- Windows 10 or Windows 11 on a 64-bit PC
- SteelSeries GG with Moments enabled
- Discord desktop installed
- Permission to create or manage a webhook in the destination Discord server

The release is self-contained. Friends do not need ChatGPT, PowerShell, .NET, or a separate FFmpeg installation.

## 1. Download and extract the app

1. Open the repository's [latest release](https://github.com/malikpervez/moments-to-discord/releases/latest).
2. Download `MomentsToDiscord-win-x64.zip`.
3. Extract the ZIP into a permanent folder, such as `Documents\MomentsToDiscord`.
4. Keep `MomentsToDiscord.exe`, `ffmpeg.exe`, and `FFMPEG-LICENSE.txt` together.
5. Run `MomentsToDiscord.exe`.

The release is not code-signed. Windows SmartScreen may display an unrecognized-app warning. Verify that the download came from this repository's Releases page before choosing **More info → Run anyway**.

## 2. Find the SteelSeries Moments folder

SteelSeries documents the current location in **GG → Settings → Moments → Clip Settings**. Scroll to the clips-folder location, then select that same folder in Moments to Discord.

See SteelSeries' official guide: [I can't find my Moments clips](https://support.steelseries.com/hc/en-us/articles/38840490424205-I-can-t-find-my-moments-clips).

Choose the folder containing newly exported `.mp4` clips, not its future `uploaded` subfolder.

## 3. Create the Discord webhook

1. In Discord, open the destination server's **Server Settings**.
2. Open **Integrations**, then **Webhooks**.
3. Create a webhook, choose the channel that should receive clips, and copy its URL.
4. Paste the URL into Moments to Discord.
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
- Discord confirms the upload → the original moves into `uploaded`.
- Discord closes → the clip watcher stops.
- Discord reopens → watching resumes automatically.

Right-click the notification-area icon to view status, reopen settings, open the clips folder, or exit.

## Updating

Exit the app from its notification-area menu, replace the old application files with the newer release files, and reopen `MomentsToDiscord.exe`. Saved settings and upload state remain under `%LOCALAPPDATA%\MomentsToDiscord`.
