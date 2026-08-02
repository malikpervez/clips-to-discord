# Changelog

All notable user-facing changes are documented here.

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
