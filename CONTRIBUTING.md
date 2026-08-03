# Contributing

Thanks for helping improve Clips to Discord.

## Development setup

1. Use Windows 10 or 11 with the .NET 8 SDK or newer.
2. Fork and clone the repository.
3. Build the Release configuration:

   ```powershell
   dotnet build .\ClipsToDiscord.csproj -c Release
   ```

4. Run the smoke tests:

   ```powershell
   dotnet run --project .\tests\ClipsToDiscord.SmokeTests\ClipsToDiscord.SmokeTests.csproj -c Release
   ```

5. Run the app from the build output or create a package with `scripts\package.ps1`.

6. To build the Windows installer, prepare the package layout and use the pinned compiler helper:

   ```powershell
   .\scripts\package.ps1
   .\scripts\build-installer.ps1
   ```

   The default build path downloads and validates the pinned compiler automatically. Release packages must bundle both FFmpeg and its license and pass `-RequireFfmpeg` to `build-installer.ps1`.

## Pull requests

- Start planned feature work from an open [`agent-ready`](https://github.com/malikpervez/clips-to-discord/issues?q=is%3Aissue%20state%3Aopen%20label%3Aagent-ready) issue in the [product roadmap](ROADMAP.md), and respect its dependencies.
- Use one focused branch and pull request per issue. Include `Closes #<issue-number>` in the pull request body.
- Keep incomplete or awaiting-review work in a draft pull request; do not create a release as part of an implementation issue.
- Keep changes focused and explain user-facing behavior.
- Never commit Discord webhook URLs, clips, local settings, FFmpeg binaries, or build artifacts.
- Add or update documentation when behavior changes.
- Confirm a Release build completes without warnings.
- Test first-run setup, Discord open/close transitions, successful upload moves, and failure retries when relevant.

## Bug reports

Include the app version, Windows version, relevant redacted log lines, and reproducible steps. Never include a real webhook URL. See [SECURITY.md](SECURITY.md) for vulnerabilities or credential exposure.
