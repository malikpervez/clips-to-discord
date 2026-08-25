using ClipsToDiscord;

internal sealed record SilhouettePipelineProbeProject(
    string LibraryRoot,
    string ProjectId);

/// <summary>
/// Creates a disposable, fully validated schema-v2 capture project from real media so a developer
/// can run the production `--silhouette-worker` path without modifying the user's capture library.
/// The destination is intentionally restricted to the current user's temporary directory.
/// </summary>
internal static class SilhouettePipelineProbe
{
    internal static SilhouettePipelineProbeProject CreateProject(
        string gameplaySource,
        string cameraSource,
        string requestedRoot)
    {
        var gameplay = RequireOrdinaryMp4(gameplaySource, "gameplay source");
        var camera = RequireOrdinaryMp4(cameraSource, "camera source");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var relative = Path.GetRelativePath(temporaryRoot, root);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The silhouette pipeline probe must use a disposable folder beneath the current user's temporary directory.");
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("The silhouette pipeline probe destination must be empty.");
        }

        var gameDirectory = Path.Combine(
            root,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName,
            "Silhouette Probe");
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var gameplayPath = Path.Combine(
            gameDirectory,
            $"Silhouette-Probe__{DateTime.UtcNow:yyyy-MM-dd__HH-mm-ss}.mp4");
        var cameraStage = Path.Combine(stagingDirectory, $"camera-{Guid.NewGuid():N}.mp4");
        File.Copy(gameplay, gameplayPath, overwrite: false);
        File.Copy(camera, cameraStage, overwrite: false);

        var ffmpeg = FfmpegCompressor.FindExecutable() ??
            throw new FileNotFoundException("The silhouette pipeline probe requires ffmpeg.exe.");
        var gameplayProbe = FfmpegCompressor.ProbeMediaAsync(
                gameplayPath,
                ffmpeg,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var cameraProbe = FfmpegCompressor.ProbeMediaAsync(
                cameraStage,
                ffmpeg,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var duration = gameplayProbe.Duration <= cameraProbe.Duration
            ? gameplayProbe.Duration
            : cameraProbe.Duration;
        if (duration <= TimeSpan.FromSeconds(0.25))
        {
            throw new InvalidDataException("The probe media is too short for silhouette processing.");
        }

        var settings = CaptureSettings.Default with
        {
            LibraryRoot = root,
            SilhouetteLandscapeEnabled = true,
            SilhouettePortraitEnabled = true
        };
        var projectId = CaptureProjectStore.CreateProjectId(root, gameplayPath);
        var composition = SilhouettePreferencesModel.CreateProjectSnapshot(
            projectId,
            SilhouettePreferencesModel.CreateDefault(mirrorCamera: true),
            landscapeEnabled: true,
            portraitEnabled: true,
            mirrorCamera: true);
        var project = CaptureProjectStore.SaveReactionCameraLayerAsync(
                root,
                gameplayPath,
                cameraStage,
                gameplayStart: TimeSpan.Zero,
                cameraStart: TimeSpan.Zero,
                duration,
                mirrorCamera: true,
                composition,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new SilhouettePipelineProbeProject(root, project.ProjectId);
    }

    private static string RequireOrdinaryMp4(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        var file = new FileInfo(normalized);
        if (!file.Exists ||
            file.Length <= 0 ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.LinkTarget is not null ||
            !file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {description} must be an ordinary non-empty MP4 file.");
        }
        return file.FullName;
    }
}
