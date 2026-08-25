using System.Collections.Concurrent;
using System.Text;

namespace ClipsToDiscord;

internal enum GalleryClipRoute
{
    Uploaded,
    LocalOnly
}

internal enum GalleryClipSource
{
    SteelSeriesGg,
    Nvidia,
    ClipCord
}

internal enum GalleryRenditionOutputStatus
{
    Waiting,
    Processing,
    Ready,
    Failed,
    Unavailable
}

internal enum GalleryRenditionAggregateStatus
{
    Pending,
    Processing,
    PartiallyReady,
    Ready,
    Failed
}

internal sealed record GalleryRenditionOutputPresentation(
    string OrientationId,
    string DisplayName,
    GalleryRenditionOutputStatus Status,
    string ArtifactPath,
    long Length,
    string? FailureReason)
{
    internal bool CanPlay
    {
        get
        {
            if (Status != GalleryRenditionOutputStatus.Ready || Length <= 0) return false;
            try
            {
                var file = new FileInfo(ArtifactPath);
                return file.Exists &&
                       file.Length == Length &&
                       file.LinkTarget is null &&
                       !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    internal bool CanRetry => Status == GalleryRenditionOutputStatus.Failed;

    internal string StatusLabel => Status switch
    {
        GalleryRenditionOutputStatus.Waiting => "Waiting",
        GalleryRenditionOutputStatus.Processing => "Rendering",
        GalleryRenditionOutputStatus.Ready => "Ready",
        GalleryRenditionOutputStatus.Failed => "Failed",
        _ => "Unavailable"
    };
}

internal sealed record GalleryRenditionPresentation(
    string LibraryRoot,
    string ProjectId,
    GalleryRenditionAggregateStatus Status,
    IReadOnlyList<GalleryRenditionOutputPresentation> Outputs,
    string GameplayPath,
    string CameraLayerPath,
    SilhouetteRenditionLoadStatus LoadStatus,
    GalleryRenditionStateStamp StateStamp,
    string? StatusMessage)
{
    internal int FormatCount => Outputs.Count;
    internal int ReadyCount => Outputs.Count(output => output.CanPlay);

    internal string FormatsLabel =>
        $"{FormatCount} format{(FormatCount == 1 ? string.Empty : "s")}";

    internal string StatusLabel => Status switch
    {
        GalleryRenditionAggregateStatus.Pending => "Preparing",
        GalleryRenditionAggregateStatus.Processing => "Processing",
        GalleryRenditionAggregateStatus.PartiallyReady =>
            $"{ReadyCount} of {FormatCount} ready",
        GalleryRenditionAggregateStatus.Ready => "Ready",
        _ => "Needs attention"
    };
}

internal readonly record struct GalleryRenditionStateStamp(
    bool Exists,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed class GalleryRenditionRetryRequestedEventArgs(
    string libraryRoot,
    string projectId,
    string orientationId) : EventArgs
{
    internal string LibraryRoot { get; } = libraryRoot;
    internal string ProjectId { get; } = projectId;
    internal string OrientationId { get; } = orientationId;
}

internal sealed record GalleryClipEntry(
    string Path,
    string FileName,
    string GameName,
    GalleryClipRoute Route,
    long Length,
    DateTime LastWriteTimeUtc,
    GalleryClipSource Source = GalleryClipSource.SteelSeriesGg,
    CaptureProjectSummary? CaptureProject = null,
    GalleryRenditionPresentation? Renditions = null)
{
    internal bool HasReactionCameraProject => CaptureProject is not null;
    internal bool HasRenditions => Renditions is { FormatCount: > 0 };

    internal string SourceLabel => Source switch
    {
        GalleryClipSource.Nvidia => "NVIDIA",
        GalleryClipSource.ClipCord => "ClipCord",
        _ => "SteelSeries GG"
    };
}

internal sealed record GalleryGameEntry(
    string Name,
    IReadOnlyList<GalleryClipEntry> Clips)
{
    internal int UploadedCount => Clips.Count(clip => clip.Route == GalleryClipRoute.Uploaded);
    internal int LocalOnlyCount => Clips.Count(clip => clip.Route == GalleryClipRoute.LocalOnly);
    internal long TotalBytes => Clips.Sum(clip => clip.Length);
}

internal sealed record GallerySnapshot(
    IReadOnlyList<GalleryGameEntry> Games,
    IReadOnlyList<string> Warnings)
{
    internal int TotalClips => Games.Sum(game => game.Clips.Count);
    internal int UploadedCount => Games.Sum(game => game.UploadedCount);
    internal int LocalOnlyCount => Games.Sum(game => game.LocalOnlyCount);
}

internal readonly record struct GalleryGradient(Color Start, Color End);

internal static class GalleryCatalog
{
    private const int MaximumRenditionValidationCacheEntries = 512;
    private static readonly ConcurrentDictionary<
        RenditionValidationCacheKey,
        RenditionValidationCacheEntry> RenditionValidationCache = new();

    private static readonly GalleryGradient[] Gradients =
    [
        new(Color.FromArgb(224, 75, 69), Color.FromArgb(32, 54, 92)),
        new(Color.FromArgb(139, 61, 255), Color.FromArgb(44, 105, 189)),
        new(Color.FromArgb(213, 159, 49), Color.FromArgb(43, 50, 46)),
        new(Color.FromArgb(34, 158, 150), Color.FromArgb(33, 59, 96)),
        new(Color.FromArgb(207, 69, 126), Color.FromArgb(76, 51, 133)),
        new(Color.FromArgb(42, 133, 198), Color.FromArgb(28, 41, 72)),
        new(Color.FromArgb(219, 105, 51), Color.FromArgb(88, 44, 81)),
        new(Color.FromArgb(73, 153, 83), Color.FromArgb(29, 58, 68))
    ];

    internal static GallerySnapshot Scan(
        string clipsFolder,
        CancellationToken cancellationToken,
        Action<string>? beforeGameDirectoryScan = null,
        string? captureLibraryRoot = null,
        GalleryClipSource externalSource = GalleryClipSource.SteelSeriesGg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsFolder);
        cancellationToken.ThrowIfCancellationRequested();

        var clips = new List<GalleryClipEntry>();
        var warnings = new List<string>();
        if (Directory.Exists(clipsFolder))
        {
            ScanArchive(
                ResolveArchive(clipsFolder, GalleryClipRoute.Uploaded, warnings),
                GalleryClipRoute.Uploaded,
                externalSource,
                clips,
                warnings,
                cancellationToken,
                beforeGameDirectoryScan);
            ScanArchive(
                ResolveArchive(clipsFolder, GalleryClipRoute.LocalOnly, warnings),
                GalleryClipRoute.LocalOnly,
                externalSource,
                clips,
                warnings,
                cancellationToken,
                beforeGameDirectoryScan);
        }
        else
        {
            warnings.Add("The external clips folder is not available.");
        }
        ScanClipCordLibrary(captureLibraryRoot, clips, warnings, cancellationToken);

        var games = clips
            .GroupBy(clip => clip.GameName.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase)
            .Select(group => new GalleryGameEntry(
                group.Key,
                group.OrderByDescending(clip => clip.LastWriteTimeUtc)
                    .ThenBy(clip => clip.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(game => game.Clips.Max(clip => clip.LastWriteTimeUtc))
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GallerySnapshot(games, warnings);
    }

    internal static GalleryGradient GetGradient(string gameName)
    {
        var normalized = (gameName ?? string.Empty).Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var hash = 2166136261u;
        foreach (var character in normalized)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return Gradients[(int)(hash % (uint)Gradients.Length)];
    }

    internal static string GetInitials(string gameName)
    {
        var words = (gameName ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
        {
            return new string(words[0].Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant() switch
            {
                "" => "?",
                var value => value
            };
        }

        var initials = words
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit))
            .Where(character => character != default)
            .Take(3)
            .ToArray();
        return initials.Length == 0 ? "?" : new string(initials).ToUpperInvariant();
    }

    private static void ScanArchive(
        string? archiveFolder,
        GalleryClipRoute route,
        GalleryClipSource source,
        ICollection<GalleryClipEntry> clips,
        ICollection<string> warnings,
        CancellationToken cancellationToken,
        Action<string>? beforeGameDirectoryScan)
    {
        if (archiveFolder is null) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(archiveFolder, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClip(path, UploadedFolder.GetGameFolderName(Path.GetFileName(path)), route, source, clips);
            }

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not scan root-level clips in the {route} Gallery archive.", exception);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(archiveFolder, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not enumerate game folders in the {route} Gallery archive.", exception);
            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The optional callback is a deterministic fault-injection seam for
                // testing a folder that disappears after archive enumeration.
                beforeGameDirectoryScan?.Invoke(directory);
                var directoryInfo = new DirectoryInfo(directory);
                if (directoryInfo.LinkTarget is not null) continue;
                var gameName = string.IsNullOrWhiteSpace(directoryInfo.Name)
                    ? "Uncategorized"
                    : directoryInfo.Name.Normalize(NormalizationForm.FormC);
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddClip(path, gameName, route, source, clips);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddArchiveWarning(route, warnings);
                Log.Error($"Could not scan a game folder in the {route} Gallery archive.", exception);
            }
        }
    }

    private static string? ResolveArchive(
        string clipsFolder,
        GalleryClipRoute route,
        ICollection<string> warnings)
    {
        try
        {
            return route == GalleryClipRoute.Uploaded
                ? UploadedFolder.FindExistingUploaded(clipsFolder)
                : UploadedFolder.FindExistingLocalOnly(clipsFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not resolve the {route} Gallery archive.", exception);
            return null;
        }
    }

    private static void AddArchiveWarning(GalleryClipRoute route, ICollection<string> warnings)
    {
        var warning = route == GalleryClipRoute.Uploaded
            ? "Some uploaded clips could not be read."
            : "Some local-only clips could not be read.";
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }

    private static void AddClip(
        string path,
        string gameName,
        GalleryClipRoute route,
        GalleryClipSource source,
        ICollection<GalleryClipEntry> clips,
        CaptureProjectSummary? captureProject = null,
        GalleryRenditionPresentation? renditions = null)
    {
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists ||
                file.LinkTarget is not null ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
            clips.Add(new GalleryClipEntry(
                file.FullName,
                file.Name,
                string.IsNullOrWhiteSpace(gameName) ? "Uncategorized" : gameName,
                route,
                Math.Max(0, file.Length),
                file.LastWriteTimeUtc,
                source,
                captureProject,
                renditions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not inspect Gallery clip {Path.GetFileName(path)}.", exception);
        }
    }

    private static void ScanClipCordLibrary(
        string? captureLibraryRoot,
        ICollection<GalleryClipEntry> clips,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captureLibraryRoot)) return;
        string library;
        string captureRoot;
        try
        {
            captureRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(captureLibraryRoot));
            library = Path.Combine(
                captureRoot,
                CaptureLibraryLayout.LibraryFolderName,
                CaptureLibraryLayout.GameFolderName);
        }
        catch
        {
            warnings.Add("The ClipCord Capture library path is invalid.");
            return;
        }
        if (!Directory.Exists(library)) return;
        try
        {
            if (!HasOrdinaryCaptureLibraryChain(captureRoot, library))
            {
                warnings.Add("The ClipCord Capture library cannot be read through a symbolic link or junction.");
                return;
            }
            var projectIndex = CaptureProjectStore.LoadProjectIndex(
                captureLibraryRoot,
                cancellationToken);
            var renditionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projectPresentations = new Dictionary<string, GalleryRenditionPresentation>(
                StringComparer.Ordinal);
            foreach (var project in projectIndex.Values)
            {
                GalleryRenditionPresentation presentation;
                try
                {
                    presentation = LoadRenditionPresentation(
                        captureRoot,
                        project,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Log.Error(
                        $"Could not inspect silhouette renditions for project {project.ProjectId}.",
                        exception);
                    presentation = CreateUnavailableRenditionPresentation(
                        captureRoot,
                        project);
                }
                projectPresentations[project.ProjectId] = presentation;
                foreach (var output in presentation.Outputs.Where(output => output.CanPlay))
                {
                    renditionPaths.Add(Path.GetFullPath(output.ArtifactPath));
                }
            }
            foreach (var gameDirectory in Directory.EnumerateDirectories(library, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(gameDirectory);
                if (directory.LinkTarget is not null ||
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                var gameName = string.IsNullOrWhiteSpace(directory.Name) ? "Uncategorized" : directory.Name;
                foreach (var path in Directory.EnumerateFiles(directory.FullName, "*.mp4", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Only exact, manifest-derived rendition paths are folded into their source
                    // project. Similar user filenames remain ordinary Gallery clips.
                    var normalizedPath = Path.GetFullPath(path);
                    if (renditionPaths.Contains(normalizedPath) &&
                        !projectIndex.ContainsKey(normalizedPath))
                    {
                        continue;
                    }
                    projectIndex.TryGetValue(normalizedPath, out var project);
                    GalleryRenditionPresentation? renditions = null;
                    if (project is not null)
                    {
                        projectPresentations.TryGetValue(project.ProjectId, out renditions);
                    }
                    AddClip(
                        path,
                        gameName,
                        GalleryClipRoute.LocalOnly,
                        GalleryClipSource.ClipCord,
                        clips,
                        project,
                        renditions);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!warnings.Contains("Some ClipCord recordings could not be read."))
            {
                warnings.Add("Some ClipCord recordings could not be read.");
            }
            Log.Error("Could not scan the ClipCord Capture library.", exception);
        }
    }

    internal static GalleryRenditionPresentation LoadRenditionPresentation(
        string libraryRoot,
        CaptureProjectSummary project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(libraryRoot.Trim()));
        var stateStamp = GetRenditionStateStamp(normalizedRoot, project.ProjectId);
        var load = SilhouetteRenditionStore.Load(
            normalizedRoot,
            project.ProjectId,
            cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
        {
            var fallbackOrientations = LoadEnabledOrientations(normalizedRoot, project);
            var unavailable = load.Status is not SilhouetteRenditionLoadStatus.Missing;
            var fallbackOutputs = fallbackOrientations
                .Select(orientationId => new GalleryRenditionOutputPresentation(
                    orientationId,
                    GetOrientationName(orientationId),
                    unavailable
                        ? GalleryRenditionOutputStatus.Unavailable
                        : GalleryRenditionOutputStatus.Waiting,
                    SilhouetteArtifactStore.GetOutputPath(
                        normalizedRoot,
                        project.ProjectId,
                        orientationId),
                    Length: 0,
                    unavailable ? "Rendition status is unavailable." : null))
                .ToArray();
            return new GalleryRenditionPresentation(
                normalizedRoot,
                project.ProjectId,
                unavailable
                    ? GalleryRenditionAggregateStatus.Failed
                    : GalleryRenditionAggregateStatus.Pending,
                fallbackOutputs,
                project.GameplayPath,
                project.CameraLayerPath,
                load.Status,
                stateStamp,
                unavailable
                    ? "ClipCord could not safely read this project's rendition status."
                    : "Reaction Camera formats are waiting to be processed.");
        }

        var document = load.Document;
        var enabledOrientations = CompositionOrientationIds.All
            .Where(orientationId => document.Outputs.Any(output =>
                output.OrientationId.Equals(orientationId, StringComparison.Ordinal) &&
                output.State != SilhouetteOutputState.Disabled))
            .ToArray();
        var outputs = new List<GalleryRenditionOutputPresentation>(enabledOrientations.Length);
        foreach (var orientationId in enabledOrientations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = document.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            outputs.Add(BuildOutputPresentation(
                normalizedRoot,
                project.ProjectId,
                document.Matte.State,
                output,
                cancellationToken));
        }
        var aggregate = GetAggregateStatus(document.Matte.State, outputs);
        return new GalleryRenditionPresentation(
            normalizedRoot,
            project.ProjectId,
            aggregate,
            outputs,
            project.GameplayPath,
            project.CameraLayerPath,
            load.Status,
            stateStamp,
            GetAggregateMessage(aggregate, outputs.Count(output => output.CanPlay), outputs.Count));
    }

    private static IReadOnlyList<string> LoadEnabledOrientations(
        string libraryRoot,
        CaptureProjectSummary project)
    {
        var composition = CaptureCompositionStore.LoadOrDefault(
            libraryRoot,
            project.ProjectId);
        var enabled = composition.Document.SilhouetteLayouts
            .Where(layout => layout.Enabled)
            .Select(layout => layout.ProfileId)
            .Where(orientationId =>
                CompositionOrientationIds.All.Contains(orientationId, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return CompositionOrientationIds.All
            .Where(enabled.Contains)
            .ToArray();
    }

    private static GalleryRenditionOutputPresentation BuildOutputPresentation(
        string libraryRoot,
        string projectId,
        SilhouetteMatteState matteState,
        SilhouetteOutputRendition output,
        CancellationToken cancellationToken)
    {
        var artifactPath = SilhouetteArtifactStore.GetOutputPath(
            libraryRoot,
            projectId,
            output.OrientationId);
        if (output.State == SilhouetteOutputState.Ready)
        {
            if (output.FinalArtifact is null)
            {
                return FailedOutput(output, artifactPath, "Saved rendition identity is missing.");
            }
            SilhouetteArtifactValidationResult validation;
            try
            {
                validation = ValidateOutputCached(
                    libraryRoot,
                    projectId,
                    output,
                    artifactPath,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"Could not validate the {GetOrientationName(output.OrientationId)} rendition for project {projectId}.",
                    exception);
                return new GalleryRenditionOutputPresentation(
                    output.OrientationId,
                    GetOrientationName(output.OrientationId),
                    GalleryRenditionOutputStatus.Unavailable,
                    artifactPath,
                    Length: 0,
                    "Rendition validation is temporarily unavailable.");
            }
            if (!validation.IsValid)
            {
                return FailedOutput(
                    output,
                    artifactPath,
                    validation.Status == SilhouetteArtifactValidationStatus.Missing
                        ? "The saved rendition file is missing."
                        : "The saved rendition file changed and must be rebuilt.");
            }
            return new GalleryRenditionOutputPresentation(
                output.OrientationId,
                GetOrientationName(output.OrientationId),
                GalleryRenditionOutputStatus.Ready,
                artifactPath,
                Math.Max(0, validation.ObservedFingerprint?.ByteLength ??
                    output.FinalArtifact.ByteLength),
                FailureReason: null);
        }

        if (output.State == SilhouetteOutputState.Failed ||
            matteState == SilhouetteMatteState.Failed)
        {
            return FailedOutput(
                output,
                artifactPath,
                matteState == SilhouetteMatteState.Failed
                    ? "Reaction Camera preparation failed."
                    : GetFailureReason(output.ErrorCode));
        }

        return new GalleryRenditionOutputPresentation(
            output.OrientationId,
            GetOrientationName(output.OrientationId),
            output.State is SilhouetteOutputState.Rendering or SilhouetteOutputState.Committing
                ? GalleryRenditionOutputStatus.Processing
                : GalleryRenditionOutputStatus.Waiting,
            artifactPath,
            Length: 0,
            FailureReason: null);
    }

    private static GalleryRenditionOutputPresentation FailedOutput(
        SilhouetteOutputRendition output,
        string artifactPath,
        string failureReason) => new(
        output.OrientationId,
        GetOrientationName(output.OrientationId),
        GalleryRenditionOutputStatus.Failed,
        artifactPath,
        Length: 0,
        failureReason);

    private static SilhouetteArtifactValidationResult ValidateOutputCached(
        string libraryRoot,
        string projectId,
        SilhouetteOutputRendition output,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        var expected = output.FinalArtifact ??
            throw new InvalidDataException("The ready rendition fingerprint is missing.");
        var normalizedPath = Path.GetFullPath(artifactPath);
        var file = new FileInfo(normalizedPath);
        file.Refresh();
        long? observedLengthBefore = null;
        DateTime? observedLastWriteBefore = null;
        if (file.Exists &&
            file.LinkTarget is null &&
            !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            observedLengthBefore = file.Length;
            observedLastWriteBefore = file.LastWriteTimeUtc;
            var key = new RenditionValidationCacheKey(
                normalizedPath,
                expected.ByteLength,
                expected.Sha256.ToLowerInvariant());
            if (RenditionValidationCache.TryGetValue(key, out var cached) &&
                cached.ObservedLength == file.Length &&
                cached.ObservedLastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return new SilhouetteArtifactValidationResult(
                    SilhouetteArtifactValidationStatus.Valid,
                    expected);
            }
        }

        var validation = SilhouetteArtifactStore.ValidateOutputAsync(
                libraryRoot,
                projectId,
                output.OrientationId,
                expected,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (!validation.IsValid)
        {
            RemoveCachedValidation(normalizedPath);
            return validation;
        }

        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Length != expected.ByteLength ||
            observedLengthBefore is not null &&
            (observedLengthBefore.Value != file.Length ||
             observedLastWriteBefore != file.LastWriteTimeUtc))
        {
            RemoveCachedValidation(normalizedPath);
            return new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Mismatch,
                ObservedFingerprint: null);
        }

        if (RenditionValidationCache.Count >= MaximumRenditionValidationCacheEntries)
        {
            RenditionValidationCache.Clear();
        }
        RenditionValidationCache[new RenditionValidationCacheKey(
            normalizedPath,
            expected.ByteLength,
            expected.Sha256.ToLowerInvariant())] = new RenditionValidationCacheEntry(
            file.Length,
            file.LastWriteTimeUtc);
        return validation;
    }

    private static void RemoveCachedValidation(string artifactPath)
    {
        foreach (var key in RenditionValidationCache.Keys)
        {
            if (key.ArtifactPath.Equals(artifactPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = RenditionValidationCache.TryRemove(key, out _);
            }
        }
    }

    private static GalleryRenditionPresentation CreateUnavailableRenditionPresentation(
        string libraryRoot,
        CaptureProjectSummary project)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        IReadOnlyList<string> orientations;
        try
        {
            orientations = LoadEnabledOrientations(normalizedRoot, project);
        }
        catch
        {
            orientations = [CompositionOrientationIds.Landscape];
        }
        var outputs = orientations.Select(orientationId =>
            new GalleryRenditionOutputPresentation(
                orientationId,
                GetOrientationName(orientationId),
                GalleryRenditionOutputStatus.Unavailable,
                SilhouetteArtifactStore.GetOutputPath(
                    normalizedRoot,
                    project.ProjectId,
                    orientationId),
                Length: 0,
                "Rendition status is temporarily unavailable."))
            .ToArray();
        return new GalleryRenditionPresentation(
            normalizedRoot,
            project.ProjectId,
            GalleryRenditionAggregateStatus.Failed,
            outputs,
            project.GameplayPath,
            project.CameraLayerPath,
            SilhouetteRenditionLoadStatus.Unavailable,
            GetRenditionStateStamp(normalizedRoot, project.ProjectId),
            "ClipCord could not safely inspect this project's rendition status.");
    }

    internal static GalleryRenditionStateStamp GetRenditionStateStamp(
        string libraryRoot,
        string projectId)
    {
        try
        {
            var file = new FileInfo(SilhouetteRenditionStore.GetPath(libraryRoot, projectId));
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return default;
            }
            return new GalleryRenditionStateStamp(
                Exists: true,
                file.Length,
                file.LastWriteTimeUtc);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    private static GalleryRenditionAggregateStatus GetAggregateStatus(
        SilhouetteMatteState matteState,
        IReadOnlyList<GalleryRenditionOutputPresentation> outputs)
    {
        if (matteState is SilhouetteMatteState.Processing or SilhouetteMatteState.Committing ||
            outputs.Any(output => output.Status == GalleryRenditionOutputStatus.Processing))
        {
            return GalleryRenditionAggregateStatus.Processing;
        }
        if (outputs.Count > 0 && outputs.All(output => output.Status == GalleryRenditionOutputStatus.Ready))
        {
            return GalleryRenditionAggregateStatus.Ready;
        }
        if (outputs.Any(output => output.Status == GalleryRenditionOutputStatus.Ready))
        {
            return GalleryRenditionAggregateStatus.PartiallyReady;
        }
        if (matteState == SilhouetteMatteState.Failed ||
            outputs.Any(output => output.Status is GalleryRenditionOutputStatus.Failed or
                GalleryRenditionOutputStatus.Unavailable))
        {
            return GalleryRenditionAggregateStatus.Failed;
        }
        return GalleryRenditionAggregateStatus.Pending;
    }

    private static string GetAggregateMessage(
        GalleryRenditionAggregateStatus status,
        int readyCount,
        int formatCount) => status switch
    {
        GalleryRenditionAggregateStatus.Pending => "Reaction Camera formats are waiting to be processed.",
        GalleryRenditionAggregateStatus.Processing => "Reaction Camera formats are processing locally.",
        GalleryRenditionAggregateStatus.PartiallyReady =>
            $"{readyCount} of {formatCount} formats is ready. The other format needs attention.",
        GalleryRenditionAggregateStatus.Ready => "All requested Reaction Camera formats are ready.",
        _ => "Reaction Camera formats need attention. Nothing was uploaded."
    };

    private static string GetFailureReason(string? errorCode) => errorCode switch
    {
        "output-invalid" => "The saved rendition file is missing or changed.",
        "render-failed" => "ClipCord could not render this format.",
        "matte-processing-failed" => "Reaction Camera preparation failed.",
        _ => "This format could not be rendered."
    };

    internal static string GetOrientationName(string orientationId) => orientationId switch
    {
        CompositionOrientationIds.Landscape => "Landscape",
        CompositionOrientationIds.Portrait => "Portrait",
        _ => throw new ArgumentException(
            "The silhouette rendition orientation is not supported.",
            nameof(orientationId))
    };

    private readonly record struct RenditionValidationCacheKey(
        string ArtifactPath,
        long ExpectedLength,
        string ExpectedSha256);

    private readonly record struct RenditionValidationCacheEntry(
        long ObservedLength,
        DateTime ObservedLastWriteTimeUtc);

    internal static bool HasOrdinaryCaptureLibraryChain(
        string captureRoot,
        string gameLibrary,
        Func<string, bool>? ordinaryDirectoryProbe = null)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(captureRoot));
        var normalizedLibrary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibrary));
        var relative = Path.GetRelativePath(normalizedRoot, normalizedLibrary);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        ordinaryDirectoryProbe ??= static path =>
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists &&
                   directory.LinkTarget is null &&
                   !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        };
        var current = normalizedRoot;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            if (component.Length > 0) current = Path.Combine(current, component);
            if (!ordinaryDirectoryProbe(current)) return false;
        }
        return true;
    }
}
