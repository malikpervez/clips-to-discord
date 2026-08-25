using System.Text;

namespace ClipsToDiscord;

internal enum CaptureResolution
{
    FullHd1080p = 0,
    QuadHd1440p = 1,
    UltraHd4K = 2
}

internal sealed record CaptureProfile(
    CaptureResolution Resolution,
    int FramesPerSecond,
    TimeSpan ReplayDuration,
    bool IncludeReactionCamera = false)
{
    internal const int MinimumReplaySeconds = 15;
    internal const int MaximumReplaySeconds = 300;

    public bool IsValid =>
        Enum.IsDefined(Resolution) &&
        FramesPerSecond is 30 or 60 &&
        ReplayDuration.TotalSeconds is >= MinimumReplaySeconds and <= MaximumReplaySeconds;
}

internal sealed record CaptureSizeEstimate(
    long ExpectedBytes,
    long LowerBoundBytes,
    long UpperBoundBytes,
    int VideoBitrateKbps,
    int AudioBitrateKbps,
    int ReactionCameraBitrateKbps);

internal static class CaptureProfileCatalog
{
    // Initial H.264 targets for the Phase 0 benchmark. They intentionally favor editing quality;
    // measured hardware-encoder results may retune them before the settings UI ships.
    private static readonly IReadOnlyDictionary<(CaptureResolution Resolution, int Fps), int>
        VideoBitratesKbps = new Dictionary<(CaptureResolution, int), int>
        {
            [(CaptureResolution.FullHd1080p, 30)] = 12_000,
            [(CaptureResolution.FullHd1080p, 60)] = 20_000,
            [(CaptureResolution.QuadHd1440p, 30)] = 24_000,
            [(CaptureResolution.QuadHd1440p, 60)] = 35_000,
            [(CaptureResolution.UltraHd4K, 30)] = 45_000,
            [(CaptureResolution.UltraHd4K, 60)] = 65_000
        };

    internal const int MixedAudioBitrateKbps = 192;
    internal const int ReactionCameraBitrateKbps = 6_000;

    internal static (int Width, int Height) GetDimensions(CaptureResolution resolution) =>
        resolution switch
        {
            CaptureResolution.FullHd1080p => (1920, 1080),
            CaptureResolution.QuadHd1440p => (2560, 1440),
            CaptureResolution.UltraHd4K => (3840, 2160),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };

    internal static string GetDisplayName(CaptureResolution resolution) =>
        resolution switch
        {
            CaptureResolution.FullHd1080p => "1080p",
            CaptureResolution.QuadHd1440p => "1440p",
            CaptureResolution.UltraHd4K => "4K",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };

    internal static int GetVideoBitrateKbps(CaptureResolution resolution, int framesPerSecond)
    {
        if (!VideoBitratesKbps.TryGetValue((resolution, framesPerSecond), out var bitrate))
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                "ClipCord does not define a bitrate for this capture profile.");
        }

        return bitrate;
    }

    internal static CaptureSizeEstimate Estimate(CaptureProfile profile, bool includeAudio = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid) throw new ArgumentException("The capture profile is invalid.", nameof(profile));

        var videoBitrate = VideoBitratesKbps[(profile.Resolution, profile.FramesPerSecond)];
        // Game, microphone, and voice-chat inputs are mixed before encoding. The resulting
        // clip has one audio stream regardless of how many inputs the user enables.
        var audioBitrate = includeAudio ? MixedAudioBitrateKbps : 0;
        var cameraBitrate = profile.IncludeReactionCamera ? ReactionCameraBitrateKbps : 0;
        var totalBitrate = videoBitrate + audioBitrate + cameraBitrate;
        var expectedBytes = EstimateBytes(totalBitrate, profile.ReplayDuration);

        // Hardware encoders use variable bitrate. Present a useful range rather than implying an
        // exact file size before the content has been encoded.
        var lowerBoundBytes = ScaleSaturating(expectedBytes, 0.80);
        var upperBoundBytes = ScaleSaturating(expectedBytes, 1.25);
        return new CaptureSizeEstimate(
            expectedBytes,
            lowerBoundBytes,
            upperBoundBytes,
            videoBitrate,
            audioBitrate,
            cameraBitrate);
    }

    private static long EstimateBytes(int totalBitrateKbps, TimeSpan duration)
    {
        var bytes = totalBitrateKbps * 1000d / 8d * duration.TotalSeconds;
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bytes);
    }

    private static long ScaleSaturating(long value, double multiplier)
    {
        var scaled = value * multiplier;
        return scaled >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(scaled);
    }
}

internal static class CaptureLibraryLayout
{
    internal const string LibraryFolderName = "Library";
    internal const string GameFolderName = "Game";
    internal const string ExportsFolderName = "Exports";
    internal const string PrivateDataFolderName = ".clipcord";
    internal const string ProjectsFolderName = "Projects";
    internal const string StagingFolderName = "Staging";
    internal const string ThumbnailsFolderName = "Thumbnails";

    internal static string GetDefaultRoot()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(videos, "ClipCord");
    }

    internal static string GetRecordingDirectory(
        string root,
        string? gameName)
    {
        var safeRoot = ValidateRoot(root);
        var safeGameName = UploadedFolder.SanitizeGameFolderName(gameName);
        var gameRoot = Path.Combine(
            safeRoot,
            LibraryFolderName,
            GameFolderName);
        return ResolveEstablishedGameDirectory(gameRoot, safeGameName);
    }

    private static string ResolveEstablishedGameDirectory(string gameRoot, string safeGameName)
    {
        var proposed = Path.Combine(gameRoot, safeGameName);
        if (!Directory.Exists(gameRoot)) return proposed;

        var identity = CreateGameIdentity(safeGameName);
        if (identity.Length == 0) return proposed;
        try
        {
            var established = Directory.EnumerateDirectories(gameRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Where(directory =>
                    directory.LinkTarget is null &&
                    (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    CreateGameIdentity(directory.Name).Equals(identity, StringComparison.Ordinal))
                .OrderBy(directory => directory.CreationTimeUtc)
                .ThenBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return established?.FullName ?? proposed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return proposed;
        }
    }

    internal static string CreateGameIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var identity = new StringBuilder(value.Length);
        var withoutMarks = value
            .Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("©", string.Empty, StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormKC)
            .Trim();
        if (withoutMarks.Length > 2 &&
            withoutMarks.EndsWith("TM", StringComparison.OrdinalIgnoreCase) &&
            !char.IsLetterOrDigit(withoutMarks[^3]))
        {
            withoutMarks = withoutMarks[..^2].TrimEnd(' ', '-', '_', '.');
        }
        foreach (var character in withoutMarks)
        {
            if (char.IsLetterOrDigit(character))
            {
                identity.Append(char.ToUpperInvariant(character));
            }
        }
        return identity.ToString();
    }

    internal static string GetExportDirectory(string root, string destination, string? gameName)
    {
        var safeRoot = ValidateRoot(root);
        var safeDestination = UploadedFolder.SanitizeGameFolderName(destination);
        var safeGameName = UploadedFolder.SanitizeGameFolderName(gameName);
        return Path.Combine(safeRoot, ExportsFolderName, safeDestination, safeGameName);
    }

    internal static string GetProjectsDirectory(string root) =>
        Path.Combine(ValidateRoot(root), PrivateDataFolderName, ProjectsFolderName);

    internal static string GetStagingDirectory(string root) =>
        Path.Combine(ValidateRoot(root), PrivateDataFolderName, StagingFolderName);

    internal static string GetThumbnailsDirectory(string root) =>
        Path.Combine(ValidateRoot(root), PrivateDataFolderName, ThumbnailsFolderName);

    private static string ValidateRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root);
    }
}
