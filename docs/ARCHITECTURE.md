# Architecture

ClipCord is a self-contained .NET 8 Windows Forms tray application. It is independent of the software that creates the clip; the input contract is a completed `.mp4` file the configured capture source knows how to find — directly in the configured folder for SteelSeries GG, or one level inside it in a `<game>` subfolder for NVIDIA, where the configured folder is the one holding those game folders (`Videos\NVIDIA` by default). Discovery is the only stage that varies by source; every stage after it is identical.

```mermaid
flowchart LR
    A["Tray application"] --> B{"Discord process running?"}
    B -- "No" --> C["Idle for 5 seconds"]
    C --> B
    B -- "Yes" --> D["Scan configured folder"]
    D --> E{"Length and timestamp stable?"}
    E -- "No" --> F["Back off and observe again"]
    F --> D
    E -- "Yes" --> G["Compute SHA-256"]
    G --> H["Bounded clip queue"]
    H --> R{"Discord uploads enabled?"}
    R -- "No" --> S["Flush local-only hash and pending move"]
    S --> T["Move original into local-only/game folder"]
    R -- "Yes" --> I1["Upload worker 1"]
    R -- "Yes" --> I2["Upload worker 2"]
    I1 --> J{"Discord accepts size?"}
    I2 --> J
    J -- "No" --> K["Compress locally to progressive targets"]
    K --> J
    J -- "Yes" --> L["Flush content hash and pending move to disk"]
    L --> M["Move original into uploaded/game folder"]
```

## Components

- `TrayApplicationContext` owns the notification-area UI, settings dialog, startup registration, and controller lifecycle.
- `GlobalHotkeyManager` owns one message-only Windows handle, atomically replaces the configured mode shortcut, suppresses key-repeat dispatch, and unregisters it on disable or application exit.
- `ModeFeedbackOverlay` owns one reusable, non-activating topmost window that confirms shortcut outcomes on the monitor containing the active application without hooking or injecting into it.
- `DiscordAwareController` starts and cancels the uploader worker based on Discord desktop processes, debounces brief absences, and exposes an awaitable stop that Settings and tray reconfiguration must complete before starting a replacement worker.
- `FileReadinessTracker` requires stable metadata across multiple observations and exponentially backs off unreadable files.
- `UploaderWorker` discovers clips, computes content identity, feeds a bounded queue, and runs two clip consumers that either upload or archive locally according to the persisted setting.
- `ActivityHistoryStore` accepts thread-safe immutable lifecycle updates from both workers, retains at most 100 redacted metadata entries with atomic persistence, and posts snapshots to non-blocking UI subscribers.
- `ActivityView` renders those snapshots independently of the watcher and provides valid local file, uploaded-folder, and diagnostic-log actions.
- `GalleryCatalog` performs a bounded, one-level scan of the existing uploaded and local-only archives on demand, skips reparse points, preserves each clip's route, and groups clips case-insensitively by game. Parseable legacy clips left at an archive root join their inferred game; unrecognized names remain under `Uncategorized`.
- `GalleryView` renders deterministic local gradient cards and clip actions only while its page is active; it has no timer, file watcher, artwork request, or persistent media index.
- `LocalClipEditorView` adds an on-demand, focused Local-only workflow for still-frame preview, selection playback, trim, mute, naming, attribution, and explicit upload without embedding a browser. Playback prefers a hosted WPF `MediaElement` bounded to the selected range; when that engine is unavailable it renders the selection to a temporary clip and hands only that trimmed copy to the default player. Preview-frame adoption is suppressed while playback is active, and changing the selection cancels any in-flight preview render.
- `ClipEditProcessor` validates the selected Local-only path, probes and renders with the bundled FFmpeg through argument lists, and stages same-volume artifacts beneath the watcher-invisible `.clipcord-editing` directory.
- `EditedClipUploadService` persists the edited content hash and fixed archive/recycle disposition before touching either source file; `UploaderWorker` resumes confirmed pending dispositions without another webhook request.
- `AboutView` renders a read-only status, privacy, diagnostics, links, and credits page inside the existing Settings shell without adding a background worker or network client.
- `AboutPageSupport` normalizes watcher text to fixed categories, constructs an exact allowlisted diagnostic summary, classifies installed versus portable copies without exposing paths, and maps project actions to fixed official HTTPS GitHub targets.
- `DiscordWebhookClient` sends multipart attachments with uploader/game attribution, disabled mentions, separate connection and total deadlines, and progressively smaller compression retries.
- `FfmpegCompressor` performs local two-pass H.264/AAC compression to a requested target.
- `SettingsStore` encrypts the webhook with DPAPI and performs staged legacy migration.
- `WatchStateStore` uses durable atomic replacement for content hashes, safe-baseline keys, pending archive moves, and confirmed edited-upload dispositions.
- `GitHubUpdateChecker` fetches only the repository's fixed latest-release endpoint, validates stable release and installer metadata, and uses bounded response sizes and deadlines.
- `UpdateCoordinator` enforces the 24-hour automatic-check interval, prevents concurrent checks, and applies persisted skip/remind choices.
- `UpdateDownloadService` follows at most one allow-listed GitHub asset redirect, streams into a temporary per-version file, enforces the release size, computes SHA-256 incrementally, and commits the verified installer atomically.
- `UpdateInstallerLauncher` confines and rehashes the staged installer after the application mutex is released, then starts the per-user installer with an explicit reopen request.
- `UpdatePreferencesStore` atomically stores non-secret update timing and suppression preferences separately from the DPAPI-protected webhook settings.
- The application manifest declares `longPathAware` for supported Windows systems, reducing legacy `MAX_PATH` failures when clips are archived under game subfolders.
- `SensitiveDataRedactor` strips registered or recognizable Discord webhook URLs before log output.

## Reliability choices

- Files must be at least twenty seconds past their last write and need matching length and timestamp observations at least ten seconds apart.
- Read probes allow other readers but deny concurrent writers; three consecutive open failures produce a throttled stuck-file log.
- Read access uses shared mode for recorder compatibility; failures back off exponentially up to five minutes.
- SHA-256 identity survives file, folder, and timestamp renames at the cost of one full local read per new clip.
- Two workers isolate the queue from one slow request; each HTTP upload has a five-minute deadline and connection establishment has a 15-second deadline.
- Confirmed upload state is flushed to disk before any archive move.
- A user-edited upload stops the active watcher before sharing its state store. Each webhook POST is allowed to finish under its bounded request deadline once started, while inspection, rendering, compression, and later attempts remain cancellable. This avoids a local-cancel/remote-success duplicate window; its edited hash and disposition are then flushed before archive or Recycle Bin work.
- Local-only state and its pending destination are also flushed before moving, independently of uploaded state.
- Move destinations never overwrite existing files.
- Archive-folder resolution reuses case-insensitive `uploaded`, `local-only`, and game-name matches, creates lowercase canonical names only when none exists, and uses `Uncategorized` when no supported filename timestamp exposes a game name.
- Safe baselines hash legacy root-level archives and one-level game subfolders under both destinations, including normal iCloud placeholder folders, while refusing to traverse symbolic links and junctions.
- Upload failures retry after five minutes.
- A named mutex prevents multiple tray-app instances.
- Three consecutive two-second Discord-absence polls are required before watcher cancellation; a brief updater relaunch therefore does not churn the worker.
- Settings and tray mode changes disable concurrent reconfiguration and await complete shutdown of the old controller before creating the new one, so two workers cannot save independent stale state snapshots.
- Global-shortcut mode changes use that same serialized persist-and-reconfigure path; registration conflicts restore the previous binding, and presses are ignored while a ClipCord dialog or reconfiguration is active.
- Controller disposal waits at most ten seconds on the UI thread, while any slower worker cleanup remains observed in the background.
- Every runtime access to mutable watch-state collections is serialized through one gate shared by the scanner and both upload workers.
- Activity subscribers receive immutable snapshots through their synchronization context; closing the Activity window detaches the subscription without stopping or reconfiguring the worker.
- Gallery archive discovery runs on demand off the UI thread, is cancelled when the page closes, traverses only the archive root and one game-directory level, and never follows a symbolic link or junction.
- About diagnostics are copied only on explicit user action and contain exactly the fixed non-secret lines constructed from typed or normalized status values before centralized redaction.
- Version 1.1+ copies compatible settings from the former Moments to Discord data directory without deleting the original files.
- Stable update discovery accepts only a newer semantic version with exact official GitHub release/asset paths and a SHA-256 asset digest or bounded checksum-manifest entry.
- Update installation remains user-initiated. Downloads have visible progress and cancellation, are limited to 512 MiB, follow at most one allow-listed redirect, and are deleted if their length or digest fails verification.
- Verified installers are staged below `%LOCALAPPDATA%\ClipsToDiscord\updates\v<version>`. ClipCord exits and releases its mutex before revalidating and launching the exact staged file; setup reopens ClipCord only for this in-app path.

See [RELIABILITY.md](RELIABILITY.md) for the exact-once limitation and migration details.

## Packaging

`scripts/get-ffmpeg.ps1` downloads one pinned, versioned FFmpeg archive and validates the archive, executable, and license SHA-256 values. `scripts/package.ps1` publishes a single-file, self-contained `win-x64` executable and bundles FFmpeg only together with its license. `scripts/build-installer.ps1` accepts exactly `ClipsToDiscord.exe`, `README.txt`, and the FFmpeg/license pair for releases; directories, reparse points, settings, state, logs, and every other item fail the build. The Inno script names those four sources explicitly rather than using a recursive wildcard. After all installer tests pass on `main`, CI uploads the exact installer, portable ZIP, and checksum manifest as a release-candidate artifact.

The installer retains the existing AppId and targets `%LOCALAPPDATA%\Programs\ClipsToDiscord` so ClipCord upgrades the earlier Clips to Discord installation in place. The executable, application-data path, startup value, and mutex also retain their internal names to avoid duplicate installations, lost state, or simultaneous old/new processes. The ZIP remains the portable option. `scripts/get-inno-setup.ps1` pins the official installer SHA-256, uses a temporary download, and validates a deterministic SHA-256 identity over all 132 compiler-tree files before every cached use and after extraction. Build outputs and third-party binaries are excluded from Git.
