namespace ClipsToDiscord;

internal static class CaptureStagingRecovery
{
    internal static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(24);
    private const string ManualCapturePrefix = "manual-capture-";
    private const string ReplaySavePrefix = "replay-save-";

    internal static int RemoveOrphanedManualCaptures(
        string libraryRoot,
        DateTimeOffset? now = null,
        TimeSpan? minimumAge = null)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)) return 0;

        var stagingPath = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        DirectoryInfo stagingDirectory;
        try
        {
            stagingDirectory = new DirectoryInfo(stagingPath);
            if (!stagingDirectory.Exists ||
                stagingDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not inspect its capture staging folder.", exception);
            return 0;
        }

        var cutoffUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime -
            (minimumAge ?? DefaultMinimumAge);
        var removed = 0;
        FileInfo[] candidates;
        try
        {
            candidates = stagingDirectory
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    file.Name.StartsWith(ManualCapturePrefix, StringComparison.Ordinal) ||
                    file.Name.StartsWith(ReplaySavePrefix, StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not enumerate its capture staging folder.", exception);
            return 0;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (!IsOwnedCaptureStage(candidate) ||
                    candidate.LastWriteTimeUtc > cutoffUtc)
                {
                    continue;
                }

                candidate.Delete();
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Log.Error("ClipCord could not remove an abandoned capture staging file.", exception);
            }
        }

        return removed;
    }

    internal static int RemoveOrphanedReactionCameraProjects(
        string libraryRoot,
        DateTimeOffset? now = null,
        TimeSpan? minimumAge = null)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)) return 0;
        DirectoryInfo projectsDirectory;
        try
        {
            projectsDirectory = new DirectoryInfo(
                CaptureLibraryLayout.GetProjectsDirectory(libraryRoot));
            if (!projectsDirectory.Exists ||
                projectsDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not inspect its capture project folder.", exception);
            return 0;
        }

        var cutoffUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime -
            (minimumAge ?? DefaultMinimumAge);
        DirectoryInfo[] candidates;
        try
        {
            candidates = projectsDirectory
                .EnumerateDirectories(".capture-project-*.tmp", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not enumerate its temporary capture projects.", exception);
            return 0;
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!IsOwnedTemporaryProject(candidate) ||
                    candidate.LastWriteTimeUtc > cutoffUtc)
                {
                    continue;
                }
                var entries = candidate.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                    .ToArray();
                if (entries.Any(entry =>
                        entry is not FileInfo ||
                        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        entry.Name is not (CaptureProjectStore.CameraLayerFileName or
                            CaptureProjectStore.ManifestFileName or
                            CaptureCompositionStore.FileName)))
                {
                    continue;
                }
                foreach (var file in entries.Cast<FileInfo>()) file.Delete();
                candidate.Delete(recursive: false);
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Log.Error("ClipCord could not remove an abandoned temporary camera project.", exception);
            }
        }
        return removed;
    }

    private static bool IsOwnedTemporaryProject(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        const string prefix = ".capture-project-";
        const string suffix = ".tmp";
        if (!directory.Name.StartsWith(prefix, StringComparison.Ordinal) ||
            !directory.Name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var identity = directory.Name[prefix.Length..^suffix.Length];
        if (identity.Length != 65 || identity[32] != '-') return false;
        var projectId = identity[..32];
        var operationId = identity[33..];
        return projectId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            Guid.TryParseExact(operationId, "N", out _);
    }

    private static bool IsOwnedCaptureStage(FileInfo file)
    {
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        if (file.Name.StartsWith(ReplaySavePrefix, StringComparison.Ordinal))
        {
            return IsOwnedReplayStage(file.Name);
        }
        if (!file.Name.StartsWith(ManualCapturePrefix, StringComparison.Ordinal)) return false;
        var suffix = new[]
        {
            ".camera.mp4",
            ".video.mp4",
            ".game.wav",
            ".microphone.wav",
            ".chat.wav",
            ".mp4"
        }.FirstOrDefault(candidate => file.Name.EndsWith(candidate, StringComparison.Ordinal));
        if (suffix is null) return false;
        var tokenLength = file.Name.Length - ManualCapturePrefix.Length - suffix.Length;
        if (tokenLength <= 0) return false;
        var token = file.Name.Substring(ManualCapturePrefix.Length, tokenLength);
        return Guid.TryParseExact(token, "N", out _);
    }

    private static bool IsOwnedReplayStage(string fileName)
    {
        if (fileName.Length <= ReplaySavePrefix.Length + 32) return false;
        var token = fileName.Substring(ReplaySavePrefix.Length, 32);
        if (!Guid.TryParseExact(token, "N", out _)) return false;
        var suffix = fileName[(ReplaySavePrefix.Length + 32)..];
        if (suffix is ".concat.txt" or ".video.mp4" or ".mp4" or
            ".camera.concat.txt" or ".camera.mp4" or
            ".game.wav" or ".microphone.wav" or ".chat.wav")
        {
            return true;
        }
        if (IsNumberedMp4Suffix(suffix)) return true;
        const string cameraPrefix = ".camera";
        return suffix.StartsWith(cameraPrefix, StringComparison.Ordinal) &&
            IsNumberedMp4Suffix(suffix[cameraPrefix.Length..]);
    }

    private static bool IsNumberedMp4Suffix(string suffix) =>
        suffix.Length == 9 && suffix[0] == '.' &&
        suffix.AsSpan(1, 4).IndexOfAnyExceptInRange('0', '9') < 0 &&
        suffix.EndsWith(".mp4", StringComparison.Ordinal);
}
