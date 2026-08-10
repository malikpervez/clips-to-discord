# Privacy and security

## Data flow

ClipCord runs locally. When Discord uploads are enabled, it scans the configured clips folder and sends eligible video files directly to the configured Discord webhook endpoint over HTTPS. In local-only mode, clip processing makes no Discord request.

The app has no analytics, advertising, account system, telemetry service, or project-operated server.

## Local data

The app stores the following under `%LOCALAPPDATA%\ClipsToDiscord`, retaining the pre-ClipCord path so upgrades preserve settings and upload history:

- The chosen clips-folder path
- The uploader name shown with clips in Discord
- The start-with-Windows preference
- Whether new clips should upload to Discord or remain local-only
- The optional global shortcut used to switch between upload and local-only routing
- The Discord webhook URL encrypted with Windows DPAPI for the current user
- Path/length/timestamp keys used only to preserve the initial do-not-upload baseline
- SHA-256 hashes of clip contents used for stable duplicate detection
- Pending archive moves
- The last automatic update-check time and optional skipped/reminder version
- A verified installer temporarily staged under `updates\v<version>` only after the user chooses **Install update**
- Operational logs
- Up to 100 recent clip-activity entries containing filename, parsed game, state, attempt count, sizes/bitrates when compressed, concise redacted errors, and local source/archive paths used by **Show in folder**

The webhook cannot normally be decrypted by another Windows account or after moving the settings file to another Windows installation.

## Network access

Normal upload operation connects only to the validated Discord webhook URL supplied by the user and does not follow HTTP redirects. Each upload sends the configured uploader name and parsed game name as visible message text and attachment description. When a clip is too large, compression is performed locally by the bundled FFmpeg executable before the smaller copy is uploaded.

The app also makes an anonymous HTTPS request to the fixed official `malikpervez/clips-to-discord` GitHub Releases API no more than once every 24 hours, or when the user explicitly selects **Check for updates**. If GitHub's installer metadata lacks its own digest, the app may fetch the release's small checksum file through the validated GitHub URL and at most one allow-listed GitHub asset-CDN redirect. These requests do not contain the webhook, uploader name, clip names or paths, settings, or a project account identifier. **View changes** opens the official release page. If the user explicitly chooses **Install update**, ClipCord downloads the verified installer directly from GitHub's allow-listed release-asset host, stages it locally, and starts it only after length and SHA-256 verification.

SHA-256 hashing and FFmpeg compression are performed locally. Clip content and hashes are not sent to a project-operated server. Operational log messages pass through a webhook-URL redactor before being written.

The global mode shortcut is registered locally with Windows only while ClipCord is running. Pressing it uses the same persisted settings and watcher-reconfiguration path as the notification-area toggle; it does not create a network request by itself.

The Activity Center reads only the local bounded activity history. It never stores or displays the Discord webhook, and its text fields pass through the same webhook redactor before atomic persistence. Closing the Activity window does not affect clip watching or uploads.

The Gallery reads the local `uploaded` and `local-only` archives only while its page is open. It does not build a background media index, contact an artwork service, upload local-only clips, or send clip names and paths over the network. Playing a clip asks Windows to open the exact local file with the user's default video application.

## File handling

- Existing clips are ignored during the initial baseline.
- New top-level `.mp4` clips are read after the source application finishes writing them.
- Successfully uploaded originals move into local `uploaded\<game name>` subfolders; unrecognized filename formats use `uploaded\Uncategorized`.
- In local-only mode, newly detected originals move into local `local-only\<game name>` subfolders without being sent to Discord.
- Temporary compressed files are deleted after the upload attempt.
- Partial or failed update downloads are deleted; a completed staged installer may be reused after re-verification.
- Duplicate destination names receive a unique suffix and are never overwritten.

## Threat model and limitations

- A webhook URL is a secret bearer credential. Anyone who obtains it can post through that webhook.
- DPAPI protects the saved value at rest, but malware running as the same Windows user may still access process or user data.
- Release executables are currently unsigned. The in-app updater verifies the expected repository, asset path, byte length, and published SHA-256 digest, but this is not a substitute for future Authenticode code signing and Windows may still show an unsigned-publisher warning.
- Choosing **Install update** from a portable copy installs ClipCord into the normal per-user application directory; it does not overwrite or delete the old extracted portable folder.
- The app does not moderate clip content or control who can view the destination Discord channel.
