# Privacy and security

## Data flow

Clips to Discord runs locally. It scans the configured clips folder and sends eligible video files directly to the configured Discord webhook endpoint over HTTPS.

The app has no analytics, advertising, account system, telemetry service, or project-operated server.

## Local data

The app stores the following under `%LOCALAPPDATA%\ClipsToDiscord`:

- The chosen clips-folder path
- The start-with-Windows preference
- The Discord webhook URL encrypted with Windows DPAPI for the current user
- File signatures used to prevent duplicate uploads
- Pending archive moves
- Operational logs

The webhook cannot normally be decrypted by another Windows account or after moving the settings file to another Windows installation.

## Network access

Normal operation connects only to the Discord webhook URL supplied by the user. When a clip is too large, compression is performed locally by the bundled FFmpeg executable before the smaller copy is uploaded.

## File handling

- Existing clips are ignored during the initial baseline.
- New top-level `.mp4` clips are read after the source application finishes writing them.
- Successfully uploaded originals move into the local `uploaded` subfolder.
- Temporary compressed files are deleted after the upload attempt.
- Duplicate destination names receive a unique suffix and are never overwritten.

## Threat model and limitations

- A webhook URL is a secret bearer credential. Anyone who obtains it can post through that webhook.
- DPAPI protects the saved value at rest, but malware running as the same Windows user may still access process or user data.
- Release executables are currently unsigned. Users should download only from this repository's Releases page and verify published checksums when provided.
- The app does not moderate clip content or control who can view the destination Discord channel.
