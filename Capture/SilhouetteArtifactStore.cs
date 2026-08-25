using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipsToDiscord;

internal enum SilhouetteArtifactValidationStatus
{
    Valid,
    Missing,
    Mismatch
}

internal sealed record SilhouetteArtifactValidationResult(
    SilhouetteArtifactValidationStatus Status,
    CaptureMediaFingerprint? ObservedFingerprint)
{
    internal bool IsValid => Status == SilhouetteArtifactValidationStatus.Valid;

    internal bool IsDefinitiveFailure =>
        Status is SilhouetteArtifactValidationStatus.Missing or
            SilhouetteArtifactValidationStatus.Mismatch;
}

/// <summary>
/// Owns the fixed files produced by silhouette processing. Matte and temporary artifacts remain
/// project-local, while completed videos are published beside their validated gameplay source so
/// they are ordinary, discoverable Library clips. Durable state stores only content fingerprints;
/// callers cannot supply a final filename or redirect a path.
/// </summary>
internal static class SilhouetteArtifactStore
{
    internal const string MatteFileName = "silhouette-matte.mkv";
    internal const string LandscapeFileName = "silhouette-landscape.mp4";
    internal const string PortraitFileName = "silhouette-portrait.mp4";
    internal const int MaximumCleanupFiles = 128;
    internal const int MaximumCleanupScanEntries = 512;

    private const int AttemptGenerationDigits = 20;
    private const int TemporaryNonceDigits = 32;
    private const int HashBufferSize = 128 * 1024;
    private const int MaximumPublishedFileNameLength = 240;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static string GetMattePath(string libraryRoot, string projectId) =>
        Path.Combine(
            CaptureProjectStore.GetProjectDirectory(libraryRoot, projectId),
            MatteFileName);

    internal static string GetOutputPath(
        string libraryRoot,
        string projectId,
        string orientationId)
    {
        var context = ValidateProjectContext(libraryRoot, projectId);
        return GetOutputPath(context, projectId, orientationId);
    }

    internal static string GetLegacyOutputPath(
        string libraryRoot,
        string projectId,
        string orientationId) =>
        Path.Combine(
            ValidateProjectDirectory(libraryRoot, projectId),
            GetOutputFileName(orientationId));

    internal static string CreateMatteTemporaryPath(
        string libraryRoot,
        string projectId,
        long attemptGeneration) =>
        CreateOwnedTemporaryPath(
            libraryRoot,
            projectId,
            MatteFileName,
            attemptGeneration);

    internal static string CreateOutputTemporaryPath(
        string libraryRoot,
        string projectId,
        string orientationId,
        long attemptGeneration) =>
        CreateOwnedTemporaryPath(
            libraryRoot,
            projectId,
            GetOutputFileName(orientationId),
            attemptGeneration);

    internal static async Task<CaptureMediaFingerprint> FingerprintTemporaryAsync(
        string libraryRoot,
        string projectId,
        string temporaryPath,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = ValidateProjectDirectory(libraryRoot, projectId);
        var normalizedTemporaryPath = ValidateOwnedTemporaryFile(
            projectDirectory,
            temporaryPath,
            expectedFinalFileName: null,
            expectedAttemptGeneration: null,
            requireExists: true);
        return await CreateFingerprintAsync(
                projectDirectory,
                normalizedTemporaryPath,
                allowEmpty: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The silhouette temporary artifact was empty.");
    }

    internal static Task<CaptureMediaFingerprint> PromoteMatteAsync(
        string libraryRoot,
        string projectId,
        long attemptGeneration,
        string temporaryPath,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken = default) =>
        PromoteAsync(
            libraryRoot,
            projectId,
            MatteFileName,
            orientationId: null,
            attemptGeneration,
            temporaryPath,
            expectedFingerprint,
            cancellationToken);

    internal static Task<CaptureMediaFingerprint> PromoteOutputAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        long attemptGeneration,
        string temporaryPath,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken = default) =>
        PromoteAsync(
            libraryRoot,
            projectId,
            GetOutputFileName(orientationId),
            orientationId,
            attemptGeneration,
            temporaryPath,
            expectedFingerprint,
            cancellationToken);

    internal static Task<SilhouetteArtifactValidationResult> ValidateMatteAsync(
        string libraryRoot,
        string projectId,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken = default) =>
        ValidateFinalAsync(
            libraryRoot,
            projectId,
            MatteFileName,
            expectedFingerprint,
            cancellationToken);

    internal static Task<SilhouetteArtifactValidationResult> ValidateOutputAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken = default) =>
        ValidateOutputFinalAsync(
            libraryRoot,
            projectId,
            orientationId,
            expectedFingerprint,
            cancellationToken);

    /// <summary>
    /// Best-effort deletion for an exact ClipCord-owned rendition temporary. Caller-supplied
    /// lookalikes, files outside the validated project, and reparse points are never removed.
    /// A file that is still busy remains eligible for the bounded age-gated cleanup pass.
    /// </summary>
    internal static void DiscardOwnedTemporary(
        string libraryRoot,
        string projectId,
        string temporaryPath)
    {
        try
        {
            TryDeleteOwnedTemporary(
                ValidateProjectDirectory(libraryRoot, projectId),
                temporaryPath);
        }
        catch
        {
            // Bounded cleanup can remove an exact owned name on a later processing pass.
        }
    }

    /// <summary>
    /// Deletes at most <paramref name="maximumFiles"/> old, exact ClipCord temporary files.
    /// Enumeration is also capped so a hostile or unexpectedly large directory cannot turn
    /// startup cleanup into unbounded work. Unknown entries and all reparse points are preserved.
    /// </summary>
    internal static int CleanupTemporaryArtifacts(
        string libraryRoot,
        string projectId,
        DateTimeOffset olderThanUtc,
        int maximumFiles = MaximumCleanupFiles)
    {
        if (maximumFiles <= 0 || maximumFiles > MaximumCleanupFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        }
        if (olderThanUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The silhouette cleanup cutoff must use UTC.",
                nameof(olderThanUtc));
        }

        var projectDirectory = ValidateProjectDirectory(libraryRoot, projectId);
        var deleted = 0;
        var inspected = 0;
        foreach (var entry in new DirectoryInfo(projectDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (++inspected > MaximumCleanupScanEntries || deleted >= maximumFiles) break;
            try
            {
                if (entry is not FileInfo file ||
                    entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    !IsOwnedTemporaryFileName(file.Name, out _, out _) ||
                    file.LastWriteTimeUtc > olderThanUtc.UtcDateTime)
                {
                    continue;
                }

                file.Delete();
                deleted++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Exact-name cleanup is best effort. A busy artifact remains for a later pass.
            }
        }
        return deleted;
    }

    internal static bool IsOwnedProjectFileName(string fileName) =>
        IsFinalArtifactFileName(fileName) ||
        IsOwnedTemporaryFileName(fileName, out _, out _);

    internal static bool IsFinalArtifactFileName(string fileName) =>
        fileName is MatteFileName or LandscapeFileName or PortraitFileName;

    internal static bool IsOwnedTemporaryFileName(
        string fileName,
        out string finalFileName,
        out long attemptGeneration)
    {
        finalFileName = string.Empty;
        attemptGeneration = 0;
        if (string.IsNullOrEmpty(fileName)) return false;

        foreach (var candidate in FinalFileNames)
        {
            var prefix = "." + candidate + ".";
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = fileName.AsSpan(prefix.Length);
            var expectedLength = AttemptGenerationDigits + 1 + TemporaryNonceDigits + 4;
            if (remainder.Length != expectedLength ||
                remainder[AttemptGenerationDigits] != '.' ||
                !remainder[^4..].SequenceEqual(".tmp".AsSpan()))
            {
                return false;
            }

            var generationText = remainder[..AttemptGenerationDigits];
            var nonceText = remainder.Slice(
                AttemptGenerationDigits + 1,
                TemporaryNonceDigits);
            if (!long.TryParse(
                    generationText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out attemptGeneration) ||
                attemptGeneration <= 0 ||
                !Guid.TryParseExact(nonceText, "N", out _))
            {
                attemptGeneration = 0;
                return false;
            }

            finalFileName = candidate;
            return true;
        }
        return false;
    }

    private static IReadOnlyList<string> FinalFileNames { get; } =
    [
        MatteFileName,
        LandscapeFileName,
        PortraitFileName
    ];

    private static string CreateOwnedTemporaryPath(
        string libraryRoot,
        string projectId,
        string finalFileName,
        long attemptGeneration)
    {
        if (attemptGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptGeneration));
        }
        RequireFinalFileName(finalFileName);
        var projectDirectory = ValidateProjectDirectory(libraryRoot, projectId);
        return CreateTemporaryPathInProject(
            projectDirectory,
            finalFileName,
            attemptGeneration);
    }

    private static string CreateTemporaryPathInProject(
        string projectDirectory,
        string finalFileName,
        long attemptGeneration)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fileName = string.Create(
                CultureInfo.InvariantCulture,
                $".{finalFileName}.{attemptGeneration:D20}.{Guid.NewGuid():N}.tmp");
            var path = Path.Combine(projectDirectory, fileName);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
        throw new IOException("ClipCord could not reserve a unique silhouette temporary path.");
    }

    private static string CreateBackupPath(
        string projectDirectory,
        string finalFileName,
        long attemptGeneration) =>
        CreateTemporaryPathInProject(projectDirectory, finalFileName, attemptGeneration);

    private static async Task<CaptureMediaFingerprint> PromoteAsync(
        string libraryRoot,
        string projectId,
        string finalFileName,
        string? orientationId,
        long attemptGeneration,
        string temporaryPath,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken)
    {
        if (attemptGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptGeneration));
        }
        ValidateFingerprint(expectedFingerprint, nameof(expectedFingerprint));
        RequireFinalFileName(finalFileName);
        cancellationToken.ThrowIfCancellationRequested();

        var context = ValidateProjectContext(libraryRoot, projectId);
        var projectDirectory = context.ProjectDirectory;
        var normalizedTemporaryPath = ValidateOwnedTemporaryFile(
            projectDirectory,
            temporaryPath,
            finalFileName,
            attemptGeneration,
            requireExists: true);
        var actualTemporaryFingerprint = await CreateFingerprintAsync(
                projectDirectory,
                normalizedTemporaryPath,
                allowEmpty: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The silhouette temporary artifact was empty.");
        if (!FingerprintsEqual(expectedFingerprint, actualTemporaryFingerprint))
        {
            throw new InvalidDataException(
                "The silhouette temporary artifact did not match its expected content identity.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        context = ValidateProjectContext(libraryRoot, projectId);
        projectDirectory = context.ProjectDirectory;
        normalizedTemporaryPath = ValidateOwnedTemporaryFile(
            projectDirectory,
            normalizedTemporaryPath,
            finalFileName,
            attemptGeneration,
            requireExists: true);
        var finalPath = orientationId is null
            ? Path.Combine(projectDirectory, finalFileName)
            : GetOutputPath(context, projectId, orientationId);
        var finalDirectory = Path.GetDirectoryName(finalPath) ??
            throw new IOException("The silhouette output folder is invalid.");
        EnsureSafeDestination(finalDirectory, finalPath);

        using var saveMutex = new Mutex(
            initiallyOwned: false,
            SilhouetteRenditionStore.GetSaveMutexName(projectId));
        var lockTaken = false;
        try
        {
            while (!lockTaken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockTaken = saveMutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }

            ValidateCommittingState(
                libraryRoot,
                projectId,
                orientationId,
                attemptGeneration,
                expectedFingerprint);
            context = ValidateProjectContext(libraryRoot, projectId);
            projectDirectory = context.ProjectDirectory;
            normalizedTemporaryPath = ValidateOwnedTemporaryFile(
                projectDirectory,
                normalizedTemporaryPath,
                finalFileName,
                attemptGeneration,
                requireExists: true);
            finalPath = orientationId is null
                ? Path.Combine(projectDirectory, finalFileName)
                : GetOutputPath(context, projectId, orientationId);
            finalDirectory = Path.GetDirectoryName(finalPath) ??
                throw new IOException("The silhouette output folder is invalid.");
            EnsureSafeDestination(finalDirectory, finalPath);
            cancellationToken.ThrowIfCancellationRequested();

            // Matte replacement remains project-local. Published videos move from the private
            // project temp folder to the validated gameplay folder on the same library volume.
            // An occupied public filename is adopted only when its bytes are already identical;
            // ClipCord never overwrites unrelated user media.
            var replacingExisting = File.Exists(finalPath);
            if (orientationId is not null && replacingExisting)
            {
                var existingFingerprint = CreateFingerprintAsync(
                        finalDirectory,
                        finalPath,
                        allowEmpty: true,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (existingFingerprint is null ||
                    !FingerprintsEqual(expectedFingerprint, existingFingerprint))
                {
                    throw new IOException(
                        "The Reaction Camera output filename is already occupied by different media.");
                }

                TryDeleteOwnedTemporary(projectDirectory, normalizedTemporaryPath);
                return existingFingerprint;
            }
            var backupPath = replacingExisting
                ? CreateBackupPath(projectDirectory, finalFileName, attemptGeneration)
                : null;
            var promotionCommitted = false;
            try
            {
                if (replacingExisting)
                {
                    File.Replace(
                        normalizedTemporaryPath,
                        finalPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(normalizedTemporaryPath, finalPath, overwrite: false);
                }
                promotionCommitted = true;

                // Mutex ownership is thread-affine, so verification is deliberately completed
                // synchronously on this thread after the irreversible commit boundary.
                var verifiedFinalFingerprint = CreateFingerprintAsync(
                        finalDirectory,
                        finalPath,
                        allowEmpty: false,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
                    ?? throw new InvalidDataException(
                        "The promoted silhouette artifact was empty.");
                if (!FingerprintsEqual(expectedFingerprint, verifiedFinalFingerprint))
                {
                    throw new InvalidDataException(
                        "The promoted silhouette artifact did not retain its expected content identity.");
                }

                if (backupPath is not null) TryDeleteOwnedTemporary(projectDirectory, backupPath);
                return verifiedFinalFingerprint;
            }
            catch
            {
                if (promotionCommitted)
                {
                    RollBackPromotion(finalDirectory, finalPath, backupPath);
                }
                throw;
            }
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private static async Task<SilhouetteArtifactValidationResult> ValidateFinalAsync(
        string libraryRoot,
        string projectId,
        string finalFileName,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateFingerprint(expectedFingerprint, nameof(expectedFingerprint));
        RequireFinalFileName(finalFileName);
        cancellationToken.ThrowIfCancellationRequested();
        var projectDirectory = ValidateProjectDirectory(libraryRoot, projectId);
        var finalPath = Path.Combine(projectDirectory, finalFileName);
        if (!PathExists(finalPath))
        {
            // Probe attributes as well so a dangling reparse point is rejected where the host
            // exposes it, instead of being silently classified as an ordinary missing file.
            try
            {
                var attributes = File.GetAttributes(finalPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException(
                        "A silhouette artifact cannot be a symbolic link or junction.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            return new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Missing,
                ObservedFingerprint: null);
        }

        EnsureOrdinaryExistingPath(
            projectDirectory,
            finalPath,
            requireDirectory: false,
            "silhouette artifact");
        var observed = await CreateFingerprintAsync(
                projectDirectory,
                finalPath,
                allowEmpty: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (observed is not null && FingerprintsEqual(expectedFingerprint, observed))
        {
            return new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Valid,
                observed);
        }
        return new SilhouetteArtifactValidationResult(
            SilhouetteArtifactValidationStatus.Mismatch,
            observed);
    }

    private static async Task<SilhouetteArtifactValidationResult> ValidateOutputFinalAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken)
    {
        ValidateFingerprint(expectedFingerprint, nameof(expectedFingerprint));
        _ = GetOutputFileName(orientationId);
        cancellationToken.ThrowIfCancellationRequested();

        var context = ValidateProjectContext(libraryRoot, projectId);
        var outputPath = GetOutputPath(context, projectId, orientationId);
        var outputDirectory = Path.GetDirectoryName(outputPath) ??
            throw new IOException("The Reaction Camera output folder is invalid.");
        var legacyPath = Path.Combine(
            context.ProjectDirectory,
            GetOutputFileName(orientationId));
        var visibleValidation = await ValidatePathAsync(
                outputDirectory,
                outputPath,
                expectedFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (visibleValidation.IsValid)
        {
            TryDeleteMatchingLegacyOutput(
                context.ProjectDirectory,
                legacyPath,
                expectedFingerprint);
            return visibleValidation;
        }
        if (visibleValidation.Status == SilhouetteArtifactValidationStatus.Mismatch)
        {
            return visibleValidation;
        }

        // Builds created before visible publication kept the final MP4 in the private project
        // directory. A fingerprint-backed one-time move makes those completed clips discoverable
        // without re-rendering or keeping a duplicate copy.
        var legacyValidation = await ValidatePathAsync(
                context.ProjectDirectory,
                legacyPath,
                expectedFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (!legacyValidation.IsValid) return legacyValidation;

        using var saveMutex = new Mutex(
            initiallyOwned: false,
            SilhouetteRenditionStore.GetSaveMutexName(projectId));
        var lockTaken = false;
        try
        {
            while (!lockTaken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockTaken = saveMutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }

            context = ValidateProjectContext(libraryRoot, projectId);
            outputPath = GetOutputPath(context, projectId, orientationId);
            outputDirectory = Path.GetDirectoryName(outputPath) ??
                throw new IOException("The Reaction Camera output folder is invalid.");
            legacyPath = Path.Combine(
                context.ProjectDirectory,
                GetOutputFileName(orientationId));

            visibleValidation = ValidatePathAsync(
                    outputDirectory,
                    outputPath,
                    expectedFingerprint,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (visibleValidation.IsValid)
            {
                TryDeleteMatchingLegacyOutput(
                    context.ProjectDirectory,
                    legacyPath,
                    expectedFingerprint);
                return visibleValidation;
            }
            if (visibleValidation.Status == SilhouetteArtifactValidationStatus.Mismatch)
            {
                return visibleValidation;
            }

            legacyValidation = ValidatePathAsync(
                    context.ProjectDirectory,
                    legacyPath,
                    expectedFingerprint,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (!legacyValidation.IsValid) return legacyValidation;

            EnsureSafeDestination(outputDirectory, outputPath);
            File.Move(legacyPath, outputPath, overwrite: false);
            return ValidatePathAsync(
                    outputDirectory,
                    outputPath,
                    expectedFingerprint,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private static async Task<SilhouetteArtifactValidationResult> ValidatePathAsync(
        string allowedDirectory,
        string path,
        CaptureMediaFingerprint expectedFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PathExists(path))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException(
                        "A silhouette artifact cannot be a symbolic link or junction.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            return new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Missing,
                ObservedFingerprint: null);
        }

        EnsureOrdinaryExistingPath(
            allowedDirectory,
            path,
            requireDirectory: false,
            "silhouette artifact");
        var observed = await CreateFingerprintAsync(
                allowedDirectory,
                path,
                allowEmpty: true,
                cancellationToken)
            .ConfigureAwait(false);
        return observed is not null && FingerprintsEqual(expectedFingerprint, observed)
            ? new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Valid,
                observed)
            : new SilhouetteArtifactValidationResult(
                SilhouetteArtifactValidationStatus.Mismatch,
                observed);
    }

    private static void TryDeleteMatchingLegacyOutput(
        string projectDirectory,
        string legacyPath,
        CaptureMediaFingerprint expectedFingerprint)
    {
        try
        {
            if (!File.Exists(legacyPath)) return;
            EnsureOrdinaryExistingPath(
                projectDirectory,
                legacyPath,
                requireDirectory: false,
                "legacy silhouette artifact");
            var fingerprint = CreateFingerprintAsync(
                    projectDirectory,
                    legacyPath,
                    allowEmpty: true,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (fingerprint is not null &&
                FingerprintsEqual(expectedFingerprint, fingerprint))
            {
                File.Delete(legacyPath);
            }
        }
        catch
        {
            // A matching legacy duplicate is harmless and can be cleaned on a later validation.
        }
    }

    private static void ValidateCommittingState(
        string libraryRoot,
        string projectId,
        string? orientationId,
        long attemptGeneration,
        CaptureMediaFingerprint expectedFingerprint)
    {
        var loaded = SilhouetteRenditionStore.Load(libraryRoot, projectId);
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            throw new SilhouetteRenditionConcurrencyException(
                "The silhouette commit state is not available for artifact promotion.");
        }

        if (orientationId is null)
        {
            if (loaded.Document.Matte.State != SilhouetteMatteState.Committing ||
                loaded.Document.Matte.AttemptGeneration != attemptGeneration ||
                !SilhouetteRenditionModel.FingerprintsEqual(
                    loaded.Document.Matte.ExpectedTemporaryArtifact,
                    expectedFingerprint))
            {
                throw new SilhouetteRenditionConcurrencyException(
                    "The shared matte artifact belongs to a stale commit attempt.");
            }
            return;
        }

        var output = loaded.Document.Outputs.Single(candidate =>
            candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
        if (output.State != SilhouetteOutputState.Committing ||
            output.AttemptGeneration != attemptGeneration ||
            !SilhouetteRenditionModel.FingerprintsEqual(
                output.ExpectedTemporaryArtifact,
                expectedFingerprint))
        {
            throw new SilhouetteRenditionConcurrencyException(
                "The silhouette output artifact belongs to a stale commit attempt.");
        }
    }

    private static void RollBackPromotion(
        string projectDirectory,
        string finalPath,
        string? backupPath)
    {
        try
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                EnsureOrdinaryExistingPath(
                    projectDirectory,
                    backupPath,
                    requireDirectory: false,
                    "silhouette artifact backup");
                if (File.Exists(finalPath))
                {
                    EnsureOrdinaryExistingPath(
                        projectDirectory,
                        finalPath,
                        requireDirectory: false,
                        "silhouette artifact");
                    File.Replace(
                        backupPath,
                        finalPath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(backupPath, finalPath, overwrite: false);
                }
                return;
            }

            if (File.Exists(finalPath))
            {
                EnsureOrdinaryExistingPath(
                    projectDirectory,
                    finalPath,
                    requireDirectory: false,
                    "silhouette artifact");
                File.Delete(finalPath);
            }
        }
        catch
        {
            // Recovery will validate the fixed path against durable state. Any exact backup is
            // intentionally retained rather than expanding cleanup to an uncertain target.
        }
    }

    private static void TryDeleteOwnedTemporary(
        string projectDirectory,
        string path)
    {
        try
        {
            var normalized = ValidateOwnedTemporaryFile(
                projectDirectory,
                path,
                expectedFinalFileName: null,
                expectedAttemptGeneration: null,
                requireExists: true);
            File.Delete(normalized);
        }
        catch
        {
            // A retained exact temporary file is bounded by CleanupTemporaryArtifacts.
        }
    }

    private static async Task<CaptureMediaFingerprint?> CreateFingerprintAsync(
        string projectDirectory,
        string path,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOrdinaryExistingPath(
            projectDirectory,
            path,
            requireDirectory: false,
            "silhouette artifact");
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HashBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var configuredStream = stream.ConfigureAwait(false);
        EnsureOrdinaryExistingPath(
            projectDirectory,
            path,
            requireDirectory: false,
            "silhouette artifact");
        var byteLength = stream.Length;
        if (byteLength <= 0)
        {
            if (allowEmpty) return null;
            throw new InvalidDataException("A silhouette artifact was empty.");
        }
        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (stream.Length != byteLength)
        {
            throw new IOException("A silhouette artifact changed while it was fingerprinted.");
        }
        return new CaptureMediaFingerprint(
            byteLength,
            Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static string ValidateProjectDirectory(string libraryRoot, string projectId) =>
        ValidateProjectContext(libraryRoot, projectId).ProjectDirectory;

    private static ValidatedProjectContext ValidateProjectContext(
        string libraryRoot,
        string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(libraryRoot.Trim()));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("The ClipCord library folder does not exist.");
        }
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            normalizedRoot,
            requireDirectory: true,
            "ClipCord library folder");

        var projectDirectory = Path.GetFullPath(
            CaptureProjectStore.GetProjectDirectory(normalizedRoot, projectId));
        EnsurePathIsInside(normalizedRoot, projectDirectory, "capture project folder");
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            projectDirectory,
            requireDirectory: true,
            "capture project folder");

        var manifestPath = Path.Combine(
            projectDirectory,
            CaptureProjectStore.ManifestFileName);
        EnsureOrdinaryExistingPath(
            projectDirectory,
            manifestPath,
            requireDirectory: false,
            "capture project manifest");
        CaptureProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                           CaptureProjectStore.ReadBoundedManifestFile(manifestPath),
                           ManifestJsonOptions)
                       ?? throw new InvalidDataException(
                           "The capture project manifest is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The capture project manifest is invalid.",
                exception);
        }
        if (manifest.SchemaVersion != CaptureProjectStore.CurrentSchemaVersion ||
            !string.Equals(manifest.ProjectId, projectId, StringComparison.Ordinal) ||
            !CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(
                    manifest.GameplayRelativePath)
                .Equals(projectId, StringComparison.Ordinal))
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
        if (!Path.GetExtension(gameplayPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The capture project gameplay file is not an MP4 recording.");
        }
        var gameplayDirectory = Path.GetDirectoryName(gameplayPath) ??
            throw new InvalidDataException("The capture project gameplay folder is invalid.");
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            gameplayDirectory,
            requireDirectory: true,
            "project gameplay folder");
        return new ValidatedProjectContext(
            normalizedRoot,
            projectDirectory,
            gameplayPath);
    }

    private static string ValidateOwnedTemporaryFile(
        string projectDirectory,
        string temporaryPath,
        string? expectedFinalFileName,
        long? expectedAttemptGeneration,
        bool requireExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        var normalized = Path.GetFullPath(temporaryPath.Trim());
        if (!string.Equals(
                Path.GetDirectoryName(normalized),
                projectDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !IsOwnedTemporaryFileName(
                Path.GetFileName(normalized),
                out var finalFileName,
                out var attemptGeneration) ||
            expectedFinalFileName is not null &&
            !string.Equals(finalFileName, expectedFinalFileName, StringComparison.Ordinal) ||
            expectedAttemptGeneration is not null &&
            attemptGeneration != expectedAttemptGeneration.Value)
        {
            throw new IOException(
                "The silhouette temporary artifact is not an exact ClipCord-owned path.");
        }
        EnsurePathIsInside(projectDirectory, normalized, "silhouette temporary artifact");
        if (requireExists)
        {
            EnsureOrdinaryExistingPath(
                projectDirectory,
                normalized,
                requireDirectory: false,
                "silhouette temporary artifact");
        }
        return normalized;
    }

    private static string GetOutputFileName(string orientationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orientationId);
        return orientationId switch
        {
            CompositionOrientationIds.Landscape => LandscapeFileName,
            CompositionOrientationIds.Portrait => PortraitFileName,
            _ => throw new ArgumentException(
                "The silhouette rendition orientation is not supported.",
                nameof(orientationId))
        };
    }

    private static string GetOutputPath(
        ValidatedProjectContext context,
        string projectId,
        string orientationId)
    {
        var suffix = orientationId switch
        {
            CompositionOrientationIds.Landscape =>
                "__Reaction-Camera__Landscape.mp4",
            CompositionOrientationIds.Portrait =>
                "__Reaction-Camera__Portrait.mp4",
            _ => throw new ArgumentException(
                "The silhouette rendition orientation is not supported.",
                nameof(orientationId))
        };
        var sourceStem = Path.GetFileNameWithoutExtension(context.GameplayPath);
        var fileName = sourceStem + suffix;
        if (fileName.Length > MaximumPublishedFileNameLength)
        {
            var identity = "__" + projectId[..12];
            var maximumStemLength = MaximumPublishedFileNameLength -
                identity.Length - suffix.Length;
            if (maximumStemLength <= 0)
            {
                throw new PathTooLongException(
                    "The Reaction Camera output filename could not be bounded safely.");
            }
            sourceStem = sourceStem[..Math.Min(sourceStem.Length, maximumStemLength)];
            fileName = sourceStem + identity + suffix;
        }

        var gameplayDirectory = Path.GetDirectoryName(context.GameplayPath) ??
            throw new IOException("The Reaction Camera output folder is invalid.");
        var outputPath = Path.GetFullPath(Path.Combine(gameplayDirectory, fileName));
        EnsurePathIsInside(gameplayDirectory, outputPath, "Reaction Camera output");
        return outputPath;
    }

    private static void RequireFinalFileName(string fileName)
    {
        if (!IsFinalArtifactFileName(fileName))
        {
            throw new ArgumentException("The silhouette artifact filename is not owned.", nameof(fileName));
        }
    }

    private static void EnsureSafeDestination(string projectDirectory, string finalPath)
    {
        EnsureOrdinaryExistingPath(
            projectDirectory,
            projectDirectory,
            requireDirectory: true,
            "silhouette output folder");
        EnsurePathIsInside(projectDirectory, finalPath, "silhouette artifact");
        if (!PathExists(finalPath)) return;
        EnsureOrdinaryExistingPath(
            projectDirectory,
            finalPath,
            requireDirectory: false,
            "silhouette artifact");
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static void ValidateFingerprint(
        CaptureMediaFingerprint? fingerprint,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(fingerprint, parameterName);
        if (fingerprint.ByteLength <= 0 ||
            fingerprint.Sha256 is not { Length: 64 } ||
            !fingerprint.Sha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "The silhouette artifact fingerprint is invalid.",
                parameterName);
        }
    }

    private static bool FingerprintsEqual(
        CaptureMediaFingerprint left,
        CaptureMediaFingerprint right) =>
        left.ByteLength == right.ByteLength &&
        left.Sha256.Equals(right.Sha256, StringComparison.Ordinal);

    private static void EnsurePathIsInside(
        string allowedRoot,
        string candidate,
        string description)
    {
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        if (relative.Equals(".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException(
                $"The {description} must remain inside its ClipCord-owned folder.");
        }
    }

    private static void EnsureOrdinaryExistingPath(
        string allowedRoot,
        string candidate,
        bool requireDirectory,
        string description)
    {
        if (!Path.GetFullPath(candidate).Equals(
                Path.GetFullPath(allowedRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            EnsurePathIsInside(allowedRoot, candidate, description);
        }
        EnsureNotReparsePoint(allowedRoot, description);
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        var current = allowedRoot;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new IOException(
                    $"The {description} disappeared while ClipCord was validating it.");
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
            throw new IOException(
                $"The {description} cannot be a symbolic link or junction.");
        }
    }

    private sealed record ValidatedProjectContext(
        string LibraryRoot,
        string ProjectDirectory,
        string GameplayPath);
}
