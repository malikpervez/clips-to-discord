# Changelog

All notable user-facing changes are documented here.

## Unreleased

- Added an in-app update download with visible progress, cancellation, retry, and automatic restart after a successful install.
- Verified the exact official GitHub release URL, allowed asset redirect, declared file length, and SHA-256 digest before staging an installer under the current user's local application data.
- Reverify and confine the staged installer immediately before launch, release the single-instance mutex before setup starts, and leave the current installation untouched when download or verification fails.
- Kept cancellation authoritative even when a network response completes at the same moment, allowed Windows shutdown to proceed, and reopened the existing app if a launched setup later fails.

## 1.4.1 — 2026-08-05

- Replaced the simplified Settings-header symbol with the official ClipCord app artwork.
- Enlarged the header logo and centered it with the ClipCord wordmark using DPI-independent layout.
- Added regression coverage that verifies the embedded PNG is decoded, displayed at a proportional size, and visibly painted with the official brand colors.

## 1.4.0 — 2026-08-05

- Redesigned Settings with ClipCord's coral/violet branding, clearer cards, live watcher status, and a DPI-safe responsive layout.
- Added a visibly disabled Activity navigation item with a "Coming in a future release" tooltip and accessibility description.
- Added reliable twelve-pixel edge and corner resizing, multi-monitor working-area-aware maximizing, keyboard navigation, accessible field names, and responsive behavior across DPI changes.
- Fixed stale buffered pixels appearing behind the Start with Windows toggle after repainting or resizing the Settings window.

## 1.3.7 — 2026-08-04

- Renamed the app to ClipCord across the user interface, installer, portable package, and documentation.
- Preserved the existing installer identity, executable name, data directory, startup value, and mutex so current users upgrade in place without losing settings or duplicate-upload history.
- Added a legacy `ClipsToDiscord-Setup.exe` release alias so pre-ClipCord update checkers can discover the transition release.

## 1.3.6 — 2026-08-04

- Added stable-only update discovery through the official GitHub Releases API with strict repository, HTTPS, semantic-version, installer, and SHA-256 verification.
- Added a manual **Check for updates** action plus **View changes**, **Download update**, **Skip this version**, and **Remind me later** choices; download actions open the official release page and never install silently.
- Limited automatic checks to once per 24 hours, prevented concurrent checks, persisted skip/remind preferences atomically, and isolated all update failures from clip watching and uploads.

## 1.3.5 — 2026-08-03

- Stopped over-escaping ordinary game-name punctuation so names such as `Battlefield™-6` and `Half-Life` display without visible backslashes in Discord.
- Made uploader-name truncation safe at UTF-16 surrogate boundaries so an emoji at the 80-character limit cannot create an invalid payload.
- Declared the application long-path-aware to reduce archive-move failures for deeply nested clip folders and long recording filenames on supported Windows systems.
- Pinned and hash-verified the FFmpeg release package used by CI, tested upgrades against the actual v1.3.4 installer, and retained the exact post-test installer, portable ZIP, and checksum manifest as a downloadable CI artifact.
- Added regression coverage for hyphenated attribution and Unicode boundary truncation.

## 1.3.4 — 2026-08-03

- Reworked the settings window into a resizable, DPI-aware layout so long paths, action buttons, helper text, and bottom controls remain visible.
- Added a per-installation uploader name for friends who share one webhook; existing settings safely default to the current Windows account name until changed.
- Label each Discord upload with `Uploader uploaded a clip from Game` as both visible message content and the attachment description, while continuing to suppress mentions.
- Applied the branded application icon to the open settings window.
- Added UI-boundary, uploader-normalization, Markdown-escaping, attachment-description, and mention-suppression regression tests.

## 1.3.3 — 2026-08-03

- Added a custom coral film-frame and violet lightning icon designed to remain recognizable at Windows system-tray sizes.
- Applied the same branded icon to the live tray app, executable, Start Menu and desktop shortcuts, Add/Remove Programs entry, and installer.
- Added automated package checks that reject an app or installer missing the expected coral and violet icon colors.

## 1.3.2 — 2026-08-03

- Changed the compression target default for new settings from 9 MB to 95 MB, while preserving every existing saved choice and retaining progressively smaller retries down to 9 MB and below.
- Archive newly uploaded clips into `uploaded\<game name>` when the filename contains a recognized SteelSeries, dotted, or compact recording timestamp.
- Route filenames without a recognizable game/timestamp structure into one `uploaded\Uncategorized` folder.
- Reuse both the main archive and game subfolders case-insensitively, normalize folder names, reject unsafe Windows names, and never overwrite a same-named clip.
- Include legacy root-level and new game-subfolder clips in safe-baseline content hashing.

## 1.3.1 — 2026-08-03

- Replaced the recursive installer payload wildcard with four explicit permitted files and rejected every unexpected directory, file, or reparse point before compilation.
- Required FFmpeg and its license to appear as a pair, with an additional release-build switch that makes both mandatory.
- Verified the complete pinned Inno Setup compiler tree on every cached use and after installation; removed automatic fallback to unverified system compilers.
- Downloaded the compiler installer through a temporary file and published it to the cache only after its pinned SHA-256 passes.
- Expanded CI to cover the default install path, Start Menu behavior, startup non-creation and cleanup, in-place upgrades, application-data preservation, running-app mutex rejection, compiler tampering, and unexpected package files.
- Documented normal unelevated installation and the requirement to exit the tray app before updates.

## 1.3.0 — 2026-08-03

- Added a no-admin per-user Windows installer that installs under `%LOCALAPPDATA%\Programs\ClipsToDiscord`.
- Added Start Menu and Add/Remove Programs integration plus an optional desktop shortcut.
- Launch the installed copy after setup and remove the startup registry value during uninstall.
- Retained the ZIP as a supported portable distribution.
- Added reproducible installer build scripts with a pinned, checksum-verified Inno Setup compiler download.
- Added CI compilation and silent-install verification of the installer.

## 1.2.2 — 2026-08-02

- Captured the Discord-close decision without a second process re-poll, preventing updater relaunch races from permanently disabling monitoring.
- Require three consecutive absent polls before stopping the watcher, avoiding churn during brief Discord process flickers.
- Always cancel and observe the watcher before disposing its linked token source.
- Bounded UI-thread controller disposal to ten seconds and defer cleanup safely if a worker takes longer.
- Preserved pending archive moves when an interrupted migration forces a safe-baseline rebuild.
- Added controller and migration regression coverage plus Windows GitHub Actions build and smoke-test CI.

## 1.2.1 — 2026-08-02

- Added an explicit 20-second minimum age to the existing two-observation file-stability check.
- Changed readiness, hashing, and upload reads to allow other readers while denying concurrent writers.
- Log a stuck readiness probe after three consecutive open failures, with five-minute log throttling.
- Guarded FFmpeg's `NUL` first-pass output with an explicit Windows platform check.

## 1.2.0 — 2026-08-02

- Replaced the single readiness check with length-and-timestamp stability observations plus exponential lock backoff.
- Replaced path/timestamp deduplication with SHA-256 content identity and safe one-time baseline upgrades.
- Added a bounded queue with two concurrent upload workers so one slow upload does not stop discovery or the full queue.
- Added a 15-second connection timeout and a five-minute total deadline for each upload attempt.
- Added a configurable 1–100 MB compression target with progressively smaller retries after Discord size rejections.
- Accepted both `/api/webhooks/...` and versioned `/api/v{number}/webhooks/...` Discord URLs.
- Added staged legacy migration with a safe-baseline recovery marker.
- Added centralized log redaction for registered and pattern-matched Discord webhook URLs.
- Made state writes explicitly flush to disk before replacement.

## 1.1.1 — 2026-08-02

- Reuse an existing `uploaded` archive folder regardless of capitalization, including `Uploaded` and `UPLOADED`.
- Create the canonical lowercase `uploaded` folder automatically when no case-insensitive match exists.
- Added a regression smoke test for archive-folder resolution and creation.

## 1.1.0 — 2026-08-02

- Renamed the app from Moments to Discord to Clips to Discord.
- Generalized folder selection and documentation for any tool that saves MP4 clips.
- Added migration of compatible v1.0 settings, state, logs, and Windows startup registration.
- Renamed application, package, and local-data identifiers while retaining existing behavior.

## 1.0.0 — 2026-08-02

- Added first-run setup for the clips folder and Discord webhook.
- Added DPAPI-encrypted webhook storage.
- Added Windows notification-area controls and optional startup registration.
- Added Discord-aware watcher start and stop behavior.
- Added stable-file detection, upload retries, and duplicate prevention.
- Added automatic moves into the `uploaded` subfolder after successful uploads.
- Added collision-safe filenames and persisted pending moves.
- Added optional FFmpeg compression for clips rejected for size.
- Added self-contained Windows packaging and public documentation.
