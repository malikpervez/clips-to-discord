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

## Restricted capability explanation

ClipCord is a traditional WinForms/WPF notification-area desktop application and declares `runFullTrust` so it can monitor a user-selected folder for completed MP4 files, launch its bundled FFmpeg process for local thumbnails/playback/editing/compression, register an optional global hotkey, integrate with the Windows notification area, and organize files into user-visible local archive folders. It runs at medium integrity as the current user, does not request elevation, and installs no service, driver, browser extension, or shell extension.

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
