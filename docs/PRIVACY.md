# Privacy and security

## Data flow

ClipCord runs locally. When Discord uploads are enabled, it scans the configured clips folder and sends eligible video files directly to the configured Discord webhook endpoint over HTTPS. In local-only mode, automatic clip processing makes no Discord request; a Local-only clip is sent only when the user explicitly chooses **Edit & upload** in Gallery.

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

The Gallery reads the local `uploaded` and `local-only` archives only while its page is open. It does not build a background media index, contact an artwork service, or upload anything merely because a clip was browsed or played. For cards currently visible in Gallery, the bundled FFmpeg may decode one near-start frame locally. Those thumbnails are cached under `%TEMP%\ClipsToDiscord\gallery-thumbnails`, keyed by the clip path, length, and last-write time, expired after seven days, and kept within 128 MB; no thumbnail or clip identity is sent over the network. Playback stays inside ClipCord and may prepare the local mixed-audio copy described below. Selecting **Edit & upload** is an explicit upload action: ClipCord uses the bundled FFmpeg locally for still-frame preview and any trim/mute render, then sends only the prepared video, uploader/game attribution, and optional description to the configured Discord webhook. The Local-only original remains untouched until Discord confirms success; by default it then goes to the Windows Recycle Bin after the edited archive is committed, or remains in Local only when the user enables **Keep original**.

The About page computes its status locally. **Copy diagnostics** places a fixed, allowlisted summary on the Windows clipboard only when the user selects it. The summary can include the ClipCord, Windows, and .NET versions; operating-system and process architecture; installed or portable state; normalized watcher and routing states; Discord, startup, and FFmpeg availability; and a UTC timestamp. It excludes the webhook, uploader name, Windows user and machine names, clip and application-data paths, clip names, raw watcher text, activity history, and logs. Nothing is submitted automatically; project and documentation actions open fixed HTTPS pages in the official GitHub repository.

## File handling

- Existing clips are ignored during the initial baseline.
- New top-level `.mp4` clips are read after the source application finishes writing them. When the capture source is set to NVIDIA, the configured folder is the one holding your per-game recording folders — for a default NVIDIA install that is `Videos\NVIDIA`. ClipCord then reads new `.mp4` clips exactly one level inside it, in each `<game>` subfolder. Nothing deeper is scanned, files sitting loose in the configured folder are ignored, and its own `uploaded`, `local-only`, and `.clipcord-editing` folders are never treated as capture folders.
- Successfully uploaded originals move into local `uploaded\<game name>` subfolders; unrecognized filename formats use `uploaded\Uncategorized`.
- In local-only mode, newly detected originals move into local `local-only\<game name>` subfolders without being sent to Discord.
- User-requested Gallery edits stage beneath `.clipcord-editing` in the configured clips folder so the watcher ignores them and the final archive move stays on the same volume. Failed or cancelled pre-upload edits clean their stage and leave the original unchanged; confirmed uploads persist a recovery record before archive or Recycle Bin work.
- Choosing **Play selection** in the editor renders only the selected range with the bundled FFmpeg into `%TEMP%\ClipsToDiscord\editor-playback`, and plays it in place. If in-editor playback is unavailable, ClipCord asks Windows to open that trimmed copy with the default video application; the untrimmed original is never handed to another program. Each trimmed preview is deleted when the next one starts, when playback stops, and when the editor closes.
- Gallery thumbnail generation reads only cards that enter the visible Gallery viewport. Cached PNGs are invalidated when the source path, length, or write time changes; partial files older than one hour and thumbnails older than seven days are pruned, with an overall 128 MB cap. Opening ClipCord or leaving Always Watching enabled does not start thumbnail work.
- Playing a clip with multiple audio tracks creates an on-demand mixed playback copy under `%TEMP%\ClipsToDiscord\playback-mix` so every recorded audio track can be heard together. These copies are reused for the same unchanged clip, expire after seven days, and the cache is capped at 4 GB. The original recording is never modified.
- Temporary compressed files are deleted after the upload attempt.
- Partial or failed update downloads are deleted; a completed staged installer may be reused after re-verification.
- Duplicate destination names receive a unique suffix and are never overwritten.

## Threat model and limitations

- A webhook URL is a secret bearer credential. Anyone who obtains it can post through that webhook.
- DPAPI protects the saved value at rest, but malware running as the same Windows user may still access process or user data.
- Release executables are currently unsigned. The in-app updater verifies the expected repository, asset path, byte length, and published SHA-256 digest, but this is not a substitute for future Authenticode code signing and Windows may still show an unsigned-publisher warning.
- Choosing **Install update** from a portable copy installs ClipCord into the normal per-user application directory; it does not overwrite or delete the old extracted portable folder.
- The app does not moderate clip content or control who can view the destination Discord channel.
