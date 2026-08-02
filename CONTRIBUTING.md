# Contributing

Thanks for helping improve Clips to Discord.

## Development setup

1. Use Windows 10 or 11 with the .NET 8 SDK or newer.
2. Fork and clone the repository.
3. Build the Release configuration:

   ```powershell
   dotnet build .\ClipsToDiscord.csproj -c Release
   ```

4. Run the app from the build output or create a package with `scripts\package.ps1`.

## Pull requests

- Keep changes focused and explain user-facing behavior.
- Never commit Discord webhook URLs, clips, local settings, FFmpeg binaries, or build artifacts.
- Add or update documentation when behavior changes.
- Confirm a Release build completes without warnings.
- Test first-run setup, Discord open/close transitions, successful upload moves, and failure retries when relevant.

## Bug reports

Include the app version, Windows version, relevant redacted log lines, and reproducible steps. Never include a real webhook URL. See [SECURITY.md](SECURITY.md) for vulnerabilities or credential exposure.
