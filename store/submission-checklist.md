# Microsoft Store submission checklist

## Reserved product identity

- Product name: ClipCord
- Store ID: `9MWKB7KDCB2R`
- Package identity: `DKGLabs.ClipCord`
- Publisher: `CN=3BF1D083-8330-4BB1-A011-C31DD2E3487F`
- Publisher display name: `DKG Labs`
- Store link after publication: `https://apps.microsoft.com/detail/9MWKB7KDCB2R`

These values come from Partner Center's Product identity page. Do not substitute a local test publisher in a package uploaded to this product.

## Suggested listing

- Category: Utilities & tools
- Short description: Watch, organize, edit, and share completed gaming clips.
- Support URL: `https://github.com/malikpervez/clips-to-discord/issues`
- Privacy policy URL: `https://github.com/malikpervez/clips-to-discord/blob/main/docs/PRIVACY.md`
- Website: `https://github.com/malikpervez/clips-to-discord`
- Search terms: gaming clips; gameplay; Discord; video clips; clip editor; replay

### Description

ClipCord keeps completed gaming clips organized and ready to share.

- Watch a folder used by SteelSeries GG, NVIDIA, or another recorder that saves MP4 files.
- Send new clips to a configured Discord webhook or keep them Local only.
- Browse locally generated thumbnails and play archived clips inside the app.
- Mark clips as Favorites and browse them together without moving or duplicating the original files.
- Trim, preview, mute, rename, and explicitly upload a Local-only clip.
- Preserve upload history and prevent duplicate posting with local content hashes.
- Keep settings, history, thumbnails, editing, playback preparation, and compression on the PC.

ClipCord has no analytics, advertising, account system, telemetry service, or project-operated server. Discord uploads occur only through the webhook configured by the user. ClipCord is not affiliated with Discord or any recording-software vendor.

## Capability explanations

### `runFullTrust`

ClipCord is a traditional WinForms/WPF notification-area desktop application and declares `runFullTrust` so it can monitor a user-selected folder for completed MP4 files, launch its bundled FFmpeg process for local thumbnails/playback/editing/compression, register an optional global hotkey, integrate with the Windows notification area, and organize files into user-visible local archive folders. It runs at medium integrity as the current user, does not request elevation, and installs no service, driver, browser extension, or shell extension.

### `graphicsCaptureWithoutBorder`

ClipCord declares `graphicsCaptureWithoutBorder` for its optional ClipCord Capture feature. After the user chooses a game window, or after ClipCord locally recognizes the foreground game for Instant Replay, ClipCord uses Windows Graphics Capture to record that fixed game window. The capability lets ClipCord request that Windows omit the system capture border while recording, so the indicator does not cover or distract from live gameplay. ClipCord requests borderless access through the Windows app-capability API; if Windows does not grant it, capture remains available with the normal Windows capture indicator. Manual selection continues to use the Windows capture picker, automatic capture rejects non-game windows, and a recording never follows focus to another window or monitor. The capability does not elevate the app. Gameplay frames remain on the PC unless the user later sends a completed clip through an enabled route. See [Privacy and security — File handling](../docs/PRIVACY.md#file-handling).

### `webcam`

ClipCord declares `webcam` only for its optional Reaction Camera feature. Reaction Camera is off by default, requires the user's explicit in-app consent, and remains subject to Windows camera privacy controls. ClipCord opens only the camera selected by the user, and only for a requested consent preview or while a manual recording or recognized-game Instant Replay buffer is actively capturing. Unsaved replay camera frames remain in bounded memory; saved camera media is processed locally as a separate reaction layer and is not uploaded by itself. If camera access is denied or unavailable, gameplay capture continues without the camera. See [Privacy and security — File handling](../docs/PRIVACY.md#file-handling).

These explanations are the capability-purpose text to provide with a Store submission. They do not state or imply that Microsoft has approved any capability for a particular submission.

## Certification notes

- No ClipCord account is required.
- A Discord webhook is optional. For a no-network test, choose any writable temporary folder, turn off **Upload new clips to Discord**, and save.
- ClipCord runs in the notification area. Double-click the notification-area icon or use its context menu to reopen the app.
- **Check for updates** opens Microsoft Store's downloads and updates surface in this distribution.
- **Start with Windows** uses the declared `ClipCordStartup` startup task and can be disabled in ClipCord or Windows Settings.
- The package contains the pinned FFmpeg build and its matching license. Third-party notices are included in `THIRD_PARTY_NOTICES.md`.

## Before submission

1. Build the exact merge-commit package with `scripts/build-store-msix.ps1` and require FFmpeg.
2. Run the warning-as-error build, full smoke suite, and `scripts/test-store-msix.ps1 -RequireFfmpeg`.
3. Install a locally trusted test package or use a Partner Center private audience to verify first launch, settings migration, startup toggling, Store update routing, and uninstall.
4. Upload the `.msix` from the verified CI artifact. Do not upload the unsigned package as a public direct download; Microsoft signs the accepted Store package.
5. Upload current 1800×1140 Home, Gallery, Player, Editor, Activity, Settings, and About screenshots.
6. Complete the age-rating questionnaire truthfully for a utility that opens user-owned local videos and can send a selected clip to a user-configured Discord destination.
7. Review the final pricing/availability and submission summary, then obtain explicit approval before selecting **Submit for certification**.
