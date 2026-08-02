# Troubleshooting

## The app appears not to open

Check the Windows notification area, including its hidden-icons menu. The app is designed to run without a normal main window after setup. Only one instance can run at a time.

If Windows SmartScreen appears, verify the ZIP came from this repository's Releases page. The current community build is not code-signed.

## A new clip did not upload

Check these in order:

1. Discord desktop is open. The watcher intentionally pauses while Discord is closed.
2. The tray status says it is watching for clips.
3. The selected directory is the folder containing the new `.mp4`, not the `uploaded` folder.
4. The clip is a top-level `.mp4`. Nested directories and other formats are ignored.
5. The recording tool has finished writing the file. The app waits at least 20 seconds and requires exclusive read access before uploading.
6. The webhook still exists and points to the intended channel.

Clips already present during the first scan are recorded as a safe baseline and are not uploaded automatically.

## The clip is too large

The release ZIP includes `ffmpeg.exe`. Keep it beside `ClipsToDiscord.exe`; the app uses it only after Discord rejects an original clip for size. Compression can take a while on long clips.

If building from source without FFmpeg, normal-size clips still work, but oversized clips report an error in the log.

## Uploaded clip did not move

After Discord accepts the upload, the app records it before moving the original. This prevents a move failure from posting the same clip twice. The app retries pending moves into the `uploaded` subfolder. Check that the Windows account can create and move files in the selected clips directory.

## Logs and local state

Open this directory in File Explorer:

```text
%LOCALAPPDATA%\ClipsToDiscord
```

- `app.log` contains operational messages and upload errors.
- `settings.json` contains the folder preference and an encrypted webhook value.
- `state.json` tracks the initial baseline, completed clips, and pending moves.

Do not post `settings.json` publicly. Although the webhook is encrypted for the Windows account, configuration files should still be treated as private.

## Reset the baseline safely

1. Exit the app from the notification-area menu.
2. Rename `state.json` to `state.backup.json`.
3. Start the app again.

The next scan treats all currently present clips as an existing baseline. This does not upload them. Keep the backup until the app is confirmed working, then remove it if desired.
