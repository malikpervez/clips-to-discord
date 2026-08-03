# Setup guide

## Requirements

- Windows 10 or Windows 11 on a 64-bit PC
- A clipping, recording, replay-buffer, or export tool that saves finished `.mp4` files into a folder
- Discord desktop installed
- Permission to create or manage a webhook in the destination Discord server

The release is self-contained. Friends do not need ChatGPT, PowerShell, .NET, or a separate FFmpeg installation.

## 1. Install the app

1. Open the repository's [latest release](https://github.com/malikpervez/clips-to-discord/releases/latest).
2. Download `ClipsToDiscord-Setup.exe`.
3. Run the installer normally from Downloads, Desktop, or any other location. Do not choose **Run as administrator**.
4. Select **Install**. Administrator permission is not required.
5. Leave **Launch Clips to Discord** selected when setup completes.

The installer copies the application to `%LOCALAPPDATA%\Programs\ClipsToDiscord`, creates a Start Menu shortcut and an Add/Remove Programs entry, and optionally creates a desktop shortcut. Running it as another administrator account would install into that account's profile instead of yours. The downloaded setup file can be deleted after installation.

The portable `ClipsToDiscord-win-x64.zip` remains available. Portable users must extract every file together and keep the extracted folder in place while **Start with Windows** is enabled.

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
2. Set the compression target. The default 9 MB works with Discord's standard 10 MiB per-file limit while leaving headroom. Higher server limits can use a larger target.
3. Select **Save**.
4. The app moves to the Windows notification area.

The first scan records existing clips as a baseline and does not upload them. After setup:

- Discord opens → the clip watcher starts.
- A new `.mp4` remains unchanged across the stability window → the app hashes and queues it.
- Two upload workers process the queue independently.
- Discord confirms the upload → the original moves into an existing case-insensitive `uploaded` folder, or the app creates `uploaded` automatically.
- Discord closes → the clip watcher stops.
- Discord reopens → watching resumes automatically.

Right-click the notification-area icon to view status, reopen settings, open the clips folder, or exit.

Discord documents a default 10 MiB per-file limit, with potentially higher limits depending on the user or server. See Discord's official [Uploading Files reference](https://docs.discord.com/developers/reference#uploading-files). If Discord still rejects a compressed result, the app automatically tries progressively smaller targets.

## Updating from version 1.0

Exit the old app from its notification-area menu, download the current `ClipsToDiscord-Setup.exe`, and run it. The installed app copies compatible settings and upload state from `%LOCALAPPDATA%\MomentsToDiscord` into `%LOCALAPPDATA%\ClipsToDiscord` when the new location is empty.

## Updating later releases

Exit the app from its notification-area menu and run the newer `ClipsToDiscord-Setup.exe`. It upgrades the existing per-user installation in place. Saved settings and upload state remain under `%LOCALAPPDATA%\ClipsToDiscord`.

The installer deliberately refuses a silent update while the application mutex is active. Exit the tray app before an unattended update. Silent setup does not relaunch the app; interactive setup offers to launch it when finished.

Portable users can exit the app, replace the extracted application files, and reopen `ClipsToDiscord.exe`.

## Uninstalling

Open **Settings → Apps → Installed apps**, find **Clips to Discord**, and select **Uninstall**. The uninstaller removes the application, its shortcuts, and its **Start with Windows** entry.

Settings, logs, and upload history remain in `%LOCALAPPDATA%\ClipsToDiscord` so reinstalling does not lose the duplicate-upload protections. Delete that data directory manually only if you also want to reset the app completely.
