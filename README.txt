CLIPS TO DISCORD — PORTABLE PACKAGE

1. Keep every file from this ZIP together in one folder.
2. Run ClipsToDiscord.exe.
3. Choose the folder where your clipping tool saves finished MP4 clips.
4. Paste your Discord webhook URL.
5. Enter the name Discord should show with your uploaded clips.
6. Choose a compression target (new settings default to 95 MB).
7. Test the webhook, then save.

The app stays in the Windows notification area. It watches for clips only while
Discord desktop is open. Each Discord message identifies the uploader and parsed
game name. After upload, the original moves into "uploaded\<game name>"; filenames
without a recognized game/timestamp structure use "uploaded\Uncategorized".

The app works with any tool that saves MP4 clips into the selected folder.

Keep your webhook URL private. If it is exposed, rotate or delete it in Discord.

The app checks the official GitHub Releases API at most once per day for a newer
stable release. It never downloads or installs an update silently. Use "Check for
updates" in Settings for an immediate check; update actions open the official
GitHub release page.

This app is not affiliated with Discord or any recording-software vendor.

The bundled FFmpeg 8.1.2 essentials build is provided by Gyan.dev under GPLv3.
Its license is in FFMPEG-LICENSE.txt. The corresponding FFmpeg source revision is:
https://github.com/FFmpeg/FFmpeg/commit/38b88335f9

For an automatic per-user installation, download ClipsToDiscord-Setup.exe from
the GitHub Releases page instead of this portable ZIP.
