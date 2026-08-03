# Changelog

All notable user-facing changes are documented here.

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
