# Privacy and security

## Data flow

ClipCord runs locally. When Discord uploads are enabled, it scans the configured clips folder and sends eligible video files directly to the configured Discord webhook endpoint over HTTPS. In local-only mode, automatic clip processing makes no Discord request; a Local-only clip is sent only when the user explicitly chooses **Edit & upload** in Gallery.

The app has no analytics, advertising, account system, telemetry service, or project-operated server.

When ClipCord Capture is used, the app starts an isolated copy of its same signed executable for
capture work. The main interface and capture worker communicate only through a randomly named,
current-Windows-user local pipe. This communication does not use the network, and the worker exits
when ClipCord exits.

## Local data

The app stores the following under `%LOCALAPPDATA%\ClipsToDiscord`, retaining the pre-ClipCord path so upgrades preserve settings and upload history:

- The chosen clips-folder path
- The uploader name shown with clips in Discord
- The start-with-Windows preference
- Whether new clips should upload to Discord or remain local-only
- The optional global shortcut used to switch between upload and local-only routing
- ClipCord Capture preferences, including the selected camera device and the locally stored record
  of one-time Reaction Camera consent
- The Discord webhook URL encrypted with Windows DPAPI for the current user
- Additional named Discord connections encrypted with Windows DPAPI for the current user; route
  files contain only random opaque connection identifiers, never webhook URLs or tokens
- Local route definitions, the crash-resumable legacy-cutover marker (prepared, aborting, or
  committed), and a durable execution-authority record that binds active Routing to the exact
  migrated watcher source and ClipCord Capture library. The Capture binding consists only of two
  one-way SHA-256 fingerprints for the canonical location and Windows directory identity; neither
  record stores the Capture library path, Windows user name, native file id, or a Discord secret.
  While ClipCord is running, it also keeps a local Windows directory handle open to prevent that
  authorized folder from being silently renamed or replaced between checks. The handle is released
  at shutdown and never leaves the computer. ClipCord rechecks this local authority while Capture
  is running; if it is lost, ClipCord unregisters the replay shortcut and terminates the isolated
  recording process without finalizing or promoting an in-progress clip into the untrusted folder.
- Bounded local delivery plans, approval/retry state, content hashes, and opaque provider receipt
  references needed to resume active Routing without repeating a confirmed Discord delivery
- Immutable watched-source journal entries containing the clip's relative path and display filename,
  parsed game, source kind, duration, dimensions, stable file identity, content hash, matched plan,
  and opaque source/migration identities. The configured absolute watched root is not copied into
  those journal entries.
- In developer-enabled watched-folder Shadow mode only, a bounded local comparison document with
  opaque source/content digests, capture-source kind, parsed game name, duration and dimensions,
  matched route identifiers, and destination/output summaries. It contains no clip path or filename,
  webhook URL, token, uploader name, or provider receipt. Shadow mode is off by default and never
  uploads, moves, claims, or deletes a clip.
- Path/length/timestamp keys used only to preserve the initial do-not-upload baseline
- SHA-256 hashes of clip contents used for stable duplicate detection
- Pending archive moves
- A separate Routing-owned recovery record for explicit Gallery edited uploads, containing only the
  local source/archive paths and content hashes needed to finish filing an upload after Discord has
  confirmed it. It does not contain a webhook URL or token.
- The last automatic update-check time and optional skipped/reminder version
- A verified installer temporarily staged under `updates\v<version>` only after the user chooses **Install update**
- Operational logs
- Up to 100 recent clip-activity entries containing filename, parsed game, state, attempt count, sizes/bitrates when compressed, concise redacted errors, and local source/archive paths used by **Show in folder**
- Favorite clip identities containing only the local path, file length, and last-write time needed to recognize the same unchanged clip

The webhook cannot normally be decrypted by another Windows account or after moving the settings file to another Windows installation.

## Network access

Normal upload operation connects only to the validated Discord webhook URL supplied by the user and does not follow HTTP redirects. Each upload sends the configured uploader name and parsed game name as visible message text and attachment description. When a clip is too large, compression is performed locally by the bundled FFmpeg executable before the smaller copy is uploaded.

Unpackaged builds also make an anonymous HTTPS request to the fixed official `malikpervez/clips-to-discord` GitHub Releases API no more than once every 24 hours, or when the user explicitly selects **Check for updates**. If GitHub's installer metadata lacks its own digest, the app may fetch the release's small checksum file through the validated GitHub URL and at most one allow-listed GitHub asset-CDN redirect. These requests do not contain the webhook, uploader name, clip names or paths, settings, or a project account identifier. **View changes** opens the official release page. If the user explicitly chooses **Install update**, ClipCord downloads the verified installer directly from GitHub's allow-listed release-asset host, stages it locally, and starts it only after length and SHA-256 verification. Microsoft Store builds do not make these GitHub update requests; **Check for updates** opens the Store's downloads and updates surface instead.

SHA-256 hashing and FFmpeg compression are performed locally. Clip content and hashes are not sent to a project-operated server. Operational log messages pass through a webhook-URL redactor before being written. Raw FFmpeg stderr is not written to the log. FFmpeg failures are reduced to bounded, allow-listed diagnostic categories and numeric error codes, so no file path, username, clip name, URL, token, or command fragment from the media tool's raw diagnostic reaches the log.

The global mode shortcut is registered locally with Windows only while ClipCord is running. Pressing it uses the same persisted settings and watcher-reconfiguration path as the notification-area toggle; it does not create a network request by itself.

The Activity Center reads only the local bounded activity history. It never stores or displays the Discord webhook, and its text fields pass through the same webhook redactor before atomic persistence. Closing the Activity window does not affect clip watching or uploads.

The Gallery reads the local `uploaded` and `local-only` archives only while its page is open. It does not build a background media index, contact an artwork service, or upload anything merely because a clip was browsed, favorited, or played. Favorites are a local saved view: favoriting a clip does not move or copy it, and missing entries remain stored until the user removes them. For cards currently visible in Gallery, the bundled FFmpeg may decode one near-start frame locally. Those thumbnails are cached under `%TEMP%\ClipsToDiscord\gallery-thumbnails`, keyed by the clip path, length, and last-write time, expired after seven days, and kept within 128 MB; no thumbnail or clip identity is sent over the network. Playback stays inside ClipCord and may prepare the local mixed-audio copy described below. Selecting **Edit & upload** is an explicit upload action: ClipCord uses the bundled FFmpeg locally for still-frame preview and any trim/mute render, then sends only the prepared video, uploader/game attribution, and optional description to the configured Discord webhook. The Local-only original remains untouched until Discord confirms success; by default it then goes to the Windows Recycle Bin after the edited archive is committed, or remains in Local only when the user enables **Keep original**.

The About page computes its status locally. **Copy diagnostics** places a fixed, allowlisted summary on the Windows clipboard only when the user selects it. The summary can include the ClipCord, Windows, and .NET versions; operating-system and process architecture; installed or portable state; normalized watcher and routing states; Discord, startup, and FFmpeg availability; and a UTC timestamp. It excludes the webhook, uploader name, Windows user and machine names, clip and application-data paths, clip names, raw watcher text, activity history, and logs. Nothing is submitted automatically; project and documentation actions open fixed HTTPS pages in the official GitHub repository.

## File handling

- Existing clips are ignored during the initial baseline.
- New top-level `.mp4` clips are read after the source application finishes writing them. When the capture source is set to NVIDIA, the configured folder is the one holding your per-game recording folders — for a default NVIDIA install that is `Videos\NVIDIA`. ClipCord then reads new `.mp4` clips exactly one level inside it, in each `<game>` subfolder. Nothing deeper is scanned, files sitting loose in the configured folder are ignored, and its own `uploaded`, `local-only`, and `.clipcord-editing` folders are never treated as capture folders.
- Developer-enabled watched-folder Shadow mode reuses that same legacy scan and stable content hash;
  it does not run a second scanner. Its evaluator output is retained only as local comparison evidence
  and cannot replace the established Discord or Local-only processing path.
- After the one-time Routing activation succeeds, ClipCord's active watched-folder adapters replace
  the legacy scan for new SteelSeries or NVIDIA clips. Each physical occurrence is validated beneath
  the configured source root, recorded in the local write-ahead journal, planned before delivery,
  and moved only after its required actions reach a durable terminal state. On restart, ClipCord
  resumes those journal and outbox records rather than treating the clip as new work.
- Successfully uploaded originals move into local `uploaded\<game name>` subfolders; unrecognized filename formats use `uploaded\Uncategorized`.
- In local-only mode, newly detected originals move into local `local-only\<game name>` subfolders without being sent to Discord.
- User-requested Gallery edits stage beneath `.clipcord-editing` in the configured clips folder so the watcher ignores them and the final archive move stays on the same volume. Failed or cancelled pre-upload edits clean their stage and leave the original unchanged; confirmed uploads persist a recovery record before archive or Recycle Bin work.
- Choosing **Play selection** in the editor renders only the selected range with the bundled FFmpeg into `%TEMP%\ClipsToDiscord\editor-playback`, and plays it in place. If in-editor playback is unavailable, ClipCord asks Windows to open that trimmed copy with the default video application; the untrimmed original is never handed to another program. Each trimmed preview is deleted when the next one starts, when playback stops, and when the editor closes.
- Gallery thumbnail generation reads only cards that enter the visible Gallery viewport. Cached PNGs are invalidated when the source path, length, or write time changes; partial files older than one hour and thumbnails older than seven days are pruned, with an overall 128 MB cap. Opening ClipCord or leaving Always Watching enabled does not start thumbnail work.
- Playing a clip with multiple audio tracks creates an on-demand mixed playback copy under `%TEMP%\ClipsToDiscord\playback-mix` so every recorded audio track can be heard together. These copies are reused for the same unchanged clip, expire after seven days, and the cache is capped at 4 GB. The original recording is never modified.
- Temporary compressed files are deleted after the upload attempt.
- ClipCord-owned recordings are written first beneath the configured library's `.clipcord\Staging` folder and become visible in `Library\Game\<Game>` only after the MP4 closes successfully. Startup recovery removes only abandoned ClipCord-owned `manual-capture-<random id>` video and audio stages older than 24 hours; recent stages and unrelated files are left untouched.
- When **Instant Replay** is enabled, ClipCord keeps short hardware-encoded H.264/MP4 video segments and the enabled game, microphone, and voice-chat PCM packets in bounded memory for the selected duration. It does not continuously write replay media to disk. Pressing the configured local shortcut takes a snapshot of that buffer, writes randomly named `replay-save-<random id>` files beneath `.clipcord\Staging`, combines the enabled audio inputs into one AAC stream, and atomically moves the completed clip into `Library\Game\<Game>`. Staging files are deleted after the save attempt; startup recovery can remove abandoned ClipCord-owned replay stages older than 24 hours. Turning Instant Replay off or exiting ClipCord discards the unsaved buffer.
- Reaction Camera is off by default. Before its first use, ClipCord presents a blocking consent
  explanation and records the user's one-time ClipCord consent locally. Windows camera privacy
  controls apply separately and can still allow or deny access. Apart from an explicitly requested
  consent preview, ClipCord opens the selected camera only while a manual recording or recognized-game
  replay buffer is actively capturing; arming Instant Replay while it is waiting for a game does not
  open the camera. Unsaved replay camera segments remain only in bounded RAM and are discarded when
  buffering stops or ClipCord exits. Saving a replay or finishing a manual recording stores the
  synchronized reaction segment as a separate local project layer under `.clipcord\Projects`; it
  does not replace the gameplay original.
  The Silhouette layouts editor stores only normalized placement, scale, mirror, outline, and
  Portrait framing defaults in `silhouette-preferences.json` under ClipCord's local app-data
  folder. When a camera-enabled capture starts, ClipCord freezes those values for that clip and
  commits them as `composition.json` beside the camera project; neither file contains a clip path,
  camera frame, or account credential, and changing the defaults never rewrites an existing clip.
  ClipCord removes an owned camera project only after its gameplay file has been missing for at
  least 30 days; unfamiliar or malformed project data is preserved rather than guessed at.
  After a camera-enabled clip commits, a separate low-priority ClipCord worker performs
  background removal entirely on the device with the bundled MODNet model. It keeps one shared
  lossless matte in the private camera project and publishes each selected Landscape and/or Portrait
  MP4 beside the original gameplay clip in `Library\Game\<Game>`, using a visible
  `__Reaction-Camera__Landscape.mp4` or `__Reaction-Camera__Portrait.mp4` suffix. The original
  gameplay MP4 and camera layer are immutable inputs and are never rewritten. Processing state and
  SHA-256 content identities are stored in `renditions.json` so an interrupted render can recover
  without treating a partial file as ready. Temporary rendition files use exact ClipCord-owned names
  in the private project directory and are atomically moved into the game folder only after
  validation; an occupied filename containing different media is never overwritten. A handled
  render failure deletes its exact owned temporary immediately when possible. Cancellation, shutdown,
  or a crash can leave an interrupted temporary behind; a later processing pass performs a bounded
  scan and removes only exact ClipCord-owned rendition temporaries once they are older than 24 hours.
  No frame,
  matte, model input, or rendered output leaves the computer
  unless the user later sends a completed clip through an enabled route.
  If the camera is unavailable, disconnected, busy, or denied by Windows, gameplay capture continues
  without a camera layer. ClipCord stops accepting camera frames when you switch it off. If Windows
  does not confirm device release within the bounded shutdown window, ClipCord keeps a visible tray
  warning and asks you to exit ClipCord to guarantee release while gameplay capture continues. The
  separate camera layer is not uploaded by itself.
- While ClipCord Capture is available, its isolated local capture worker checks the foreground top-level window so it can recognize a stable game from Windows' local game registration and common game-library locations. It keeps only the exact window handle, process identifier, display name, dimensions, and observation time in memory for up to 45 seconds; executable paths are used only for the local game check and are not stored or transmitted. ClipCord, Windows shell, browser, communication, hidden, minimized, tool, child, and undersized windows are rejected. Once capture begins, the target is fixed and does not follow focus to another monitor or app. **Choose game** remains the explicit fallback when automatic recognition is uncertain.
- During a ClipCord-owned recording, each enabled Windows audio endpoint is captured locally into a randomly named private WAV beneath `.clipcord\Staging`. Game and voice-chat output use Windows loopback capture; microphone capture uses the selected Windows input endpoint. These temporary inputs are aligned to the video clock, mixed locally into one AAC stream by the bundled FFmpeg, and deleted after finalization or a failed attempt. ClipCord does not transmit raw microphone, game, or chat audio anywhere unless the completed clip is later uploaded through an enabled route.
- Partial or failed update downloads are deleted; a completed staged installer may be reused after re-verification.
- Duplicate destination names receive a unique suffix and are never overwritten.

## Threat model and limitations

- A webhook URL is a secret bearer credential. Anyone who obtains it can post through that webhook.
- DPAPI protects the saved value at rest, but malware running as the same Windows user may still access process or user data.
- Direct GitHub release executables are currently unsigned. Their in-app updater verifies the expected repository, asset path, byte length, and published SHA-256 digest, but Windows may still show an unsigned-publisher warning. The Microsoft Store distribution is delivered as an MSIX package signed by Microsoft.
- Choosing **Install update** from a portable copy installs ClipCord into the normal per-user application directory; it does not overwrite or delete the old extracted portable folder.
- The app does not moderate clip content or control who can view the destination Discord channel.
