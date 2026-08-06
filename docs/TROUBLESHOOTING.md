# Troubleshooting

## The app appears not to open

Check the Windows notification area, including its hidden-icons menu. The app is designed to run without a normal main window after setup. Only one instance can run at a time.

If Windows SmartScreen appears, verify the installer or portable ZIP came from this repository's Releases page. The current community build is not code-signed.

## A new clip did not upload

Check these in order:

1. Discord desktop is open. The watcher intentionally pauses while Discord is closed.
2. The tray status says it is watching for clips.
3. **Upload new clips to Discord** is enabled. If the tray says **Local only**, the clip is intentionally moved under `local-only` without a Discord request.
4. The selected directory is the folder containing the new `.mp4`, not the `uploaded` or `local-only` folder.
5. The clip is a top-level `.mp4`. Nested directories and other formats are ignored.
6. The recording tool has finished writing the file. The app requires the last write to be at least twenty seconds old, matching length and timestamp observations across ten seconds, and a read handle that denies concurrent writers before queueing it.
7. The webhook still exists and points to the intended channel.

Clips already present during the first scan are recorded as a safe baseline and are not uploaded automatically.

## The clip is too large

Both release packages include `ffmpeg.exe`. The installer keeps it with the application automatically. Portable users must keep it beside `ClipsToDiscord.exe`. The app uses FFmpeg only after Discord rejects an original clip for size, and compression can take a while on long clips.

The compression target is configurable from 1–100 MB and defaults to 95 MB for new settings. If the selected target is rejected, the app retries progressively smaller targets that can still preserve its minimum video bitrate. For a destination limited to Discord's standard 10 MiB per-file limit, selecting 9 MB avoids the initial larger attempts.

The selected value is a size ceiling, not a request to fill the entire allowance. ClipCord currently caps compressed video at 6,000 kbps, so short high-bitrate recordings can finish substantially below the selected target while retaining good playback quality. The application log records the original and compressed sizes, reduction percentage, target ceiling, and chosen video/audio bitrates for each completed encode.

Very long clips may be impossible to compress to a small Discord limit without falling below that bitrate. ClipCord reports **Upload needs attention**, leaves the original clip in place, and stops automatic retries for that clip until settings change or the app restarts. Shorten the clip, raise the compression target if the destination accepts larger files, or upload it another way.

If building from source without FFmpeg, normal-size clips still work, but oversized clips report an error in the log.

## Uploaded clip did not move

After Discord accepts the upload, the app records it before moving the original. This prevents a move failure from posting the same clip twice. The app retries pending moves into `uploaded\<game name>`. Check that the Windows account can create and move files in the selected clips directory.

Folder matching is case-insensitive: `uploaded`, `Uploaded`, and `UPLOADED` are treated as the same archive folder, and differently-cased versions of one game reuse the existing game folder. When no archive exists, the app creates lowercase `uploaded` automatically. SteelSeries `Game__YYYY-MM-DD__HH-MM-SS`, common dotted timestamps, and compact timestamps are recognized; other filenames go into `Uncategorized`.

## A clip was intentionally kept local

When **Upload new clips to Discord** is off, ClipCord makes no webhook request. After the normal readiness checks, it moves each new clip into `local-only\<game name>` and shows **Local only** in the status area. Turning uploads back on does not scan or upload anything already inside `local-only`; post those files manually if desired.

## Multiple people share one webhook

Open **Settings** on each computer and give every installation a different **Uploader name**, normally that person's Discord display name. Each upload is posted as visible text and attachment description, for example `Malik uploaded a clip from Battlefield™-6.` Existing installations upgrading from an earlier release initially use the Windows account name and can change it at any time.

## Logs and local state

Open this directory in File Explorer:

```text
%LOCALAPPDATA%\ClipsToDiscord
```

- `app.log` contains operational messages and upload errors.
- `settings.json` contains the folder preference and an encrypted webhook value.
- `state.json` tracks the initial baseline, completed clips, and pending moves.

State uses SHA-256 content hashes. The first v1.2 launch may spend extra time reading existing top-level and archived clips once to create a safe baseline.

Do not post `settings.json` publicly. Although the webhook is encrypted for the Windows account, configuration files should still be treated as private.

## Reset the baseline safely

1. Exit the app from the notification-area menu.
2. Rename `state.json` to `state.backup.json`.
3. Start the app again.

The next scan treats all currently present clips as an existing baseline. This does not upload them. Keep the backup until the app is confirmed working, then remove it if desired.
