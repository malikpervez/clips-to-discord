using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal sealed record CaptureProjectManifest(
    int SchemaVersion,
    string ProjectId,
    string GameplayRelativePath,
    string CameraLayerRelativePath,
    CaptureMediaFingerprint GameplayFingerprint,
    CaptureMediaFingerprint CameraLayerFingerprint,
    long CameraStartOffsetTicks,
    long DurationTicks,
    bool MirrorCamera,
    DateTimeOffset CreatedUtc)
{
    [JsonIgnore]
    internal TimeSpan CameraStartOffset => TimeSpan.FromTicks(CameraStartOffsetTicks);

    [JsonIgnore]
    internal TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}

/// <summary>
/// Content identity for an immutable capture-project input. A path by itself is not an
/// identity: the byte length is a cheap rejection gate and SHA-256 binds the exact bytes.
/// </summary>
internal sealed record CaptureMediaFingerprint(
    long ByteLength,
    string Sha256);

internal sealed record CaptureProjectSaveResult(
    string ProjectId,
    string ProjectDirectory,
    string ManifestPath,
    string CameraLayerPath,
    CaptureProjectManifest Manifest)
{
    internal string CompositionPath => Path.Combine(
        ProjectDirectory,
        CaptureCompositionStore.FileName);
}

internal sealed record CaptureProjectSummary(
    string ProjectId,
    string GameplayPath,
    string ManifestPath,
    string CameraLayerPath,
    TimeSpan CameraStartOffset,
    TimeSpan Duration,
    bool MirrorCamera)
{
    internal string CompositionPath => Path.Combine(
        Path.GetDirectoryName(ManifestPath)!,
        CaptureCompositionStore.FileName);
}

internal readonly record struct CaptureCommittedMediaIdentity(
    string GameplayPath,
    string CameraLayerPath);

/// <summary>
/// Resolves the immutable composition document that the capture transaction will commit.
/// The UI process resolves reusable preferences at capture start and carries them in settings;
/// the optional argument remains an explicit seam for in-process callers and deterministic tests.
/// This layer never reaches back into mutable global preferences while a clip is committing.
/// Schema defaults are used only by direct callers that have no UI-host boundary.
/// </summary>
internal static class CaptureCompositionSnapshotFactory
{
    internal static CaptureCompositionDocument Create(
        CaptureSettings settings,
        string finalGameplayPath,
        bool mirrorCamera,
        DateTimeOffset capturedAt,
        SilhouettePreferencesDocument? resolvedPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalGameplayPath);
        var projectId = CaptureProjectStore.CreateProjectId(
            settings.LibraryRoot,
            finalGameplayPath);
        var preferences = resolvedPreferences ??
            settings.SilhouettePreferencesSnapshot ??
            SilhouettePreferencesModel.CreateDefault(mirrorCamera, capturedAt);
        return SilhouettePreferencesModel.CreateProjectSnapshot(
            projectId,
            preferences,
            settings.SilhouetteLandscapeEnabled,
            settings.SilhouettePortraitEnabled,
            mirrorCamera,
            capturedAt);
    }
}

/// <summary>
/// Atomically promotes a completed reaction-camera stage into ClipCord's private project store.
/// The visible gameplay MP4 is an immutable input: it is never moved, renamed, or rewritten.
/// </summary>
internal static class CaptureProjectStore
{
    internal const int CurrentSchemaVersion = 2;
    internal const string ManifestFileName = "project.json";
    internal const string CameraLayerFileName = "reaction-camera.mp4";
    internal const int MaximumManifestBytes = 128 * 1024;
    internal static readonly TimeSpan DefaultOrphanRetention = TimeSpan.FromDays(30);

    private const string TemporaryProjectPrefix = ".capture-project-";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions CompositionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    internal static async Task<CaptureProjectSaveResult> SaveReactionCameraLayerAsync(
        string libraryRoot,
        string finalGameplayPath,
        string stagedCameraMp4Path,
        TimeSpan gameplayStart,
        TimeSpan cameraStart,
        TimeSpan duration,
        bool mirrorCamera,
        CaptureCompositionDocument compositionSnapshot,
        DateTimeOffset? createdUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalGameplayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedCameraMp4Path);
        ArgumentNullException.ThrowIfNull(compositionSnapshot);
        if (gameplayStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gameplayStart));
        }
        if (cameraStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cameraStart));
        }
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeRoot(libraryRoot);
        var normalizedGameplayPath = NormalizeRequiredFile(
            normalizedRoot,
            finalGameplayPath,
            "gameplay recording");
        var stagingRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(CaptureLibraryLayout.GetStagingDirectory(normalizedRoot)));
        EnsurePathIsInside(normalizedRoot, stagingRoot, "capture staging folder");
        EnsureOrdinaryExistingPath(normalizedRoot, stagingRoot, requireDirectory: true, "capture staging folder");
        if (IsSameOrInside(stagingRoot, normalizedGameplayPath))
        {
            throw new IOException("The completed gameplay recording cannot remain in private capture staging.");
        }
        var normalizedCameraStage = NormalizeRequiredFile(
            stagingRoot,
            stagedCameraMp4Path,
            "reaction-camera stage");
        if (normalizedCameraStage.Equals(normalizedGameplayPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The gameplay recording and reaction-camera stage must be separate files.");
        }
        if (!Path.GetExtension(normalizedGameplayPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(normalizedCameraStage).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Capture project media must use the MP4 container.");
        }
        if (new FileInfo(normalizedCameraStage).Length <= 0)
        {
            throw new InvalidDataException("The staged reaction-camera layer was empty.");
        }
        if (new FileInfo(normalizedGameplayPath).Length <= 0)
        {
            throw new InvalidDataException("The completed gameplay recording was empty.");
        }

        var gameplayRelativePath = ToPortableRelativePath(normalizedRoot, normalizedGameplayPath);
        var projectId = CreateProjectIdFromRelativeGameplayPath(gameplayRelativePath);
        var projectsRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(CaptureLibraryLayout.GetProjectsDirectory(normalizedRoot)));
        EnsurePathIsInside(normalizedRoot, projectsRoot, "capture projects folder");
        EnsureOrdinaryAncestorsBeforeCreate(normalizedRoot, projectsRoot, "capture projects folder");
        Directory.CreateDirectory(projectsRoot);
        EnsureOrdinaryExistingPath(normalizedRoot, projectsRoot, requireDirectory: true, "capture projects folder");

        var projectDirectory = GetProjectDirectory(normalizedRoot, projectId);
        if (Directory.Exists(projectDirectory) || File.Exists(projectDirectory))
        {
            throw new IOException("A reaction-camera project already exists for that gameplay recording.");
        }

        var temporaryName = $"{TemporaryProjectPrefix}{projectId}-{Guid.NewGuid():N}.tmp";
        var temporaryDirectory = Path.Combine(projectsRoot, temporaryName);
        var stagedProjectCamera = Path.Combine(temporaryDirectory, CameraLayerFileName);
        var stagedManifest = Path.Combine(temporaryDirectory, ManifestFileName);
        var stagedComposition = Path.Combine(
            temporaryDirectory,
            CaptureCompositionStore.FileName);
        var cameraLayerPath = GetCameraLayerPath(normalizedRoot, projectId);
        var manifestPath = GetManifestPath(normalizedRoot, projectId);
        var cameraRelativePath = ToPortableRelativePath(normalizedRoot, cameraLayerPath);
        var normalizedComposition = CaptureCompositionModel.Normalize(
            compositionSnapshot,
            projectId,
            mirrorCamera);
        var compositionBytes = SerializeBoundedUtf8(
            normalizedComposition,
            CompositionJsonOptions,
            CaptureCompositionStore.MaximumDocumentBytes,
            "capture composition snapshot");
        CaptureProjectManifest manifest;

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            EnsureOwnedTemporaryDirectory(projectsRoot, temporaryDirectory, projectId);
            cancellationToken.ThrowIfCancellationRequested();
            // Capture staging and Projects are both beneath the same normalized library root, so
            // this is a same-volume atomic rename rather than an unbounded post-gameplay copy.
            File.Move(normalizedCameraStage, stagedProjectCamera);
            var gameplayFingerprint = await CreateFingerprintAsync(
                normalizedGameplayPath,
                cancellationToken).ConfigureAwait(false);
            var cameraFingerprint = await CreateFingerprintAsync(
                stagedProjectCamera,
                cancellationToken).ConfigureAwait(false);
            manifest = new CaptureProjectManifest(
                CurrentSchemaVersion,
                projectId,
                gameplayRelativePath,
                cameraRelativePath,
                gameplayFingerprint,
                cameraFingerprint,
                checked((cameraStart - gameplayStart).Ticks),
                duration.Ticks,
                mirrorCamera,
                (createdUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
            var manifestBytes = SerializeBoundedUtf8(
                manifest,
                ManifestJsonOptions,
                MaximumManifestBytes,
                "capture project manifest");
            await File.WriteAllBytesAsync(
                stagedComposition,
                compositionBytes,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                stagedManifest,
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Directory.Move is an atomic rename because both directories are beneath the same
            // Projects root. Nothing after this point is allowed to turn a committed project into
            // a failed gameplay save.
            Directory.Move(temporaryDirectory, projectDirectory);
            TryDeletePromotedStage(normalizedCameraStage, stagingRoot);
            return new CaptureProjectSaveResult(
                projectId,
                projectDirectory,
                manifestPath,
                cameraLayerPath,
                manifest);
        }
        catch
        {
            TryRemoveOwnedTemporaryDirectory(projectsRoot, temporaryDirectory, projectId);
            throw;
        }
    }

    internal static string CreateProjectId(string libraryRoot, string finalGameplayPath)
    {
        var normalizedRoot = NormalizeRoot(libraryRoot);
        var normalizedGameplayPath = Path.GetFullPath(finalGameplayPath);
        EnsurePathIsInside(normalizedRoot, normalizedGameplayPath, "gameplay recording");
        return CreateProjectIdFromRelativeGameplayPath(
            ToPortableRelativePath(normalizedRoot, normalizedGameplayPath));
    }

    internal static string CreateProjectIdFromRelativeGameplayPath(string gameplayRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameplayRelativePath);
        var normalized = gameplayRelativePath
            .Replace('\\', '/')
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.Length == 0 ||
            Path.IsPathRooted(normalized) ||
            normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            components.Length == 0 ||
            components.Any(component => component is "." or ".."))
        {
            throw new ArgumentException(
                "The gameplay path must be relative to the ClipCord library.",
                nameof(gameplayRelativePath));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static string GetProjectDirectory(string libraryRoot, string projectId)
    {
        ValidateProjectId(projectId);
        return Path.Combine(CaptureLibraryLayout.GetProjectsDirectory(libraryRoot), projectId);
    }

    internal static string GetManifestPath(string libraryRoot, string projectId) =>
        Path.Combine(GetProjectDirectory(libraryRoot, projectId), ManifestFileName);

    internal static string GetCameraLayerPath(string libraryRoot, string projectId) =>
        Path.Combine(GetProjectDirectory(libraryRoot, projectId), CameraLayerFileName);

    /// <summary>
    /// Re-derives both immutable media paths and verifies their schema-v2 content identities.
    /// Project-aware callers such as the composition editor use this gate instead of trusting
    /// a deterministic path alone. A mismatch is invalid data, never a request to adopt the
    /// bytes currently occupying that path.
    /// </summary>
    internal static CaptureCommittedMediaIdentity ValidateCommittedMediaIdentity(
        string libraryRoot,
        string expectedProjectId,
        CaptureProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateProjectId(expectedProjectId);
        var normalizedRoot = NormalizeRoot(libraryRoot);
        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            manifest.DurationTicks <= 0 ||
            !string.Equals(manifest.ProjectId, expectedProjectId, StringComparison.Ordinal) ||
            !CreateProjectIdFromRelativeGameplayPath(manifest.GameplayRelativePath)
                .Equals(expectedProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The capture project manifest does not match its project.");
        }

        var gameplayPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            manifest.GameplayRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsurePathIsInside(normalizedRoot, gameplayPath, "project gameplay recording");
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            gameplayPath,
            requireDirectory: false,
            "project gameplay recording");

        var cameraPath = Path.GetFullPath(GetCameraLayerPath(
            normalizedRoot,
            expectedProjectId));
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            cameraPath,
            requireDirectory: false,
            "reaction-camera layer");
        var expectedCameraRelativePath = ToPortableRelativePath(normalizedRoot, cameraPath);
        if (!string.Equals(
                manifest.CameraLayerRelativePath,
                expectedCameraRelativePath,
                StringComparison.Ordinal) ||
            !Path.GetExtension(gameplayPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(cameraPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            !FingerprintMatches(
                gameplayPath,
                manifest.GameplayFingerprint,
                cancellationToken) ||
            !FingerprintMatches(
                cameraPath,
                manifest.CameraLayerFingerprint,
                cancellationToken))
        {
            throw new InvalidDataException(
                "The capture project media no longer matches its committed identity.");
        }

        return new CaptureCommittedMediaIdentity(gameplayPath, cameraPath);
    }

    /// <summary>
    /// Builds a validated, gameplay-path keyed view of camera projects for Gallery/editor use.
    /// A manifest is never allowed to redirect either media path: both are re-derived from
    /// the library root and deterministic project identity before the project is exposed.
    /// Invalid or incomplete projects degrade to an ordinary gameplay clip.
    /// </summary>
    internal static IReadOnlyDictionary<string, CaptureProjectSummary> LoadProjectIndex(
        string libraryRoot,
        CancellationToken cancellationToken = default,
        Action<string>? beforeProjectDirectoryRead = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projects = new Dictionary<string, CaptureProjectSummary>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(libraryRoot)) return projects;

        string normalizedRoot;
        DirectoryInfo projectsRoot;
        try
        {
            normalizedRoot = NormalizeRoot(libraryRoot);
            projectsRoot = new DirectoryInfo(CaptureLibraryLayout.GetProjectsDirectory(normalizedRoot));
            if (!projectsRoot.Exists ||
                projectsRoot.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return projects;
            }
            EnsureOrdinaryExistingPath(
                normalizedRoot,
                projectsRoot.FullName,
                requireDirectory: true,
                "capture projects folder");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("ClipCord could not inspect saved camera projects for editing.", exception);
            return projects;
        }

        DirectoryInfo[] candidates;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates = projectsRoot.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not enumerate saved camera projects for editing.", exception);
            return projects;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeProjectDirectoryRead?.Invoke(candidate.FullName);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!IsProjectId(candidate.Name) ||
                    candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var manifestPath = GetManifestPath(normalizedRoot, candidate.Name);
                var cameraPath = GetCameraLayerPath(normalizedRoot, candidate.Name);
                var compositionPath = Path.Combine(
                    candidate.FullName,
                    CaptureCompositionStore.FileName);
                EnsureOrdinaryExistingPath(
                    normalizedRoot,
                    manifestPath,
                    requireDirectory: false,
                    "capture project manifest");
                EnsureOrdinaryExistingPath(
                    normalizedRoot,
                    cameraPath,
                    requireDirectory: false,
                    "reaction-camera layer");
                EnsureOrdinaryExistingPath(
                    normalizedRoot,
                    compositionPath,
                    requireDirectory: false,
                    "capture composition snapshot");

                var manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                    ReadBoundedManifestFile(manifestPath),
                    ManifestJsonOptions);
                if (manifest is null)
                {
                    continue;
                }
                var mediaIdentity = ValidateCommittedMediaIdentity(
                    normalizedRoot,
                    candidate.Name,
                    manifest,
                    cancellationToken);
                if (!CompositionSnapshotMatches(
                        compositionPath,
                        candidate.Name,
                        manifest.MirrorCamera))
                {
                    continue;
                }

                projects.TryAdd(
                    mediaIdentity.GameplayPath,
                    new CaptureProjectSummary(
                        candidate.Name,
                        mediaIdentity.GameplayPath,
                        manifestPath,
                        mediaIdentity.CameraLayerPath,
                        manifest.CameraStartOffset,
                        manifest.Duration,
                        manifest.MirrorCamera));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or ArgumentException)
            {
                Log.Error("ClipCord ignored a camera project it could not safely validate.", exception);
            }
        }

        return projects;
    }

    internal static int RemoveOrphanedProjects(
        string libraryRoot,
        DateTimeOffset? now = null,
        TimeSpan? minimumAge = null,
        CancellationToken cancellationToken = default,
        Action? beforeDelete = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(libraryRoot)) return 0;
        string normalizedRoot;
        DirectoryInfo projectsRoot;
        try
        {
            normalizedRoot = NormalizeRoot(libraryRoot);
            projectsRoot = new DirectoryInfo(
                CaptureLibraryLayout.GetProjectsDirectory(normalizedRoot));
            if (!projectsRoot.Exists ||
                projectsRoot.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("ClipCord could not inspect saved Reaction Camera projects.", exception);
            return 0;
        }

        var cutoffUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime -
            (minimumAge ?? DefaultOrphanRetention);
        DirectoryInfo[] candidates;
        try
        {
            candidates = projectsRoot.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not enumerate saved Reaction Camera projects.", exception);
            return 0;
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!IsProjectId(candidate.Name) ||
                    candidate.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    candidate.LastWriteTimeUtc > cutoffUtc)
                {
                    continue;
                }
                var entries = candidate.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                    .ToArray();
                 if (entries.Any(entry =>
                         entry is not FileInfo ||
                         entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                         entry.Name is not (ManifestFileName or CameraLayerFileName or
                             CaptureCompositionStore.FileName or
                             SilhouetteRenditionStore.FileName) &&
                         !SilhouetteArtifactStore.IsOwnedProjectFileName(entry.Name)))
                 {
                    continue;
                }
                var manifestFile = entries.OfType<FileInfo>()
                    .SingleOrDefault(file => file.Name == ManifestFileName);
                if (manifestFile is null) continue;
                var manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                    ReadBoundedManifestFile(manifestFile.FullName),
                    ManifestJsonOptions);
                if (manifest is null ||
                    manifest.SchemaVersion != CurrentSchemaVersion ||
                    !string.Equals(manifest.ProjectId, candidate.Name, StringComparison.Ordinal) ||
                    !CreateProjectIdFromRelativeGameplayPath(manifest.GameplayRelativePath)
                        .Equals(candidate.Name, StringComparison.Ordinal))
                {
                    continue;
                }
                var gameplayPath = Path.GetFullPath(Path.Combine(
                    normalizedRoot,
                    manifest.GameplayRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                EnsurePathIsInside(normalizedRoot, gameplayPath, "project gameplay recording");
                if (File.Exists(gameplayPath)) continue;

                foreach (var file in entries.Cast<FileInfo>())
                {
                    beforeDelete?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    file.Delete();
                }
                beforeDelete?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                candidate.Delete(recursive: false);
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or ArgumentException)
            {
                Log.Error("ClipCord preserved a camera project it could not safely reconcile.", exception);
            }
        }
        return removed;
    }

    private static string NormalizeRoot(string libraryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The ClipCord library folder does not exist.");
        }
        EnsureOrdinaryExistingPath(root, root, requireDirectory: true, "ClipCord library folder");
        return root;
    }

    internal static string ReadBoundedManifestFile(string path) =>
        ReadBoundedUtf8File(
            path,
            MaximumManifestBytes,
            "capture project manifest");

    internal static string ReadBoundedUtf8File(
        string path,
        int maximumBytes,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {description} has an invalid size.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                throw new IOException($"The {description} changed while it was read.");
            }
            total += read;
        }
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"The {description} is too large.");
        }

        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The {description} is not valid UTF-8.", exception);
        }
    }

    private static byte[] SerializeBoundedUtf8<T>(
        T value,
        JsonSerializerOptions options,
        int maximumBytes,
        string description)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        if (bytes.Length <= 0 || bytes.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {description} has an invalid size.");
        }
        return bytes;
    }

    private static async Task<CaptureMediaFingerprint> CreateFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var byteLength = stream.Length;
        if (byteLength <= 0)
        {
            throw new InvalidDataException("A capture project media source was empty.");
        }
        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (stream.Length != byteLength)
        {
            throw new IOException("A capture project media source changed while it was fingerprinted.");
        }
        return new CaptureMediaFingerprint(
            byteLength,
            Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static bool FingerprintMatches(
        string path,
        CaptureMediaFingerprint? expected,
        CancellationToken cancellationToken)
    {
        if (!IsValidFingerprint(expected)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expected!.ByteLength) return false;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        if (stream.Length != expected.ByteLength) return false;
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return actual.Equals(expected.Sha256, StringComparison.Ordinal);
    }

    private static bool IsValidFingerprint(CaptureMediaFingerprint? fingerprint) =>
        fingerprint is
        {
            ByteLength: > 0,
            Sha256.Length: 64
        } &&
        fingerprint.Sha256.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CompositionSnapshotMatches(
        string path,
        string projectId,
        bool mirrorCamera)
    {
        var json = ReadBoundedUtf8File(
            path,
            CaptureCompositionStore.MaximumDocumentBytes,
            "capture composition snapshot");
        var document = JsonSerializer.Deserialize<CaptureCompositionDocument>(
            json,
            CompositionJsonOptions);
        if (document is null) return false;
        _ = CaptureCompositionModel.Normalize(document, projectId, mirrorCamera);
        return true;
    }

    private static string NormalizeRequiredFile(string allowedRoot, string path, string description)
    {
        var normalized = Path.GetFullPath(path.Trim());
        EnsurePathIsInside(allowedRoot, normalized, description);
        if (!File.Exists(normalized))
        {
            throw new FileNotFoundException($"The {description} does not exist.", normalized);
        }
        EnsureOrdinaryExistingPath(allowedRoot, normalized, requireDirectory: false, description);
        return normalized;
    }

    private static void EnsurePathIsInside(string allowedRoot, string candidate, string description)
    {
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        if (relative.Equals(".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} must remain inside its ClipCord-owned folder.");
        }
    }

    private static bool IsSameOrInside(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative.Equals(".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void EnsureOrdinaryAncestorsBeforeCreate(
        string allowedRoot,
        string candidate,
        string description)
    {
        EnsurePathIsInside(allowedRoot, candidate, description);
        EnsureNotReparsePoint(allowedRoot, description);
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        var current = allowedRoot;
        foreach (var component in SplitRelativePath(relative))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current)) break;
            EnsureNotReparsePoint(current, description);
            if (File.Exists(current))
            {
                throw new IOException($"The {description} is blocked by an existing file.");
            }
        }
    }

    private static void EnsureOrdinaryExistingPath(
        string allowedRoot,
        string candidate,
        bool requireDirectory,
        string description)
    {
        if (!Path.GetFullPath(candidate).Equals(Path.GetFullPath(allowedRoot), StringComparison.OrdinalIgnoreCase))
        {
            EnsurePathIsInside(allowedRoot, candidate, description);
        }
        EnsureNotReparsePoint(allowedRoot, description);
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        var current = allowedRoot;
        foreach (var component in SplitRelativePath(relative))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new IOException($"The {description} disappeared while ClipCord was validating it.");
            }
            EnsureNotReparsePoint(current, description);
        }

        if (requireDirectory ? !Directory.Exists(candidate) : !File.Exists(candidate))
        {
            throw new IOException($"The {description} had an unexpected file-system type.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} cannot be a symbolic link or junction.");
        }
    }

    private static IEnumerable<string> SplitRelativePath(string relativePath) =>
        relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static string ToPortableRelativePath(string root, string path)
    {
        EnsurePathIsInside(root, path, "capture project path");
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static void EnsureOwnedTemporaryDirectory(
        string projectsRoot,
        string temporaryDirectory,
        string projectId)
    {
        var expectedPrefix = TemporaryProjectPrefix + projectId + "-";
        var directory = new DirectoryInfo(temporaryDirectory);
        if (!directory.Exists ||
            directory.Parent is null ||
            !directory.Parent.FullName.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase) ||
            !directory.Name.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !directory.Name.EndsWith(".tmp", StringComparison.Ordinal) ||
            directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("ClipCord could not create a safe temporary camera project.");
        }
    }

    private static void TryRemoveOwnedTemporaryDirectory(
        string projectsRoot,
        string temporaryDirectory,
        string projectId)
    {
        try
        {
            EnsureOwnedTemporaryDirectory(projectsRoot, temporaryDirectory, projectId);
            TryDeleteExactFile(Path.Combine(temporaryDirectory, ManifestFileName));
            TryDeleteExactFile(Path.Combine(temporaryDirectory, CameraLayerFileName));
            TryDeleteExactFile(Path.Combine(
                temporaryDirectory,
                CaptureCompositionStore.FileName));
            Directory.Delete(temporaryDirectory, recursive: false);
        }
        catch
        {
            // Cleanup is deliberately best effort and never expands beyond the exact owned files.
        }
    }

    private static void TryDeletePromotedStage(string stagedPath, string stagingRoot)
    {
        try
        {
            EnsurePathIsInside(stagingRoot, stagedPath, "reaction-camera stage");
            EnsureOrdinaryExistingPath(stagingRoot, stagedPath, requireDirectory: false, "reaction-camera stage");
            File.Delete(stagedPath);
        }
        catch
        {
            // The project has already committed. Startup staging recovery may remove this copy.
        }
    }

    private static void TryDeleteExactFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of an exact owned filename only.
        }
    }

    private static void ValidateProjectId(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!IsProjectId(projectId))
        {
            throw new ArgumentException("The capture project id was invalid.", nameof(projectId));
        }
    }

    private static bool IsProjectId(string projectId) =>
        projectId.Length == 32 && projectId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
