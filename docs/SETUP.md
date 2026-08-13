# Setup guide

## Requirements

- Windows 10 or Windows 11 on a 64-bit PC
- A clipping, recording, replay-buffer, or export tool that saves finished `.mp4` files into a folder
- Discord desktop installed
- Permission to create or manage a webhook in the destination Discord server, unless every new clip will remain local-only

The release is self-contained. Friends do not need ChatGPT, PowerShell, .NET, or a separate FFmpeg installation.

## 1. Install the app

1. Open the repository's [latest release](https://github.com/malikpervez/clips-to-discord/releases/latest).
2. Download `ClipCord-Setup.exe`.
3. Run the installer normally from Downloads, Desktop, or any other location. Do not choose **Run as administrator**.
4. Select **Install**. Administrator permission is not required.
5. Leave **Launch ClipCord** selected when setup completes.

The installer copies the application to `%LOCALAPPDATA%\Programs\ClipsToDiscord`, creates a Start Menu shortcut and an Add/Remove Programs entry, and optionally creates a desktop shortcut. Running it as another administrator account would install into that account's profile instead of yours. The downloaded setup file can be deleted after installation.

The portable `ClipCord-win-x64.zip` remains available. Portable users must extract every file together and keep the extracted folder in place while **Start with Windows** is enabled.

The release is not code-signed. Windows SmartScreen may display an unrecognized-app warning. Verify that the download came from this repository's Releases page before choosing **More info → Run anyway**.

## 2. Choose the clips folder

Open the settings for your clipping or recording tool and find its save, export, recordings, highlights, or replay folder. If the location is unclear, create a short test clip and use File Explorer to find the resulting `.mp4`.

Select the folder that directly receives new MP4 files. Do not select the `uploaded` or `local-only` subfolder that ClipCord creates.

The app watches only the top level of the selected directory. It does not scan nested folders, and it ignores formats other than `.mp4`.

## 3. Create the Discord webhook

This section is optional if **Upload new clips to Discord** will be turned off. Local-only mode does not require or contact a webhook.

1. In Discord, open the destination server's **Server Settings**.
2. Open **Integrations**, then **Webhooks**.
3. Create a webhook, choose the channel that should receive clips, and copy its URL.
4. Paste the URL into ClipCord.
5. Enter the uploader name that should appear with your clips. When friends share one webhook, each person should enter their own Discord display name.
6. Select **Test webhook** and confirm the test message appears in the channel with the expected name.

Discord's official guides: [Intro to Webhooks](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks) and [Server Integrations Page](https://support.discord.com/hc/en-us/articles/360045093012-Server-Integrations-Page).

Treat the webhook URL like a password. Anyone who has it can post through that webhook.

## 4. Save and use the app

1. Leave **Start with Windows** selected if the app should be available after each sign-in.
2. Set the compression target. New settings default to 95 MB to leave headroom beneath a 100 MB upload limit. If the destination only accepts the standard 10 MiB limit, set this to 9 MB to avoid unnecessary larger compression attempts. Updating the app preserves an existing saved value.
3. Leave **Upload new clips to Discord** on for automatic posting, or turn it off to move new clips into `local-only\<game name>` without a Discord request.
4. Keep the default **Ctrl + Alt + L** mode shortcut, focus its field and press another supported combination, or disable it. A shortcut must contain Ctrl or Alt, may also contain Shift, and must end with a letter, number, or F-key.
5. Select **Save**.
6. The app moves to the Windows notification area.

The first scan records existing clips as a baseline and does not upload them. After setup:

- Discord opens → the clip watcher starts.
- A new `.mp4` remains unchanged across the stability window → the app hashes and queues it.
- Two upload workers process the queue independently.
- Discord receives visible text and an attachment description such as `Malik uploaded a clip from Battlefield™-6.` Mentions remain disabled for uploader-provided text.
- Discord confirms the upload → the original moves into a case-insensitive `uploaded\<game name>` folder inferred from its timestamped filename. Unrecognized names use `uploaded\Uncategorized`.
- In local-only mode → no upload is attempted and the original moves into the corresponding case-insensitive `local-only\<game name>` folder.
- Discord closes → the clip watcher stops.
- Discord reopens → watching resumes automatically.

Right-click the notification-area icon to view status, toggle Discord uploads, reopen settings, open the clips folder, or exit. The configured global shortcut performs the same durable mode change and briefly shows a branded, non-activating indicator on the monitor containing the active game. It works only while ClipCord is running and is ignored while a ClipCord dialog or another settings change is active. Clips already moved into `local-only` are not uploaded when the toggle is turned back on.

Discord documents a default 10 MiB per-file limit, with potentially higher limits depending on the user or server. See Discord's official [Uploading Files reference](https://docs.discord.com/developers/reference#uploading-files). If Discord still rejects a compressed result, the app automatically tries progressively smaller targets.

## Updating from version 1.0

Exit the old app from its notification-area menu, download the current `ClipCord-Setup.exe`, and run it. The installed app copies compatible settings and upload state from `%LOCALAPPDATA%\MomentsToDiscord` into `%LOCALAPPDATA%\ClipsToDiscord` when the new location is empty.

## Updating later releases

Exit the app from its notification-area menu and run the newer `ClipCord-Setup.exe`. It upgrades the existing per-user installation in place, including installations previously named Clips to Discord. Saved settings and upload state remain under `%LOCALAPPDATA%\ClipsToDiscord`.

The app checks the official `malikpervez/clips-to-discord` GitHub Releases API no more than once every 24 hours. Stable checks ignore drafts and prereleases and offer only a newer release containing the expected installer plus a verifiable SHA-256 digest or checksum entry. Use **Check for updates** in Settings to check immediately.

When an update is available, **View changes** opens the verified official GitHub release page. **Install update** downloads the exact release installer inside ClipCord with visible progress and a cancel option. ClipCord validates the release URL, download host, file length, and SHA-256 digest before it closes. It then verifies the staged file again while holding it read-only through process launch, runs the per-user installer, and reopens automatically. If setup reports a failure after launching, it attempts to reopen the existing ClipCord installation instead. Nothing is downloaded or installed until you choose **Install update**.

If a download is interrupted or fails verification, its partial file is deleted and the installed version keeps running. **Skip this version** suppresses that exact version during automatic checks; **Remind me later** waits 24 hours. A later stable version is not hidden by either choice.

Branches, pull requests, Actions artifacts, tags without GitHub releases, drafts, and prereleases are never shown to stable users. An offline or failed update check does not stop clip watching or uploads.

The installer deliberately refuses an update while the application mutex is active. The in-app updater releases that mutex before setup starts and passes a dedicated restart request. Ordinary unattended setup does not relaunch the app; interactive setup still offers to launch it when finished.

Portable users who choose **Install update** transition to the normal per-user installation under `%LOCALAPPDATA%\Programs\ClipsToDiscord`; settings and upload history are shared, an existing **Start with Windows** entry is redirected to the installed copy, and the old extracted portable folder can then be removed. To remain portable, use **View changes**, exit ClipCord, replace all extracted application files together, and reopen `ClipsToDiscord.exe`.

## Uninstalling

Open **Settings → Apps → Installed apps**, find **ClipCord**, and select **Uninstall**. The uninstaller removes the application, its shortcuts, and its **Start with Windows** entry.

Settings, logs, and upload history remain in `%LOCALAPPDATA%\ClipsToDiscord` for compatibility with previous versions, so reinstalling does not lose the duplicate-upload protections. Delete that data directory manually only if you also want to reset the app completely.
