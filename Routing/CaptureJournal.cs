using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal enum CaptureJournalState
{
    OriginalCommitted,
    CameraPending,
    RenditionsReady,
    RenditionsFailed
}

internal enum CaptureJournalSourceKind
{
    ManualCapture,
    InstantReplay
}

internal static class CaptureJournalArtifactKinds
{
    internal const string ReactionCamera = "reaction-camera";
    internal const string Landscape = CompositionOrientationIds.Landscape;
    internal const string Portrait = CompositionOrientationIds.Portrait;

    internal static IReadOnlyList<string> Renditions { get; } =
    [
        Landscape,
        Portrait
    ];

    internal static bool IsKnown(string value) =>
        value.Equals(ReactionCamera, StringComparison.Ordinal) ||
        Renditions.Contains(value, StringComparer.Ordinal);
}

internal sealed record CaptureJournalFingerprint(
    long ByteLength,
    string Sha256);

internal sealed record CaptureJournalArtifact(
    string ArtifactId,
    string Kind,
    string RelativePath,
    CaptureJournalFingerprint Fingerprint);

internal sealed record CaptureJournalClipMetadata(
    string ClipId,
    CaptureJournalSourceKind SourceKind,
    string GameName,
    DateTimeOffset CapturedUtc,
    long DurationTicks,
    int Width,
    int Height,
    bool ReactionCameraRequested,
    IReadOnlyList<string> RequestedRenditions,
    CaptureJournalArtifact Original);

internal sealed record CaptureJournalDocument(
    int SchemaVersion,
    long Generation,
    CaptureJournalState State,
    long ProcessingAttemptEpoch,
    string? ProcessingAttemptId,
    CaptureJournalClipMetadata Clip,
    bool CameraPresent,
    IReadOnlyList<CaptureJournalArtifact> Artifacts,
    string? FailureCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed class CaptureJournalConcurrencyException : InvalidOperationException
{
    internal CaptureJournalConcurrencyException(string message) : base(message)
    {
    }
}

internal enum CaptureJournalLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedSchema,
    IdentityMismatch,
    Invalid,
    Unavailable
}

internal sealed record CaptureJournalLoadResult(
    string ClipId,
    CaptureJournalDocument? Document,
    CaptureJournalLoadStatus Status)
{
    internal bool LoadedFromDisk =>
        Status == CaptureJournalLoadStatus.Loaded && Document is not null;
}

internal enum CaptureJournalArtifactValidationStatus
{
    Valid,
    Missing,
    Mismatch,
    Unavailable
}

internal sealed record CaptureJournalArtifactValidationResult(
    CaptureJournalArtifactValidationStatus Status,
    CaptureJournalFingerprint? ObservedFingerprint)
{
    internal bool IsValid => Status == CaptureJournalArtifactValidationStatus.Valid;
}

internal sealed record CaptureJournalClipIdPage(
    IReadOnlyList<string> ClipIds,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Pure state transitions for one captured clip. The original gameplay artifact and all clip
/// metadata are immutable. Camera and rendition work may advance only through the durable
/// CameraPending state, which gives startup reconciliation a precise recovery point.
/// </summary>
internal static class CaptureJournalModel
{
    internal static CaptureJournalDocument CreateOriginalCommitted(
        CaptureJournalClipMetadata clip,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.RequestedRenditions is null || clip.Original is null ||
            clip.Original.Fingerprint is null)
        {
            throw new InvalidDataException("The capture journal clip metadata is incomplete.");
        }
        var utcNow = RequireUtc(now, nameof(now));
        var document = new CaptureJournalDocument(
            CaptureJournalStore.CurrentSchemaVersion,
            Generation: 1,
            CaptureJournalState.OriginalCommitted,
            ProcessingAttemptEpoch: 0,
            ProcessingAttemptId: null,
            NormalizeClip(clip),
            CameraPresent: false,
            Artifacts: [],
            FailureCode: null,
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
        ValidateDocumentShape(document);
        return document;
    }

    internal static CaptureJournalDocument BeginCamera(
        CaptureJournalDocument current,
        DateTimeOffset now,
        string? processingAttemptId = null)
    {
        ValidateDocumentShape(current);
        Require(
            current.Clip.ReactionCameraRequested,
            "Camera processing cannot begin for a clip that did not request Reaction Camera.");
        Require(
            current.State is CaptureJournalState.OriginalCommitted or
                CaptureJournalState.RenditionsFailed,
            "Camera processing can begin only for a committed original or failed rendition attempt.");
        return Next(
            current,
            CaptureJournalState.CameraPending,
            cameraPresent: false,
            artifacts: [],
            failureCode: null,
            now,
            processingAttemptEpoch: checked(current.ProcessingAttemptEpoch + 1),
            processingAttemptId: processingAttemptId ?? Guid.NewGuid().ToString("N"));
    }

    internal static CaptureJournalDocument AttachCamera(
        CaptureJournalDocument current,
        CaptureJournalArtifact camera,
        DateTimeOffset now)
    {
        ValidateDocumentShape(current);
        ValidateArtifact(camera, current.Clip.ClipId, allowOriginal: false);
        Require(
            current.State == CaptureJournalState.CameraPending,
            "The camera artifact can be attached only while camera work is pending.");
        Require(
            camera.Kind.Equals(CaptureJournalArtifactKinds.ReactionCamera, StringComparison.Ordinal),
            "The supplied artifact is not a Reaction Camera layer.");
        var artifacts = ReplaceArtifact(current.Artifacts, camera);
        return Next(
            current,
            CaptureJournalState.CameraPending,
            cameraPresent: true,
            artifacts,
            failureCode: null,
            now);
    }

    internal static CaptureJournalDocument CompleteRenditions(
        CaptureJournalDocument current,
        IReadOnlyList<CaptureJournalArtifact> renditions,
        DateTimeOffset now)
    {
        ValidateDocumentShape(current);
        ArgumentNullException.ThrowIfNull(renditions);
        Require(
            current.State == CaptureJournalState.CameraPending,
            "Renditions can complete only while camera work is pending.");
        Require(current.CameraPresent, "Renditions cannot complete without a committed camera layer.");

        var artifacts = current.Artifacts.ToList();
        foreach (var rendition in renditions)
        {
            ValidateArtifact(rendition, current.Clip.ClipId, allowOriginal: false);
            Require(
                CaptureJournalArtifactKinds.Renditions.Contains(
                    rendition.Kind,
                    StringComparer.Ordinal),
                "A completed rendition has an unknown kind.");
            Require(
                current.Clip.RequestedRenditions.Contains(
                    rendition.Kind,
                    StringComparer.Ordinal),
                "A completed rendition was not requested by the immutable capture snapshot.");
            artifacts = ReplaceArtifact(artifacts, rendition).ToList();
        }
        foreach (var requested in current.Clip.RequestedRenditions)
        {
            Require(
                artifacts.Any(candidate => candidate.Kind.Equals(requested, StringComparison.Ordinal)),
                $"The requested {requested} rendition has not been committed.");
        }
        return Next(
            current,
            CaptureJournalState.RenditionsReady,
            cameraPresent: true,
            artifacts,
            failureCode: null,
            now);
    }

    internal static CaptureJournalDocument AttachRendition(
        CaptureJournalDocument current,
        CaptureJournalArtifact rendition,
        DateTimeOffset now)
    {
        ValidateDocumentShape(current);
        ValidateArtifact(rendition, current.Clip.ClipId, allowOriginal: false);
        Require(
            current.State == CaptureJournalState.CameraPending,
            "A rendition can be attached only while camera work is pending.");
        Require(current.CameraPresent, "A rendition cannot be attached without a committed camera layer.");
        Require(
            current.Clip.RequestedRenditions.Contains(rendition.Kind, StringComparer.Ordinal),
            "The supplied rendition was not requested by the immutable capture snapshot.");
        var artifacts = ReplaceArtifact(current.Artifacts, rendition);
        var allReady = current.Clip.RequestedRenditions.All(requested =>
            artifacts.Any(candidate => candidate.Kind.Equals(requested, StringComparison.Ordinal)));
        return Next(
            current,
            allReady
                ? CaptureJournalState.RenditionsReady
                : CaptureJournalState.CameraPending,
            cameraPresent: true,
            artifacts,
            failureCode: null,
            now);
    }

    internal static CaptureJournalDocument FailRenditions(
        CaptureJournalDocument current,
        string failureCode,
        DateTimeOffset now)
    {
        ValidateDocumentShape(current);
        Require(
            current.State == CaptureJournalState.CameraPending,
            "Renditions can fail only while camera work is pending.");
        ValidateFailureCode(failureCode);
        return Next(
            current,
            CaptureJournalState.RenditionsFailed,
            current.CameraPresent,
            current.Artifacts,
            failureCode,
            now);
    }

    internal static void ValidateDocumentShape(CaptureJournalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(
            document.SchemaVersion == CaptureJournalStore.CurrentSchemaVersion,
            "The capture journal schema is not supported.");
        Require(document.Generation > 0, "The capture journal generation is invalid.");
        Require(document.ProcessingAttemptEpoch >= 0,
            "The capture journal processing attempt epoch is invalid.");
        Require(document.Clip is not null, "The capture journal clip metadata is missing.");
        var clip = document.Clip!;
        ValidateClip(clip);
        Require(document.Artifacts is not null, "The capture journal artifacts are missing.");
        var artifacts = document.Artifacts!;
        foreach (var artifact in artifacts)
        {
            ValidateArtifact(artifact, clip.ClipId, allowOriginal: false);
        }
        Require(
            artifacts.Select(candidate => candidate.Kind).Distinct(StringComparer.Ordinal).Count() ==
                artifacts.Count,
            "The capture journal contains duplicate artifact kinds.");
        Require(
            artifacts
                .Where(candidate => CaptureJournalArtifactKinds.Renditions.Contains(
                    candidate.Kind,
                    StringComparer.Ordinal))
                .All(candidate => clip.RequestedRenditions.Contains(
                    candidate.Kind,
                    StringComparer.Ordinal)),
            "The capture journal contains a rendition not requested by its immutable snapshot.");
        var cameraPresent = artifacts.Any(candidate =>
            candidate.Kind.Equals(CaptureJournalArtifactKinds.ReactionCamera, StringComparison.Ordinal));
        Require(
            document.CameraPresent == cameraPresent,
            "The capture journal camera marker does not match its artifacts.");
        Require(document.CreatedUtc.Offset == TimeSpan.Zero, "The capture journal creation time is not UTC.");
        Require(document.UpdatedUtc.Offset == TimeSpan.Zero, "The capture journal update time is not UTC.");
        Require(
            document.UpdatedUtc >= document.CreatedUtc,
            "The capture journal update time predates its creation.");

        switch (document.State)
        {
            case CaptureJournalState.OriginalCommitted:
                Require(document.ProcessingAttemptEpoch == 0 && document.ProcessingAttemptId is null,
                    "A committed original cannot contain a processing attempt.");
                Require(!document.CameraPresent, "A committed-original state cannot contain a camera layer.");
                Require(artifacts.Count == 0, "A committed-original state cannot contain derived artifacts.");
                Require(document.FailureCode is null, "A committed-original state cannot contain a failure.");
                break;
            case CaptureJournalState.CameraPending:
                ValidateProcessingAttempt(document);
                Require(clip.ReactionCameraRequested, "Camera work was not requested for this clip.");
                Require(document.FailureCode is null, "Pending camera work cannot contain a failure.");
                break;
            case CaptureJournalState.RenditionsReady:
                ValidateProcessingAttempt(document);
                Require(clip.ReactionCameraRequested, "Ready renditions require Reaction Camera.");
                Require(document.CameraPresent, "Ready renditions require a camera layer.");
                Require(document.FailureCode is null, "Ready renditions cannot contain a failure.");
                foreach (var requested in clip.RequestedRenditions)
                {
                    Require(
                        artifacts.Any(candidate => candidate.Kind.Equals(requested, StringComparison.Ordinal)),
                        $"The ready capture journal is missing its {requested} rendition.");
                }
                break;
            case CaptureJournalState.RenditionsFailed:
                ValidateProcessingAttempt(document);
                Require(clip.ReactionCameraRequested, "A rendition failure requires Reaction Camera.");
                ValidateFailureCode(document.FailureCode);
                break;
            default:
                throw new InvalidDataException("The capture journal state is invalid.");
        }
    }

    internal static bool HasSameImmutableIdentity(
        CaptureJournalDocument left,
        CaptureJournalDocument right) =>
        left.SchemaVersion == right.SchemaVersion &&
        ClipsEqual(left.Clip, right.Clip) &&
        left.CreatedUtc == right.CreatedUtc;

    internal static bool ClipsEqual(
        CaptureJournalClipMetadata left,
        CaptureJournalClipMetadata right) =>
        left.ClipId.Equals(right.ClipId, StringComparison.Ordinal) &&
        left.SourceKind == right.SourceKind &&
        left.GameName.Equals(right.GameName, StringComparison.Ordinal) &&
        left.CapturedUtc == right.CapturedUtc &&
        left.DurationTicks == right.DurationTicks &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.ReactionCameraRequested == right.ReactionCameraRequested &&
        left.RequestedRenditions.SequenceEqual(right.RequestedRenditions, StringComparer.Ordinal) &&
        left.Original == right.Original;

    internal static bool IsClipId(string? value) =>
        value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static string CreateArtifactId(string clipId, string kind)
    {
        if (!IsClipId(clipId)) throw new ArgumentException("The clip id is invalid.", nameof(clipId));
        if (!CaptureJournalArtifactKinds.IsKnown(kind))
        {
            throw new ArgumentException("The artifact kind is invalid.", nameof(kind));
        }
        return $"{clipId}:{kind}";
    }

    private static CaptureJournalClipMetadata NormalizeClip(CaptureJournalClipMetadata clip) =>
        clip with
        {
            GameName = clip.GameName.Trim(),
            CapturedUtc = RequireUtc(clip.CapturedUtc, nameof(clip.CapturedUtc)),
            RequestedRenditions = clip.RequestedRenditions
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Original = clip.Original with
            {
                Fingerprint = clip.Original.Fingerprint with
                {
                    Sha256 = clip.Original.Fingerprint.Sha256.ToLowerInvariant()
                }
            }
        };

    private static void ValidateClip(CaptureJournalClipMetadata clip)
    {
        Require(IsClipId(clip.ClipId), "The capture journal clip id is invalid.");
        Require(Enum.IsDefined(clip.SourceKind), "The capture journal source kind is invalid.");
        Require(
            !string.IsNullOrWhiteSpace(clip.GameName) && clip.GameName.Length <= 160,
            "The capture journal game name is invalid.");
        Require(clip.CapturedUtc.Offset == TimeSpan.Zero, "The capture time is not UTC.");
        Require(clip.DurationTicks > 0, "The capture duration is invalid.");
        Require(clip.Width > 0 && clip.Height > 0, "The capture dimensions are invalid.");
        Require(clip.RequestedRenditions is not null, "The requested renditions are missing.");
        var requested = clip.RequestedRenditions!;
        Require(
            requested.Distinct(StringComparer.Ordinal).Count() == requested.Count,
            "The requested renditions contain duplicates.");
        Require(
            requested.All(candidate =>
                CaptureJournalArtifactKinds.Renditions.Contains(candidate, StringComparer.Ordinal)),
            "A requested rendition kind is invalid.");
        Require(
            clip.ReactionCameraRequested == (requested.Count > 0),
            "Reaction Camera and requested rendition metadata disagree.");
        ValidateArtifact(clip.Original, clip.ClipId, allowOriginal: true);
        Require(
            clip.Original.Kind.Equals("original", StringComparison.Ordinal),
            "The clip source artifact is not the original gameplay file.");
    }

    internal static void ValidateArtifact(
        CaptureJournalArtifact artifact,
        string clipId,
        bool allowOriginal)
    {
        if (artifact is null) throw new InvalidDataException("A capture journal artifact is missing.");
        var expectedKinds = allowOriginal
            ? new[] { "original" }
            : new[]
            {
                CaptureJournalArtifactKinds.ReactionCamera,
                CaptureJournalArtifactKinds.Landscape,
                CaptureJournalArtifactKinds.Portrait
            };
        Require(expectedKinds.Contains(artifact.Kind, StringComparer.Ordinal),
            "A capture journal artifact kind is invalid.");
        var expectedId = allowOriginal
            ? $"{clipId}:original"
            : CreateArtifactId(clipId, artifact.Kind);
        Require(
            artifact.ArtifactId.Equals(expectedId, StringComparison.Ordinal),
            "A capture journal artifact id does not match its clip and kind.");
        ValidateRelativePath(artifact.RelativePath);
        var fingerprint = artifact.Fingerprint ??
            throw new InvalidDataException("A capture journal fingerprint is missing.");
        Require(fingerprint.ByteLength > 0, "A capture journal artifact is empty.");
        Require(IsSha256(fingerprint.Sha256), "A capture journal artifact hash is invalid.");
    }

    internal static void ValidateRelativePath(string relativePath)
    {
        Require(!string.IsNullOrWhiteSpace(relativePath) && relativePath.Length <= 1024,
            "A capture journal artifact path is invalid.");
        Require(!Path.IsPathRooted(relativePath), "A capture journal artifact path must be relative.");
        var normalized = relativePath.Replace('\\', '/');
        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Require(
            components.Length > 0 && components.All(component =>
                component is not "." and not ".." && !component.Contains(':')),
            "A capture journal artifact path escapes the library.");
        Require(
            Path.GetExtension(normalized).Equals(".mp4", StringComparison.OrdinalIgnoreCase),
            "A capture journal video artifact must use the MP4 container.");
    }

    private static IReadOnlyList<CaptureJournalArtifact> ReplaceArtifact(
        IReadOnlyList<CaptureJournalArtifact> existing,
        CaptureJournalArtifact replacement)
    {
        var result = existing
            .Where(candidate => !candidate.Kind.Equals(replacement.Kind, StringComparison.Ordinal))
            .Append(replacement)
            .OrderBy(candidate => candidate.Kind, StringComparer.Ordinal)
            .ToArray();
        return result;
    }

    private static CaptureJournalDocument Next(
        CaptureJournalDocument current,
        CaptureJournalState state,
        bool cameraPresent,
        IReadOnlyList<CaptureJournalArtifact> artifacts,
        string? failureCode,
        DateTimeOffset now,
        long? processingAttemptEpoch = null,
        string? processingAttemptId = null)
    {
        var utcNow = RequireUtc(now, nameof(now));
        Require(utcNow >= current.UpdatedUtc, "A capture journal transition cannot move backward in time.");
        var next = current with
        {
            Generation = checked(current.Generation + 1),
            State = state,
            ProcessingAttemptEpoch = processingAttemptEpoch ?? current.ProcessingAttemptEpoch,
            ProcessingAttemptId = processingAttemptId ?? current.ProcessingAttemptId,
            CameraPresent = cameraPresent,
            Artifacts = artifacts.ToArray(),
            FailureCode = failureCode,
            UpdatedUtc = utcNow
        };
        ValidateDocumentShape(next);
        return next;
    }

    private static void ValidateProcessingAttempt(CaptureJournalDocument document)
    {
        Require(document.ProcessingAttemptEpoch > 0,
            "Camera processing requires a positive durable attempt epoch.");
        Require(IsClipId(document.ProcessingAttemptId),
            "Camera processing requires a durable attempt id.");
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capture journal times must be UTC.", name);
        }
        return value;
    }

    private static void ValidateFailureCode(string? failureCode)
    {
        Require(
            failureCode is { Length: >= 1 and <= 80 } &&
            failureCode.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'),
            "The capture journal failure code is invalid.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

/// <summary>
/// Persists one bounded JSON record per captured clip under ClipCord's private library data.
/// Every replacement is generation-checked under a cross-process mutex and promoted from a
/// write-through temporary on the same volume. Callers persist OriginalCommitted immediately
/// after gameplay promotion, before optional camera or rendition work begins.
/// </summary>
internal static class CaptureJournalStore
{
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const string RoutingFolderName = "Routing";
    internal const string JournalFolderName = "CaptureJournal";

    private const string SaveMutexPrefix = @"Local\ClipCord.CaptureJournal.";
    private const int MaximumPublishedFileNameLength = 240;
    private static readonly char[] ClipIdAlphabet = "0123456789abcdef".ToCharArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
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

    internal static string GetJournalDirectory(string libraryRoot) =>
        Path.Combine(
            NormalizeLibraryRoot(libraryRoot),
            CaptureLibraryLayout.PrivateDataFolderName,
            RoutingFolderName,
            JournalFolderName);

    internal static string GetPath(string libraryRoot, string clipId)
    {
        ValidateClipId(clipId);
        return Path.Combine(GetJournalDirectory(libraryRoot), $"{clipId}.json");
    }

    internal static async Task<CaptureJournalDocument> CommitOriginalAsync(
        string libraryRoot,
        string originalPath,
        CaptureJournalSourceKind sourceKind,
        string gameName,
        DateTimeOffset capturedUtc,
        TimeSpan duration,
        int width,
        int height,
        bool reactionCameraRequested,
        IReadOnlyList<string> requestedRenditions,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null,
        CaptureJournalFingerprint? expectedFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(requestedRenditions);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        var normalizedOriginal = ValidateExistingArtifactPath(
            normalizedRoot,
            originalPath,
            "captured gameplay original");
        var relativePath = ToPortableRelativePath(normalizedRoot, normalizedOriginal);
        var clipId = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(relativePath);
        var original = await CreateArtifactAsync(
                normalizedRoot,
                clipId,
                "original",
                normalizedOriginal,
                cancellationToken)
            .ConfigureAwait(false);
        if (expectedFingerprint is not null && original.Fingerprint != expectedFingerprint)
        {
            throw new InvalidDataException(
                "The promoted original no longer matches its write-ahead fingerprint.");
        }
        var clip = new CaptureJournalClipMetadata(
            clipId,
            sourceKind,
            gameName,
            capturedUtc.ToUniversalTime(),
            duration.Ticks,
            width,
            height,
            reactionCameraRequested,
            requestedRenditions.ToArray(),
            original);
        var document = CaptureJournalModel.CreateOriginalCommitted(
            clip,
            (now ?? DateTimeOffset.UtcNow).ToUniversalTime());
        return await SaveInitialAsync(
                normalizedRoot,
                document,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CaptureJournalArtifact> CreateArtifactAsync(
        string libraryRoot,
        string clipId,
        string kind,
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        ValidateClipId(clipId);
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        var normalizedArtifact = ValidateExistingArtifactPath(
            normalizedRoot,
            artifactPath,
            "capture journal artifact");
        if (kind.Equals("original", StringComparison.Ordinal))
        {
            var relativeOriginal = ToPortableRelativePath(normalizedRoot, normalizedArtifact);
            if (!CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(relativeOriginal)
                    .Equals(clipId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The original artifact path does not match the capture project identity.");
            }
        }
        else
        {
            var load = Load(normalizedRoot, clipId, cancellationToken);
            if (!load.LoadedFromDisk || load.Document is null)
            {
                throw new InvalidDataException(
                    "A derived artifact cannot be bound without its capture journal metadata.");
            }
            var expectedPath = GetCanonicalArtifactPath(
                normalizedRoot,
                load.Document.Clip,
                kind);
            if (!normalizedArtifact.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The capture journal artifact is not at its canonical project path.");
            }
        }
        var fingerprint = await ReadFingerprintAsync(
                normalizedRoot,
                normalizedArtifact,
                "capture journal artifact",
                cancellationToken)
            .ConfigureAwait(false);
        var artifactId = kind.Equals("original", StringComparison.Ordinal)
            ? $"{clipId}:original"
            : CaptureJournalModel.CreateArtifactId(clipId, kind);
        var artifact = new CaptureJournalArtifact(
            artifactId,
            kind,
            ToPortableRelativePath(normalizedRoot, normalizedArtifact),
            fingerprint);
        CaptureJournalModel.ValidateArtifact(
            artifact,
            clipId,
            allowOriginal: kind.Equals("original", StringComparison.Ordinal));
        return artifact;
    }

    internal static async Task<CaptureJournalArtifactValidationResult> ValidateArtifactAsync(
        string libraryRoot,
        CaptureJournalArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var separator = artifact.ArtifactId.IndexOf(':');
        if (separator != 32)
        {
            throw new InvalidDataException("The capture journal artifact id is invalid.");
        }
        var clipId = artifact.ArtifactId[..separator];
        CaptureJournalModel.ValidateArtifact(
            artifact,
            clipId,
            allowOriginal: artifact.Kind.Equals("original", StringComparison.Ordinal));
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        var load = Load(normalizedRoot, clipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidDataException(
                "The capture journal artifact cannot be validated without its clip metadata.");
        }
        var expectedPath = GetCanonicalArtifactPath(
            normalizedRoot,
            load.Document.Clip,
            artifact.Kind);
        var path = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsurePathIsInside(normalizedRoot, path, "capture journal artifact");
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The capture journal artifact path is not canonical for its clip and kind.");
        }
        try
        {
            var parent = Path.GetDirectoryName(path) ??
                         throw new IOException(
                             "The capture journal artifact folder is invalid.");
            EnsureOrdinaryExistingPath(
                normalizedRoot,
                parent,
                requireDirectory: true,
                "capture journal artifact folder");
            if (!File.Exists(path))
            {
                return new CaptureJournalArtifactValidationResult(
                    CaptureJournalArtifactValidationStatus.Missing,
                    null);
            }
            var observed = await ReadFingerprintAsync(
                    normalizedRoot,
                    path,
                    "capture journal artifact",
                    cancellationToken)
                .ConfigureAwait(false);
            var matches = observed.ByteLength == artifact.Fingerprint.ByteLength &&
                          observed.Sha256.Equals(
                              artifact.Fingerprint.Sha256,
                              StringComparison.OrdinalIgnoreCase);
            return new CaptureJournalArtifactValidationResult(
                matches
                    ? CaptureJournalArtifactValidationStatus.Valid
                    : CaptureJournalArtifactValidationStatus.Mismatch,
                observed);
        }
        catch (FileNotFoundException)
        {
            return new CaptureJournalArtifactValidationResult(
                CaptureJournalArtifactValidationStatus.Missing,
                null);
        }
        catch (DirectoryNotFoundException)
        {
            return new CaptureJournalArtifactValidationResult(
                CaptureJournalArtifactValidationStatus.Missing,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            return new CaptureJournalArtifactValidationResult(
                CaptureJournalArtifactValidationStatus.Unavailable,
                null);
        }
    }

    internal static Task<CaptureJournalDocument> BeginCameraAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null) =>
        TransitionAsync(
            libraryRoot,
            clipId,
            expectedGeneration,
            current => CaptureJournalModel.BeginCamera(
                current,
                (now ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            cancellationToken);

    internal static Task<CaptureJournalDocument> AttachCameraAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        CaptureJournalArtifact camera,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null) =>
        TransitionAsync(
            libraryRoot,
            clipId,
            expectedGeneration,
            current => CaptureJournalModel.AttachCamera(
                current,
                camera,
                (now ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            cancellationToken);

    internal static Task<CaptureJournalDocument> CompleteRenditionsAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        IReadOnlyList<CaptureJournalArtifact> renditions,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null) =>
        TransitionAsync(
            libraryRoot,
            clipId,
            expectedGeneration,
            current => CaptureJournalModel.CompleteRenditions(
                current,
                renditions,
                (now ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            cancellationToken);

    internal static Task<CaptureJournalDocument> AttachRenditionAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        CaptureJournalArtifact rendition,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null) =>
        TransitionAsync(
            libraryRoot,
            clipId,
            expectedGeneration,
            current => CaptureJournalModel.AttachRendition(
                current,
                rendition,
                (now ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            cancellationToken);

    internal static Task<CaptureJournalDocument> FailRenditionsAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        string failureCode,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null) =>
        TransitionAsync(
            libraryRoot,
            clipId,
            expectedGeneration,
            current => CaptureJournalModel.FailRenditions(
                current,
                failureCode,
                (now ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            cancellationToken);

    internal static CaptureJournalLoadResult Load(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateClipId(clipId);
            var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
            var journalDirectory = GetJournalDirectory(normalizedRoot);
            var path = Path.Combine(journalDirectory, $"{clipId}.json");
            if (!File.Exists(path))
            {
                return new CaptureJournalLoadResult(
                    clipId,
                    null,
                    CaptureJournalLoadStatus.Missing);
            }
            EnsureOrdinaryExistingPath(
                normalizedRoot,
                path,
                requireDirectory: false,
                "capture journal entry");
            var bytes = ReadBoundedFile(path);
            int schemaVersion;
            using (var parsed = JsonDocument.Parse(bytes))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                    !schema.TryGetInt32(out schemaVersion))
                {
                    return new CaptureJournalLoadResult(
                        clipId,
                        null,
                        CaptureJournalLoadStatus.Invalid);
                }
            }
            if (schemaVersion != CurrentSchemaVersion)
            {
                return new CaptureJournalLoadResult(
                    clipId,
                    null,
                    CaptureJournalLoadStatus.UnsupportedSchema);
            }
            var document = JsonSerializer.Deserialize<CaptureJournalDocument>(bytes, JsonOptions);
            if (document is null)
            {
                return new CaptureJournalLoadResult(
                    clipId,
                    null,
                    CaptureJournalLoadStatus.Corrupt);
            }
            CaptureJournalModel.ValidateDocumentShape(document);
            if (!document.Clip.ClipId.Equals(clipId, StringComparison.Ordinal))
            {
                return new CaptureJournalLoadResult(
                    clipId,
                    null,
                    CaptureJournalLoadStatus.IdentityMismatch);
            }
            ValidateCanonicalArtifactPaths(normalizedRoot, document);
            return new CaptureJournalLoadResult(
                clipId,
                document,
                CaptureJournalLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new CaptureJournalLoadResult(
                clipId,
                null,
                CaptureJournalLoadStatus.Corrupt);
        }
        catch (InvalidDataException)
        {
            return new CaptureJournalLoadResult(
                clipId,
                null,
                CaptureJournalLoadStatus.Invalid);
        }
        catch (ArgumentException)
        {
            return new CaptureJournalLoadResult(
                clipId,
                null,
                CaptureJournalLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or PathTooLongException or
                System.Security.SecurityException)
        {
            return new CaptureJournalLoadResult(
                clipId,
                null,
                CaptureJournalLoadStatus.Unavailable);
        }
    }

    internal static CaptureJournalClipIdPage ReadClipIdPage(
        string libraryRoot,
        int maximumEntries,
        string? afterClipId = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (afterClipId is not null) ValidateClipId(afterClipId);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        var directory = GetJournalDirectory(normalizedRoot);
        if (!Directory.Exists(directory))
        {
            return new CaptureJournalClipIdPage([], null, HasMore: false);
        }
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            directory,
            requireDirectory: true,
            "capture journal folder");

        // Walk the hexadecimal filename namespace as a trie. Each filesystem query retains at
        // most page-size + 1 candidates; dense prefixes are subdivided instead of materializing
        // or sorting the full journal directory. The lexical cursor makes every id reachable on
        // a later page even when the underlying filesystem enumeration order changes.
        var target = checked(maximumEntries + 1);
        var collected = new List<string>(target);
        foreach (var first in ClipIdAlphabet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectClipIdsForPrefix(
                directory,
                first.ToString(),
                afterClipId,
                target,
                collected,
                cancellationToken);
            if (collected.Count >= target) break;
        }

        collected.Sort(StringComparer.Ordinal);
        var hasMore = collected.Count > maximumEntries;
        var page = collected.Take(maximumEntries).ToArray();
        return new CaptureJournalClipIdPage(
            page,
            page.Length == 0 ? null : page[^1],
            hasMore);
    }

    private static Task<CaptureJournalDocument> SaveInitialAsync(
        string normalizedRoot,
        CaptureJournalDocument document,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => SaveInitialCore(normalizedRoot, document, cancellationToken),
            cancellationToken);

    private static CaptureJournalDocument SaveInitialCore(
        string normalizedRoot,
        CaptureJournalDocument document,
        CancellationToken cancellationToken)
    {
        CaptureJournalModel.ValidateDocumentShape(document);
        ValidateCanonicalArtifactPaths(normalizedRoot, document);
        using var saveMutex = CreateSaveMutex(document.Clip.ClipId);
        var lockTaken = WaitForMutex(saveMutex, cancellationToken);
        try
        {
            using var mediaLeases = AcquireTransitionMediaLeases(
                normalizedRoot,
                document,
                cancellationToken);
            var directory = EnsureJournalDirectory(normalizedRoot);
            var path = Path.Combine(directory, $"{document.Clip.ClipId}.json");
            if (File.Exists(path))
            {
                var current = Load(normalizedRoot, document.Clip.ClipId, cancellationToken);
                if (!current.LoadedFromDisk || current.Document is null)
                {
                    throw new InvalidDataException(
                        "The existing capture journal entry cannot be reused safely.");
                }
                if (!CaptureJournalModel.ClipsEqual(current.Document.Clip, document.Clip))
                {
                    throw new CaptureJournalConcurrencyException(
                        "The capture journal clip id is already bound to different metadata.");
                }
                return current.Document;
            }
            WriteAtomically(directory, path, document, replacingExisting: false, cancellationToken);
            return document;
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private static Task<CaptureJournalDocument> TransitionAsync(
        string libraryRoot,
        string clipId,
        long expectedGeneration,
        Func<CaptureJournalDocument, CaptureJournalDocument> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (expectedGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        ValidateClipId(clipId);
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        return Task.Run(
            () => TransitionCore(
                normalizedRoot,
                clipId,
                expectedGeneration,
                transition,
                cancellationToken),
            cancellationToken);
    }

    private static CaptureJournalDocument TransitionCore(
        string normalizedRoot,
        string clipId,
        long expectedGeneration,
        Func<CaptureJournalDocument, CaptureJournalDocument> transition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var saveMutex = CreateSaveMutex(clipId);
        var lockTaken = WaitForMutex(saveMutex, cancellationToken);
        try
        {
            var current = Load(normalizedRoot, clipId, cancellationToken);
            if (!current.LoadedFromDisk || current.Document is null)
            {
                throw new InvalidDataException("The capture journal entry cannot be advanced safely.");
            }
            if (current.Document.Generation != expectedGeneration)
            {
                throw new CaptureJournalConcurrencyException(
                    "A newer capture journal generation has already been saved.");
            }
            var candidate = transition(current.Document);
            CaptureJournalModel.ValidateDocumentShape(candidate);
            ValidateCanonicalArtifactPaths(normalizedRoot, candidate);
            if (!CaptureJournalModel.HasSameImmutableIdentity(current.Document, candidate) ||
                candidate.Generation != checked(current.Document.Generation + 1))
            {
                throw new InvalidDataException(
                    "A capture journal transition changed immutable identity or skipped a generation.");
            }
            using var mediaLeases = AcquireTransitionMediaLeases(
                normalizedRoot,
                candidate,
                cancellationToken);
            var directory = EnsureJournalDirectory(normalizedRoot);
            var path = Path.Combine(directory, $"{clipId}.json");
            WriteAtomically(directory, path, candidate, replacingExisting: true, cancellationToken);
            return candidate;
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private static CaptureJournalMediaLeases AcquireTransitionMediaLeases(
        string normalizedRoot,
        CaptureJournalDocument candidate,
        CancellationToken cancellationToken)
    {
        var artifacts = candidate.State == CaptureJournalState.RenditionsFailed
            ? new[] { candidate.Clip.Original }
            : new[] { candidate.Clip.Original }.Concat(candidate.Artifacts).ToArray();
        var leases = new List<FileStream>(artifacts.Length);
        try
        {
            foreach (var artifact in artifacts.DistinctBy(value => value.ArtifactId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedPath = GetCanonicalArtifactPath(
                    normalizedRoot,
                    candidate.Clip,
                    artifact.Kind);
                var actualPath = Path.GetFullPath(Path.Combine(
                    normalizedRoot,
                    artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A transition artifact is not at its canonical path.");
                }
                EnsureOrdinaryExistingPath(
                    normalizedRoot,
                    actualPath,
                    requireDirectory: false,
                    "capture transition artifact");
                var stream = new FileStream(
                    actualPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                leases.Add(stream);
                if (stream.Length <= 0)
                {
                    throw new InvalidDataException("A capture transition artifact is empty.");
                }
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (stream.Length != artifact.Fingerprint.ByteLength ||
                    !hash.Equals(artifact.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A capture transition artifact no longer matches its committed fingerprint.");
                }
            }
            return new CaptureJournalMediaLeases(leases);
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    private static async Task<CaptureJournalFingerprint> ReadFingerprintAsync(
        string normalizedRoot,
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            path,
            requireDirectory: false,
            description);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0) throw new InvalidDataException($"The {description} is empty.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new CaptureJournalFingerprint(
            length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private sealed class CaptureJournalMediaLeases : IDisposable
    {
        private readonly IReadOnlyList<FileStream> _streams;

        internal CaptureJournalMediaLeases(IReadOnlyList<FileStream> streams) =>
            _streams = streams;

        public void Dispose()
        {
            foreach (var stream in _streams) stream.Dispose();
        }
    }

    private static void WriteAtomically(
        string directory,
        string destination,
        CaptureJournalDocument document,
        bool replacingExisting,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The capture journal entry is too large.");
        }
        var temporaryPath = Path.Combine(
            directory,
            $".{document.Clip.ClipId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: replacingExisting);
        }
        finally
        {
            TryDeleteOwnedTemporary(directory, temporaryPath);
        }
    }

    private static string EnsureJournalDirectory(string normalizedRoot)
    {
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            normalizedRoot,
            requireDirectory: true,
            "ClipCord library folder");
        var privateData = Path.Combine(normalizedRoot, CaptureLibraryLayout.PrivateDataFolderName);
        var routing = Path.Combine(privateData, RoutingFolderName);
        var journal = Path.Combine(routing, JournalFolderName);
        EnsureOrdinaryAncestorsBeforeCreate(normalizedRoot, journal);
        Directory.CreateDirectory(journal);
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            journal,
            requireDirectory: true,
            "capture journal folder");
        return journal;
    }

    private static string ValidateExistingArtifactPath(
        string normalizedRoot,
        string artifactPath,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        var normalized = Path.GetFullPath(artifactPath);
        EnsurePathIsInside(normalizedRoot, normalized, description);
        EnsureOrdinaryExistingPath(
            normalizedRoot,
            normalized,
            requireDirectory: false,
            description);
        if (!Path.GetExtension(normalized).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Capture journal video artifacts must use the MP4 container.");
        }
        return normalized;
    }

    internal static string GetCanonicalArtifactPath(
        string libraryRoot,
        CaptureJournalClipMetadata clip,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        var originalPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            clip.Original.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsurePathIsInside(normalizedRoot, originalPath, "capture journal original");
        if (!CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(
                ToPortableRelativePath(normalizedRoot, originalPath))
                .Equals(clip.ClipId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The capture journal original path does not match its project identity.");
        }

        if (kind.Equals("original", StringComparison.Ordinal)) return originalPath;
        if (kind.Equals(CaptureJournalArtifactKinds.ReactionCamera, StringComparison.Ordinal))
        {
            return Path.GetFullPath(CaptureProjectStore.GetCameraLayerPath(
                normalizedRoot,
                clip.ClipId));
        }

        var suffix = kind switch
        {
            CaptureJournalArtifactKinds.Landscape =>
                "__Reaction-Camera__Landscape.mp4",
            CaptureJournalArtifactKinds.Portrait =>
                "__Reaction-Camera__Portrait.mp4",
            _ => throw new InvalidDataException(
                "The capture journal artifact kind has no canonical path.")
        };
        var sourceStem = Path.GetFileNameWithoutExtension(originalPath);
        var fileName = sourceStem + suffix;
        if (fileName.Length > MaximumPublishedFileNameLength)
        {
            var identity = "__" + clip.ClipId[..12];
            var maximumStemLength = MaximumPublishedFileNameLength -
                                    identity.Length - suffix.Length;
            if (maximumStemLength <= 0)
            {
                throw new PathTooLongException(
                    "The capture journal rendition filename could not be bounded safely.");
            }
            sourceStem = sourceStem[..Math.Min(sourceStem.Length, maximumStemLength)];
            fileName = sourceStem + identity + suffix;
        }
        var originalDirectory = Path.GetDirectoryName(originalPath) ??
                                throw new IOException(
                                    "The capture journal original folder is invalid.");
        var outputPath = Path.GetFullPath(Path.Combine(originalDirectory, fileName));
        EnsurePathIsInside(originalDirectory, outputPath, "capture journal rendition");
        return outputPath;
    }

    private static void ValidateCanonicalArtifactPaths(
        string normalizedRoot,
        CaptureJournalDocument document)
    {
        var originalExpected = GetCanonicalArtifactPath(
            normalizedRoot,
            document.Clip,
            "original");
        var originalActual = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            document.Clip.Original.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        if (!originalActual.Equals(originalExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The capture journal original is not at its canonical project path.");
        }

        foreach (var artifact in document.Artifacts)
        {
            var expected = GetCanonicalArtifactPath(
                normalizedRoot,
                document.Clip,
                artifact.Kind);
            var actual = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsurePathIsInside(normalizedRoot, actual, "capture journal artifact");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A capture journal artifact is not at its canonical project path.");
            }
        }
    }

    private static void CollectClipIdsForPrefix(
        string directory,
        string prefix,
        string? afterClipId,
        int target,
        List<string> collected,
        CancellationToken cancellationToken)
    {
        if (collected.Count >= target) return;
        cancellationToken.ThrowIfCancellationRequested();

        if (afterClipId is not null)
        {
            var relation = string.CompareOrdinal(
                prefix,
                afterClipId[..Math.Min(prefix.Length, afterClipId.Length)]);
            if (relation < 0) return;
            if (relation == 0 && prefix.Length < afterClipId.Length)
            {
                foreach (var next in ClipIdAlphabet)
                {
                    CollectClipIdsForPrefix(
                        directory,
                        prefix + next,
                        afterClipId,
                        target,
                        collected,
                        cancellationToken);
                    if (collected.Count >= target) return;
                }
                return;
            }
            if (relation == 0 && prefix.Length == afterClipId.Length) return;
        }

        if (prefix.Length == 32)
        {
            if (File.Exists(Path.Combine(directory, prefix + ".json"))) collected.Add(prefix);
            return;
        }

        var remaining = target - collected.Count;
        var candidates = Directory
            .EnumerateFiles(directory, prefix + "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(candidate =>
                CaptureJournalModel.IsClipId(candidate) &&
                (afterClipId is null ||
                 string.CompareOrdinal(candidate, afterClipId) > 0))
            .Take(remaining + 1)
            .ToArray();
        if (candidates.Length <= remaining)
        {
            Array.Sort(candidates, StringComparer.Ordinal);
            collected.AddRange(candidates);
            return;
        }

        foreach (var next in ClipIdAlphabet)
        {
            CollectClipIdsForPrefix(
                directory,
                prefix + next,
                afterClipId,
                target,
                collected,
                cancellationToken);
            if (collected.Count >= target) return;
        }
    }

    internal static string NormalizeLibraryRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
    }

    private static string ToPortableRelativePath(string root, string path)
    {
        EnsurePathIsInside(root, path, "capture journal artifact");
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        CaptureJournalModel.ValidateRelativePath(relative);
        return relative;
    }

    private static byte[] ReadBoundedFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The capture journal entry has an invalid size.");
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The capture journal entry has an invalid size.");
        }
        return bytes;
    }

    internal static Mutex CreateSaveMutex(string clipId) =>
        new(initiallyOwned: false, SaveMutexPrefix + clipId);

    internal static bool WaitForMutex(Mutex mutex, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (mutex.WaitOne(TimeSpan.FromMilliseconds(100))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }

    private static void ValidateClipId(string clipId)
    {
        if (!CaptureJournalModel.IsClipId(clipId))
        {
            throw new ArgumentException("The capture journal clip id is invalid.", nameof(clipId));
        }
    }

    internal static void EnsurePathIsInside(string root, string candidate, string description)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} must stay inside the ClipCord library.");
        }
    }

    internal static void EnsureOrdinaryAncestorsBeforeCreate(string root, string candidate)
    {
        EnsurePathIsInside(root, candidate, "capture journal folder");
        var current = new DirectoryInfo(root);
        if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The ClipCord library folder is unavailable or redirected.");
        }
        var relative = Path.GetRelativePath(root, candidate);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, component));
            if (!current.Exists) break;
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The capture journal folder cannot use redirected directories.");
            }
        }
    }

    internal static void EnsureOrdinaryExistingPath(
        string root,
        string candidate,
        bool requireDirectory,
        string description)
    {
        EnsurePathIsInside(root, candidate, description);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The ClipCord library folder is unavailable or redirected.");
        }
        var relative = Path.GetRelativePath(root, candidate);
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var isLast = index == components.Length - 1;
            if (isLast && !requireDirectory)
            {
                var file = new FileInfo(current);
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"The {description} is unavailable or redirected.");
                }
            }
            else
            {
                var directory = new DirectoryInfo(current);
                if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"The {description} is unavailable or redirected.");
                }
            }
        }
    }

    private static void TryDeleteOwnedTemporary(string directory, string path)
    {
        try
        {
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var normalizedPath = Path.GetFullPath(path);
            if (Path.GetDirectoryName(normalizedPath)?.Equals(
                    normalizedDirectory,
                    StringComparison.OrdinalIgnoreCase) == true &&
                Path.GetFileName(normalizedPath).StartsWith(".", StringComparison.Ordinal) &&
                Path.GetExtension(normalizedPath).Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(normalizedPath))
            {
                var info = new FileInfo(normalizedPath);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
            }
        }
        catch
        {
            // A bounded startup cleanup pass can remove an abandoned private temporary later.
        }
    }
}
