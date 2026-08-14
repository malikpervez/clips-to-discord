using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipsToDiscord;

internal enum AboutWatcherState
{
    Starting,
    Watching,
    Preparing,
    Uploading,
    Compressing,
    Archiving,
    LocalOnly,
    Paused,
    NeedsAttention,
    SetupRequired,
    Unavailable
}

internal enum AboutInstallationType
{
    Installed,
    Portable
}

internal enum AboutLink
{
    Repository,
    ReportProblem,
    ReleaseNotes,
    Roadmap,
    Privacy,
    SecurityDesign,
    ProjectLicense,
    ThirdPartyNotices,
    Troubleshooting
}

internal readonly record struct AboutWatcherPresentation(
    AboutWatcherState State,
    string Label,
    string Detail);

internal sealed record AboutRuntimeFacts(
    Version ApplicationVersion,
    Version OperatingSystemVersion,
    Architecture OperatingSystemArchitecture,
    Architecture ProcessArchitecture,
    Version RuntimeVersion,
    string ExecutablePath,
    string LocalAppDataRoot,
    bool FfmpegAvailable,
    bool DiscordRunning,
    DateTimeOffset CapturedAtUtc)
{
    internal static AboutRuntimeFacts Capture(
        Version applicationVersion,
        string executablePath,
        string localAppDataRoot,
        bool ffmpegAvailable,
        bool discordRunning) => new(
            applicationVersion,
            Environment.OSVersion.Version,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            Environment.Version,
            executablePath,
            localAppDataRoot,
            ffmpegAvailable,
            discordRunning,
            DateTimeOffset.UtcNow);
}

internal sealed record AboutStatusSnapshot
{
    private AboutStatusSnapshot(
        string version,
        AboutWatcherState watcherState,
        string watcher,
        string watcherDetail,
        string routing,
        string routingDetail,
        string startup,
        string startupDetail,
        string installation,
        string installationDetail,
        string discord,
        string ffmpeg,
        string operatingSystem,
        string architecture,
        string runtime,
        DateTimeOffset capturedAtUtc)
    {
        Version = version;
        WatcherState = watcherState;
        Watcher = watcher;
        WatcherDetail = watcherDetail;
        Routing = routing;
        RoutingDetail = routingDetail;
        Startup = startup;
        StartupDetail = startupDetail;
        Installation = installation;
        InstallationDetail = installationDetail;
        Discord = discord;
        Ffmpeg = ffmpeg;
        OperatingSystem = operatingSystem;
        Architecture = architecture;
        Runtime = runtime;
        CapturedAtUtc = capturedAtUtc;
    }

    internal string Version { get; }
    internal AboutWatcherState WatcherState { get; }
    internal string Watcher { get; }
    internal string WatcherDetail { get; }
    internal string Routing { get; }
    internal string RoutingDetail { get; }
    internal string Startup { get; }
    internal string StartupDetail { get; }
    internal string Installation { get; }
    internal string InstallationDetail { get; }
    internal string Discord { get; }
    internal string Ffmpeg { get; }
    internal string OperatingSystem { get; }
    internal string Architecture { get; }
    internal string Runtime { get; }
    internal DateTimeOffset CapturedAtUtc { get; }

    internal static AboutStatusSnapshot Create(
        AppSettings settings,
        string? rawWatcherStatus,
        AboutRuntimeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.ApplicationVersion);
        ArgumentNullException.ThrowIfNull(facts.OperatingSystemVersion);
        ArgumentNullException.ThrowIfNull(facts.RuntimeVersion);

        var watcher = AboutPageSupport.NormalizeWatcherStatus(rawWatcherStatus, facts.DiscordRunning);
        var installationType = AboutPageSupport.ClassifyInstallation(
            facts.ExecutablePath,
            facts.LocalAppDataRoot);

        return new AboutStatusSnapshot(
            AboutPageSupport.FormatApplicationVersion(facts.ApplicationVersion),
            watcher.State,
            watcher.Label,
            watcher.Detail,
            settings.UploadToDiscord ? "Discord uploads" : "Local only",
            settings.UploadToDiscord ? "Automatic routing" : "Discord uploads are disabled",
            "Start with Windows",
            settings.StartWithWindows ? "Enabled" : "Disabled",
            installationType == AboutInstallationType.Installed ? "Installed copy" : "Portable copy",
            installationType == AboutInstallationType.Installed
                ? "Per-user installation"
                : "Runs from its current folder",
            facts.DiscordRunning ? "Open" : "Closed",
            facts.FfmpegAvailable ? "Available" : "Unavailable",
            $"Windows {AboutPageSupport.FormatVersion(facts.OperatingSystemVersion)}",
            $"{facts.OperatingSystemArchitecture} OS / {facts.ProcessArchitecture} process",
            $".NET {AboutPageSupport.FormatVersion(facts.RuntimeVersion)}",
            facts.CapturedAtUtc.ToUniversalTime());
    }
}

internal static class AboutPageSupport
{
    private const string RepositoryRoot = "https://github.com/malikpervez/clips-to-discord";
    private const string RepositoryPath = "/malikpervez/clips-to-discord";

    internal static AboutWatcherPresentation NormalizeWatcherStatus(
        string? rawStatus,
        bool discordRunning)
    {
        var status = (rawStatus ?? string.Empty).Trim();

        // The remainder of several live status strings is a user-controlled clip name.
        // Classify only the fixed prefix/suffix emitted by ClipCord so words such as
        // "Failed", "Setup", or "Closed" inside a game title cannot change the result.
        if (StartsWithAny(
                status,
                "Watcher error",
                "Upload needs attention",
                "Upload failed",
                "Local-only save failed"))
        {
            return Presentation(
                AboutWatcherState.NeedsAttention,
                "Needs attention",
                "Clip processing needs attention");
        }

        if (StartsWithAny(status, "Setup required", "Not configured", "Configuration required"))
        {
            return Presentation(
                AboutWatcherState.SetupRequired,
                "Setup required",
                "Finish setup to start watching");
        }

        if (StartsWithAny(status, "Discord closed", "Paused"))
        {
            return Presentation(
                AboutWatcherState.Paused,
                "Paused",
                status.StartsWith("Discord closed", StringComparison.OrdinalIgnoreCase)
                    ? "Discord is closed"
                    : discordRunning ? "Clip processing is paused" : "Discord is closed");
        }

        if (status.StartsWith("Discord open — local-only mode", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Saved locally — local-only mode", StringComparison.OrdinalIgnoreCase) ||
            (status.StartsWith("Saving ", StringComparison.OrdinalIgnoreCase) &&
             status.EndsWith(" locally", StringComparison.OrdinalIgnoreCase)))
        {
            return Presentation(
                AboutWatcherState.LocalOnly,
                "Local only",
                "Saving new clips on this PC");
        }

        if (status.StartsWith("Local-only move pending —", StringComparison.OrdinalIgnoreCase) ||
            (status.StartsWith("Uploaded ", StringComparison.OrdinalIgnoreCase) &&
             status.EndsWith(" — archive move pending", StringComparison.OrdinalIgnoreCase)) ||
            status.StartsWith("Archiving ", StringComparison.OrdinalIgnoreCase))
        {
            return Presentation(
                AboutWatcherState.Archiving,
                "Archiving",
                "Organizing a completed clip");
        }

        if (StartsWithAny(status, "Compressing ", "Encoding "))
        {
            return Presentation(
                AboutWatcherState.Compressing,
                "Compressing",
                "Reducing a clip for Discord");
        }

        if (StartsWithAny(status, "Hashing ", "Preparing clip"))
        {
            return Presentation(
                AboutWatcherState.Preparing,
                "Preparing clip",
                "Preparing a completed clip");
        }

        if (StartsWithAny(status, "Applying settings", "Starting", "Restarting"))
        {
            return Presentation(
                AboutWatcherState.Starting,
                "Starting",
                "Starting clip monitoring");
        }

        if (status.StartsWith("Discord open — watching for clips", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Upload complete — watching for clips", StringComparison.OrdinalIgnoreCase))
        {
            return Presentation(
                AboutWatcherState.Watching,
                "Watching",
                "Discord is open");
        }

        if (status.StartsWith("Uploading ", StringComparison.OrdinalIgnoreCase))
        {
            return Presentation(
                AboutWatcherState.Uploading,
                "Uploading",
                "Sending a clip to Discord");
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return Presentation(
                AboutWatcherState.Starting,
                "Starting",
                "Starting clip monitoring");
        }

        return Presentation(
            AboutWatcherState.Unavailable,
            "Status unavailable",
            "Status is temporarily unavailable");
    }

    internal static AboutInstallationType ClassifyInstallation(
        string executablePath,
        string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);

        var actualExecutable = Path.GetFullPath(executablePath);
        var expectedExecutable = Path.GetFullPath(Path.Combine(
            localAppDataRoot,
            "Programs",
            "ClipsToDiscord",
            "ClipsToDiscord.exe"));

        return string.Equals(actualExecutable, expectedExecutable, StringComparison.OrdinalIgnoreCase)
            ? AboutInstallationType.Installed
            : AboutInstallationType.Portable;
    }

    internal static string BuildDiagnostics(AboutStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Every value below is produced from a fixed label, enum, boolean, Version, or UTC
        // timestamp. Paths, clip names, logs, webhook URLs, and the uploader name never enter
        // the snapshot. Central redaction remains the final defense if this list evolves.
        var diagnostics = string.Join(Environment.NewLine,
        [
            "ClipCord diagnostics",
            $"Version: {snapshot.Version}",
            $"Operating system: {snapshot.OperatingSystem}",
            $"Architecture: {snapshot.Architecture}",
            $"Runtime: {snapshot.Runtime}",
            $"Install type: {snapshot.Installation}",
            $"Watcher: {snapshot.Watcher}",
            $"Route: {snapshot.Routing}",
            $"Discord: {snapshot.Discord}",
            $"Start with Windows: {snapshot.StartupDetail}",
            $"FFmpeg: {snapshot.Ffmpeg}",
            $"Captured (UTC): {snapshot.CapturedAtUtc:O}"
        ]);

        return SensitiveDataRedactor.Redact(diagnostics);
    }

    internal static ProcessStartInfo CreateTrustedLinkStartInfo(AboutLink link)
    {
        var target = link switch
        {
            AboutLink.Repository => RepositoryRoot,
            AboutLink.ReportProblem => RepositoryRoot + "/issues/new/choose",
            AboutLink.ReleaseNotes => RepositoryRoot + "/blob/main/CHANGELOG.md",
            AboutLink.Roadmap => RepositoryRoot + "/blob/main/ROADMAP.md",
            AboutLink.Privacy => RepositoryRoot + "/blob/main/docs/PRIVACY.md",
            AboutLink.SecurityDesign => RepositoryRoot + "/blob/main/docs/ARCHITECTURE.md",
            AboutLink.ProjectLicense => RepositoryRoot + "/blob/main/LICENSE",
            AboutLink.ThirdPartyNotices => RepositoryRoot + "/blob/main/THIRD_PARTY_NOTICES.md",
            AboutLink.Troubleshooting => RepositoryRoot + "/blob/main/docs/TROUBLESHOOTING.md",
            _ => throw new ArgumentOutOfRangeException(nameof(link), link, "Unknown About link.")
        };

        var uri = new Uri(target, UriKind.Absolute);
        if (!IsTrustedProjectUri(uri))
        {
            throw new InvalidOperationException("An About link is not on the trusted GitHub origin.");
        }

        return new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
    }

    internal static bool IsTrustedProjectUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (string.Equals(uri.AbsolutePath.TrimEnd('/'), RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.StartsWith(RepositoryPath + "/", StringComparison.OrdinalIgnoreCase));

    internal static ProcessStartInfo CreateOpenDataFolderStartInfo(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return ActivityView.CreateOpenFolderStartInfo(dataDirectory);
    }

    internal static ProcessStartInfo CreateOpenLogsStartInfo(
        string dataDirectory,
        bool logFileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var logPath = Path.Combine(dataDirectory, "app.log");
        return logFileExists
            ? ActivityView.CreateSelectFileStartInfo(logPath)
            : ActivityView.CreateOpenFolderStartInfo(dataDirectory);
    }

    internal static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Revision >= 0) return version.ToString(4);
        if (version.Build >= 0) return version.ToString(3);
        return version.ToString(2);
    }

    internal static string FormatApplicationVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }

    private static AboutWatcherPresentation Presentation(
        AboutWatcherState state,
        string label,
        string detail) => new(state, label, detail);

    private static bool StartsWithAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
}
