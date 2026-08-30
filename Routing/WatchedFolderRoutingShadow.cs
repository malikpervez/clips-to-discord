using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum RoutingWatchedFolderIngestionMode
{
    Disabled,
    Shadow,
    Active
}

internal sealed record RoutingWatchedFolderIngestionOptions(
    RoutingWatchedFolderIngestionMode Mode)
{
    internal static RoutingWatchedFolderIngestionOptions Default { get; } =
        new(RoutingWatchedFolderIngestionMode.Disabled);

    internal static RoutingWatchedFolderIngestionOptions Shadow { get; } =
        new(RoutingWatchedFolderIngestionMode.Shadow);
}

internal interface IRoutingWatchedFolderLegacyConnectionAuthority
{
    string? ResolveCurrentConnectionId(
        string legacyWebhookIdentity,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableRoutingWatchedFolderLegacyConnectionAuthority :
    IRoutingWatchedFolderLegacyConnectionAuthority
{
    internal static UnavailableRoutingWatchedFolderLegacyConnectionAuthority Instance { get; } =
        new();

    private UnavailableRoutingWatchedFolderLegacyConnectionAuthority()
    {
    }

    public string? ResolveCurrentConnectionId(
        string legacyWebhookIdentity,
        CancellationToken cancellationToken)
    {
        RoutingValidation.RequireOpaqueId(
            legacyWebhookIdentity,
            128,
            "legacy Discord webhook identity");
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}

internal sealed class DiscordCatalogRoutingWatchedFolderLegacyConnectionAuthority(
    DiscordConnectionCatalog connections,
    AppSettings settings) : IRoutingWatchedFolderLegacyConnectionAuthority
{
    private readonly DiscordConnectionCatalog _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly AppSettings _settings =
        settings ?? throw new ArgumentNullException(nameof(settings));

    public string? ResolveCurrentConnectionId(
        string legacyWebhookIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.UploadToDiscord ||
            !DiscordRoutingConnectionIdentity.TryCreate(
                _settings.WebhookUrl,
                out var currentIdentity) ||
            !currentIdentity.Equals(legacyWebhookIdentity, StringComparison.Ordinal))
        {
            return null;
        }
        return _connections.GetLegacyConnectionIds(_settings, cancellationToken)
            .SingleOrDefault();
    }
}

internal sealed record RoutingWatchedFolderCandidate(
    string ClipsRoot,
    string FilePath,
    string FileKey,
    string ContentSha256,
    long ByteLength,
    long LastWriteUtcTicks,
    ClipCaptureSource CaptureSource,
    LegacyRoutingMode LegacyMode,
    string? LegacyWebhookIdentity,
    string GameName)
{
    internal static RoutingWatchedFolderCandidate Create(
        AppSettings settings,
        FileInfo clip,
        string fileKey,
        string contentSha256)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clip);
        var source = AppSettings.NormalizeCaptureSource(settings.CaptureSource);
        var game = source == ClipCaptureSource.Nvidia
            ? clip.Directory?.Name
            : UploadedFolder.GetGameFolderName(clip.Name);
        var mode = settings.UploadToDiscord
            ? LegacyRoutingMode.DiscordUpload
            : LegacyRoutingMode.LocalOnly;
        string? legacyWebhookIdentity = null;
        if (mode == LegacyRoutingMode.DiscordUpload &&
            DiscordRoutingConnectionIdentity.TryCreate(
                settings.WebhookUrl,
                out var resolvedConnectionId))
        {
            legacyWebhookIdentity = resolvedConnectionId;
        }
        return new RoutingWatchedFolderCandidate(
            settings.ClipsFolder,
            clip.FullName,
            fileKey,
            contentSha256.ToLowerInvariant(),
            clip.Length,
            clip.LastWriteTimeUtc.Ticks,
            source,
            mode,
            legacyWebhookIdentity,
            UploadedFolder.SanitizeGameFolderName(game));
    }
}

internal sealed record RoutingWatchedFolderMediaInfo(
    TimeSpan Duration,
    int Width,
    int Height);

internal interface IRoutingWatchedFolderMediaProbe
{
    Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
        string filePath,
        CancellationToken cancellationToken);
}

internal sealed class FfmpegRoutingWatchedFolderMediaProbe : IRoutingWatchedFolderMediaProbe
{
    public async Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FfmpegCompressor.FindExecutable() ??
                     throw new FileNotFoundException(
                         "The routing shadow probe requires ffmpeg.exe next to ClipCord.");
        var probe = await FfmpegCompressor.ProbeMediaAsync(
                filePath,
                ffmpeg,
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.Duration <= TimeSpan.Zero || probe.VideoWidth <= 0 || probe.VideoHeight <= 0)
        {
            throw new InvalidDataException(
                "The watched clip media metadata is incomplete.");
        }
        return new RoutingWatchedFolderMediaInfo(
            probe.Duration,
            probe.VideoWidth,
            probe.VideoHeight);
    }
}

internal enum RoutingWatchedShadowObservationKind
{
    Planned,
    NoWork,
    LegacyKnownExcluded,
    LegacyUploadedExcluded,
    LegacyLocalOnlyExcluded,
    ContentDuplicate
}

internal sealed record RoutingWatchedShadowDelivery(
    RoutingDestinationKind Destination,
    string ConnectionId,
    RoutingOutputKind RequestedOutput,
    RoutingOutputKind EffectiveOutput,
    RoutingDeliveryMode Mode,
    PlannedDeliveryState State,
    RoutingMissingArtifactOutcome ArtifactOutcome);

internal sealed record RoutingWatchedShadowObservation(
    string SourceClipId,
    string SourceIdentitySha256,
    string ContentSha256,
    ClipCaptureSource CaptureSource,
    string GameName,
    long DurationMilliseconds,
    int Width,
    int Height,
    long RoutingGeneration,
    RoutingWatchedShadowObservationKind Kind,
    string? DuplicateOfSourceClipId,
    IReadOnlyList<Guid> MatchedRouteIds,
    IReadOnlyList<RoutingWatchedShadowDelivery> Deliveries,
    RoutingLibraryArea? LibraryArea,
    string? PlanFingerprint,
    DateTimeOffset ObservedUtc)
{
    public bool Equals(RoutingWatchedShadowObservation? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SourceClipId == other.SourceClipId &&
        SourceIdentitySha256 == other.SourceIdentitySha256 &&
        ContentSha256 == other.ContentSha256 && CaptureSource == other.CaptureSource &&
        GameName == other.GameName && DurationMilliseconds == other.DurationMilliseconds &&
        Width == other.Width && Height == other.Height &&
        RoutingGeneration == other.RoutingGeneration && Kind == other.Kind &&
        DuplicateOfSourceClipId == other.DuplicateOfSourceClipId &&
        LibraryArea == other.LibraryArea && PlanFingerprint == other.PlanFingerprint &&
        ObservedUtc == other.ObservedUtc &&
        RoutingStructural.SequenceEqual(MatchedRouteIds, other.MatchedRouteIds) &&
        RoutingStructural.SequenceEqual(Deliveries, other.Deliveries);

    public override int GetHashCode() => RoutingStructural.Hash(
        SourceClipId,
        SourceIdentitySha256,
        ContentSha256,
        CaptureSource,
        GameName,
        DurationMilliseconds,
        Width,
        Height,
        RoutingGeneration,
        Kind,
        DuplicateOfSourceClipId,
        MatchedRouteIds,
        Deliveries,
        LibraryArea,
        PlanFingerprint,
        ObservedUtc);
}

internal sealed record RoutingWatchedShadowDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<RoutingWatchedShadowObservation> Observations,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(RoutingWatchedShadowDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && CreatedUtc == other.CreatedUtc &&
        UpdatedUtc == other.UpdatedUtc &&
        RoutingStructural.SequenceEqual(Observations, other.Observations);

    public override int GetHashCode() => RoutingStructural.Hash(
        SchemaVersion,
        Generation,
        Observations,
        CreatedUtc,
        UpdatedUtc);
}

internal static class RoutingWatchedShadowModel
{
    internal const int MaximumObservations = 2_000;

    internal static RoutingWatchedShadowDocument CreateEmpty(DateTimeOffset now)
    {
        var timestamp = RoutingValidation.Utc(now);
        return new RoutingWatchedShadowDocument(
            RoutingWatchedShadowStore.CurrentSchemaVersion,
            Generation: 1,
            Observations: [],
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp);
    }

    internal static RoutingWatchedShadowDocument Append(
        RoutingWatchedShadowDocument current,
        RoutingWatchedShadowObservation observation,
        DateTimeOffset now)
    {
        Validate(current);
        ValidateObservation(observation);
        RoutingValidation.Require(current.Observations.Count < MaximumObservations,
            "The watched-folder shadow store has reached its bounded capacity.");
        RoutingValidation.Require(current.Observations.All(candidate =>
                !candidate.SourceClipId.Equals(
                    observation.SourceClipId,
                    StringComparison.Ordinal)),
            "A watched-folder occurrence is already recorded.");
        var timestamp = RoutingValidation.Utc(now);
        if (timestamp < current.UpdatedUtc) timestamp = current.UpdatedUtc;
        return current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Observations = current.Observations.Append(observation).ToArray(),
            UpdatedUtc = timestamp
        };
    }

    internal static void Validate(RoutingWatchedShadowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingWatchedShadowStore.CurrentSchemaVersion,
            "The watched-folder shadow schema is unsupported.");
        RoutingValidation.Require(document.Generation > 0,
            "The watched-folder shadow generation is invalid.");
        var observations = document.Observations ??
                           throw new InvalidDataException(
                               "The watched-folder shadow observations are missing.");
        RoutingValidation.Require(observations.Count <= MaximumObservations,
            "The watched-folder shadow observations are invalid.");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "watched-folder shadow creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc,
            "watched-folder shadow update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The watched-folder shadow timestamps are inconsistent.");
        foreach (var observation in observations)
        {
            ValidateObservation(observation);
        }
        RoutingValidation.Require(observations
                .Select(observation => observation.SourceClipId)
                .Distinct(StringComparer.Ordinal)
                .Count() == observations.Count,
            "The watched-folder shadow store contains duplicate occurrence identities.");
        var priorById = new Dictionary<string, RoutingWatchedShadowObservation>(
            StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation.DuplicateOfSourceClipId is { } duplicateOf)
            {
                RoutingValidation.Require(
                    priorById.TryGetValue(duplicateOf, out var original) &&
                    original.ContentSha256.Equals(
                        observation.ContentSha256,
                        StringComparison.Ordinal),
                    "The watched-folder duplicate evidence does not reference earlier equal content.");
            }
            priorById.Add(observation.SourceClipId, observation);
        }
    }

    internal static void ValidateSuccessor(
        RoutingWatchedShadowDocument current,
        RoutingWatchedShadowDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(candidate.Generation == current.Generation + 1 &&
                                  candidate.CreatedUtc == current.CreatedUtc &&
                                  candidate.UpdatedUtc >= current.UpdatedUtc &&
                                  candidate.Observations.Count == current.Observations.Count + 1 &&
                                  candidate.Observations.Take(current.Observations.Count)
                                      .SequenceEqual(current.Observations),
            "The watched-folder shadow successor changed durable history.");
    }

    private static void ValidateObservation(RoutingWatchedShadowObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        RoutingValidation.RequireOpaqueId(observation.SourceClipId, 32,
            "watched source clip id");
        RoutingValidation.RequireSha256(observation.SourceIdentitySha256,
            "watched source identity");
        RoutingValidation.RequireSha256(observation.ContentSha256,
            "watched source content hash");
        RoutingValidation.Require(observation.SourceClipId.Length == 32 &&
                                  observation.SourceClipId.All(character =>
                                      character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                                  observation.SourceIdentitySha256.StartsWith(
                                      observation.SourceClipId,
                                      StringComparison.Ordinal),
            "The watched source clip id is not canonical.");
        RoutingValidation.Require(observation.SourceIdentitySha256.All(character =>
                                      character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                                  observation.ContentSha256.All(character =>
                                      character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Watched source hashes must be canonical lowercase values.");
        RoutingValidation.Require(Enum.IsDefined(observation.CaptureSource) &&
                                  Enum.IsDefined(observation.Kind),
            "The watched source or observation kind is unsupported.");
        RoutingValidation.RequireText(observation.GameName, 1, 160,
            "watched source game name");
        RoutingValidation.Require(observation.RoutingGeneration > 0,
            "The watched shadow route generation is invalid.");
        var matchedRouteIds = observation.MatchedRouteIds ??
                              throw new InvalidDataException(
                                  "The watched shadow matched routes are missing.");
        var deliveries = observation.Deliveries ??
                         throw new InvalidDataException(
                             "The watched shadow deliveries are missing.");
        RoutingValidation.Require(matchedRouteIds.Distinct().Count() ==
                                  matchedRouteIds.Count,
            "The watched shadow plan summary is invalid.");
        foreach (var delivery in deliveries)
        {
            ArgumentNullException.ThrowIfNull(delivery);
            RoutingValidation.Require(Enum.IsDefined(delivery.Destination) &&
                                      Enum.IsDefined(delivery.RequestedOutput) &&
                                      Enum.IsDefined(delivery.EffectiveOutput) &&
                                      Enum.IsDefined(delivery.Mode) &&
                                      Enum.IsDefined(delivery.State) &&
                                      Enum.IsDefined(delivery.ArtifactOutcome),
                "A watched shadow delivery summary is unsupported.");
            RoutingValidation.RequireOpaqueId(delivery.ConnectionId, 128,
                "watched shadow connection id");
        }
        RoutingValidation.RequireOptionalOpaqueId(observation.DuplicateOfSourceClipId, 32,
            "duplicate watched source clip id");
        RoutingValidation.RequireOptionalOpaqueId(observation.PlanFingerprint, 64,
            "watched shadow plan fingerprint");
        if (observation.PlanFingerprint is not null)
        {
            RoutingValidation.RequireSha256(observation.PlanFingerprint,
                "watched shadow plan fingerprint");
        }
        RoutingValidation.RequireUtc(observation.ObservedUtc,
            "watched shadow observation timestamp");

        var evaluated = observation.Kind is RoutingWatchedShadowObservationKind.Planned or
            RoutingWatchedShadowObservationKind.NoWork or
            RoutingWatchedShadowObservationKind.ContentDuplicate;
        RoutingValidation.Require(evaluated
                ? observation.DurationMilliseconds > 0 && observation.Width > 0 &&
                  observation.Height > 0 && observation.PlanFingerprint is not null
                : observation.DurationMilliseconds == 0 && observation.Width == 0 &&
                  observation.Height == 0 && deliveries.Count == 0 &&
                  observation.LibraryArea is null && observation.PlanFingerprint is null,
            "The watched shadow observation payload does not match its kind.");
        RoutingValidation.Require(
            observation.Kind == RoutingWatchedShadowObservationKind.ContentDuplicate
                ? observation.DuplicateOfSourceClipId is not null
                : observation.DuplicateOfSourceClipId is null,
            "The watched shadow duplicate evidence is inconsistent.");
        RoutingValidation.Require(
            observation.Kind == RoutingWatchedShadowObservationKind.Planned
                ? deliveries.Count > 0 || observation.LibraryArea is not null
                : observation.Kind is not RoutingWatchedShadowObservationKind.NoWork ||
                  deliveries.Count == 0 && observation.LibraryArea is null,
            "The watched shadow work summary is inconsistent.");
    }
}

internal sealed class RoutingWatchedShadowStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 2 * 1024 * 1024;
    internal const string FileName = "watched-shadow.json";

    private readonly RoutingAtomicJsonStore<RoutingWatchedShadowDocument> _store;

    internal RoutingWatchedShadowStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingWatchedShadowStore(string path)
    {
        _store = new RoutingAtomicJsonStore<RoutingWatchedShadowDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingWatchedShadowModel.Validate,
            RoutingWatchedShadowModel.ValidateSuccessor,
            ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingWatchedShadowDocument> Load(
        CancellationToken cancellationToken = default) =>
        _store.Load(cancellationToken);

    internal Task<RoutingWatchedShadowDocument> LoadOrCreateAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => RoutingWatchedShadowModel.CreateEmpty(now),
            cancellationToken);

    internal Task<RoutingWatchedShadowDocument> SaveAsync(
        RoutingWatchedShadowDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken);

    private static void ValidateInitial(RoutingWatchedShadowDocument document)
    {
        RoutingWatchedShadowModel.Validate(document);
        RoutingValidation.Require(document.Generation == 1 &&
                                  document.Observations.Count == 0 &&
                                  document.CreatedUtc == document.UpdatedUtc,
            "The first watched-folder shadow document is not canonical.");
    }
}

internal enum RoutingWatchedFolderObservationStatus
{
    Disabled,
    GateUnavailable,
    AlreadyObserved,
    CapacityNeedsAttention,
    Recorded
}

internal sealed record RoutingWatchedFolderObservationResult(
    RoutingWatchedFolderObservationStatus Status,
    string? SourceClipId,
    RoutingWatchedShadowObservationKind? Kind,
    long? StoreGeneration);

internal interface IRoutingWatchedFolderObserver
{
    ValueTask<RoutingWatchedFolderObservationResult> ObserveAsync(
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken);
}

internal sealed class DisabledRoutingWatchedFolderObserver : IRoutingWatchedFolderObserver
{
    internal static DisabledRoutingWatchedFolderObserver Instance { get; } = new();

    private DisabledRoutingWatchedFolderObserver()
    {
    }

    public ValueTask<RoutingWatchedFolderObservationResult> ObserveAsync(
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RoutingWatchedFolderObservationResult(
            RoutingWatchedFolderObservationStatus.Disabled,
            null,
            null,
            null));
    }
}

/// <summary>
/// Evaluates the exact stable occurrence already admitted by the legacy watcher. Shadow mode is
/// deliberately isolated: it writes only a bounded, path-free comparison record and never opens
/// the production outbox, a provider, a filer, runtime ownership, or a migration transaction.
/// </summary>
internal sealed class RoutingWatchedFolderShadowObserver : IRoutingWatchedFolderObserver
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingWatchedFolderIngestionOptions _options;
    private readonly RoutingSnapshotStore _snapshots;
    private readonly LegacyRoutingMigrationMarkerStore _markers;
    private readonly RoutingWatchedShadowStore _shadow;
    private readonly IRoutingWatchedFolderMediaProbe _mediaProbe;
    private readonly IRoutingWatchedFolderLegacyConnectionAuthority _connectionAuthority;
    private readonly Func<Guid> _createPlanId;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingWatchedFolderShadowObserver(
        RoutingWatchedFolderIngestionOptions options,
        RoutingSnapshotStore snapshots,
        LegacyRoutingMigrationMarkerStore markers,
        RoutingWatchedShadowStore shadow,
        IRoutingWatchedFolderMediaProbe mediaProbe,
        IRoutingWatchedFolderLegacyConnectionAuthority? connectionAuthority = null,
        Func<Guid>? createPlanId = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.Mode == RoutingWatchedFolderIngestionMode.Active)
        {
            throw new NotSupportedException(
                "Live watched-folder routing requires the durable authority and execution-barrier slice.");
        }
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        _connectionAuthority = connectionAuthority ??
                               UnavailableRoutingWatchedFolderLegacyConnectionAuthority.Instance;
        _createPlanId = createPlanId ?? Guid.NewGuid;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<RoutingWatchedFolderObservationResult> ObserveAsync(
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Mode != RoutingWatchedFolderIngestionMode.Shadow)
        {
            return new RoutingWatchedFolderObservationResult(
                RoutingWatchedFolderObservationStatus.Disabled,
                null,
                null,
                null);
        }

        var snapshotLoad = _snapshots.Load(cancellationToken);
        if (!snapshotLoad.LoadedFromDisk || snapshotLoad.Document is null ||
            !CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(
                _markers,
                snapshotLoad.Document,
                cancellationToken))
        {
            return new RoutingWatchedFolderObservationResult(
                RoutingWatchedFolderObservationStatus.GateUnavailable,
                null,
                null,
                null);
        }
        var markerLoad = _markers.Load(cancellationToken);
        if (!markerLoad.LoadedFromDisk || markerLoad.Document is null ||
            !MatchesMarkerSource(markerLoad.Document, candidate, cancellationToken))
        {
            return new RoutingWatchedFolderObservationResult(
                RoutingWatchedFolderObservationStatus.GateUnavailable,
                null,
                null,
                null);
        }

        var normalized = ValidateCandidate(candidate);
        var identity = CreateOccurrenceIdentity(normalized);
        var sourceClipId = identity[..32];
        var existingLoad = _shadow.Load(cancellationToken);
        if (existingLoad.Status is not (RoutingDocumentLoadStatus.Missing or
            RoutingDocumentLoadStatus.Loaded))
        {
            throw new InvalidDataException(
                $"The watched-folder shadow store cannot be trusted ({existingLoad.Status}).");
        }
        var existing = existingLoad.Document?.Observations.SingleOrDefault(observation =>
            observation.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!existing.SourceIdentitySha256.Equals(identity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A watched-folder occurrence identity collided with different evidence.");
            }
            return new RoutingWatchedFolderObservationResult(
                RoutingWatchedFolderObservationStatus.AlreadyObserved,
                sourceClipId,
                existing.Kind,
                existingLoad.Document!.Generation);
        }

        var marker = markerLoad.Document;
        var exclusionKind = ClassifyLegacyExclusion(marker.ContentHashExclusions, normalized.ContentSha256);
        RoutingWatchedShadowObservation plannedObservation;
        if (exclusionKind is { } excluded)
        {
            plannedObservation = CreateTerminalObservation(
                normalized,
                identity,
                sourceClipId,
                snapshotLoad.Document.Generation,
                excluded,
                duplicateOf: null);
        }
        else
        {
            var priorContent = existingLoad.Document?.Observations.FirstOrDefault(observation =>
                observation.ContentSha256.Equals(
                    normalized.ContentSha256,
                    StringComparison.Ordinal));
            var media = await _mediaProbe.ProbeAsync(
                    normalized.FilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            normalized = await RevalidateAfterProbeAsync(
                    normalized,
                    cancellationToken)
                .ConfigureAwait(false);
            plannedObservation = CreatePlannedObservation(
                normalized,
                identity,
                sourceClipId,
                snapshotLoad.Document,
                media);
            if (priorContent is not null)
            {
                plannedObservation = plannedObservation with
                {
                    Kind = RoutingWatchedShadowObservationKind.ContentDuplicate,
                    DuplicateOfSourceClipId = priorContent.SourceClipId
                };
            }
        }

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _shadow.LoadOrCreateAsync(
                    RoutingValidation.Utc(_utcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            var recorded = current.Observations.SingleOrDefault(observation =>
                observation.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal));
            if (recorded is not null)
            {
                if (!recorded.SourceIdentitySha256.Equals(identity, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A watched-folder occurrence identity collided during a concurrent save.");
                }
                return new RoutingWatchedFolderObservationResult(
                    RoutingWatchedFolderObservationStatus.AlreadyObserved,
                    sourceClipId,
                    recorded.Kind,
                    current.Generation);
            }
            if (current.Observations.Count >= RoutingWatchedShadowModel.MaximumObservations)
            {
                return new RoutingWatchedFolderObservationResult(
                    RoutingWatchedFolderObservationStatus.CapacityNeedsAttention,
                    sourceClipId,
                    null,
                    current.Generation);
            }

            var toAppend = plannedObservation;
            if (exclusionKind is null)
            {
                var concurrentDuplicate = current.Observations.FirstOrDefault(observation =>
                    observation.ContentSha256.Equals(
                        normalized.ContentSha256,
                        StringComparison.Ordinal));
                if (concurrentDuplicate is not null)
                {
                    toAppend = plannedObservation with
                    {
                        Kind = RoutingWatchedShadowObservationKind.ContentDuplicate,
                        DuplicateOfSourceClipId = concurrentDuplicate.SourceClipId
                    };
                }
            }
            var timestamp = RoutingValidation.Utc(_utcNow());
            toAppend = toAppend with { ObservedUtc = timestamp };
            var candidateDocument = RoutingWatchedShadowModel.Append(
                current,
                toAppend,
                timestamp);
            try
            {
                var saved = await _shadow.SaveAsync(
                        candidateDocument,
                        current.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new RoutingWatchedFolderObservationResult(
                    RoutingWatchedFolderObservationStatus.Recorded,
                    sourceClipId,
                    toAppend.Kind,
                    saved.Generation);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reclassify against the winning durable history on the next iteration.
            }
        }

        throw new RoutingConcurrencyException(
            "The watched-folder shadow store kept changing while an observation was appended.");
    }

    private RoutingWatchedShadowObservation CreatePlannedObservation(
        RoutingWatchedFolderCandidate candidate,
        string identity,
        string sourceClipId,
        RoutingSnapshotDocument snapshot,
        RoutingWatchedFolderMediaInfo media)
    {
        if (media.Duration <= TimeSpan.Zero || media.Width <= 0 || media.Height <= 0)
        {
            throw new InvalidDataException(
                "The watched-folder media probe returned invalid dimensions or duration.");
        }
        var original = RoutingEvaluator.CreateLogicalOutputReference(
            sourceClipId,
            candidate.ContentSha256,
            RoutingOutputKind.Original);
        var outputs = new[]
        {
            new RoutingClipOutputRevision(
                original,
                RoutingOutputAvailability.Ready,
                FailureCode: null),
            MissingOutput(sourceClipId, candidate.ContentSha256, RoutingOutputKind.Landscape),
            MissingOutput(sourceClipId, candidate.ContentSha256, RoutingOutputKind.Portrait)
        };
        var facts = new RoutingClipFacts(
            sourceClipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.WatchedFolder,
            RoutingTriggerKind.WatchedFolder,
            CaptureType: null,
            candidate.GameName,
            ReactionCamera: false,
            checked((long)media.Duration.TotalMilliseconds),
            candidate.ContentSha256,
            outputs);
        var now = RoutingValidation.Utc(_utcNow());
        var proposal = RoutingEvaluator.CreatePlan(
            snapshot,
            facts,
            _createPlanId(),
            deliberateDuplicateAuthorizations: [],
            now);
        var deliveries = proposal.Deliveries.Select(delivery =>
            new RoutingWatchedShadowDelivery(
                delivery.Destination,
                delivery.ConnectionId,
                delivery.RequestedOutput.Kind,
                delivery.Output.Kind,
                delivery.Mode,
                delivery.State,
                delivery.ArtifactOutcome)).ToArray();
        var fingerprint = CreatePlanFingerprint(
            snapshot.Generation,
            proposal.MatchedRouteIds,
            deliveries,
            proposal.FileDisposition?.LibraryArea);
        return new RoutingWatchedShadowObservation(
            sourceClipId,
            identity,
            candidate.ContentSha256,
            candidate.CaptureSource,
            candidate.GameName,
            checked((long)media.Duration.TotalMilliseconds),
            media.Width,
            media.Height,
            snapshot.Generation,
            proposal.HasWork
                ? RoutingWatchedShadowObservationKind.Planned
                : RoutingWatchedShadowObservationKind.NoWork,
            DuplicateOfSourceClipId: null,
            proposal.MatchedRouteIds.ToArray(),
            deliveries,
            proposal.FileDisposition?.LibraryArea,
            fingerprint,
            now);
    }

    private RoutingWatchedShadowObservation CreateTerminalObservation(
        RoutingWatchedFolderCandidate candidate,
        string identity,
        string sourceClipId,
        long routingGeneration,
        RoutingWatchedShadowObservationKind kind,
        string? duplicateOf) => new(
        sourceClipId,
        identity,
        candidate.ContentSha256,
        candidate.CaptureSource,
        candidate.GameName,
        DurationMilliseconds: 0,
        Width: 0,
        Height: 0,
        routingGeneration,
        kind,
        duplicateOf,
        MatchedRouteIds: [],
        Deliveries: [],
        LibraryArea: null,
        PlanFingerprint: null,
        RoutingValidation.Utc(_utcNow()));

    private static RoutingClipOutputRevision MissingOutput(
        string sourceClipId,
        string contentSha256,
        RoutingOutputKind kind) => new(
        RoutingEvaluator.CreateLogicalOutputReference(sourceClipId, contentSha256, kind),
        RoutingOutputAvailability.PermanentlyMissing,
        "watched-output-not-produced");

    internal static RoutingWatchedFolderCandidate ValidateCandidate(
        RoutingWatchedFolderCandidate candidate)
    {
        RoutingValidation.RequireSha256(candidate.ContentSha256,
            "watched clip content hash");
        RoutingValidation.Require(candidate.ContentSha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The watched clip content hash must be canonical lowercase text.");
        RoutingValidation.Require(candidate.ByteLength > 0 && candidate.LastWriteUtcTicks > 0,
            "The watched clip file identity is invalid.");
        RoutingValidation.Require(Enum.IsDefined(candidate.CaptureSource),
            "The watched clip capture source is unsupported.");
        RoutingValidation.Require(Enum.IsDefined(candidate.LegacyMode) &&
                                  (candidate.LegacyMode == LegacyRoutingMode.DiscordUpload
                                      ? candidate.LegacyWebhookIdentity is not null
                                      : candidate.LegacyWebhookIdentity is null),
            "The watched clip legacy mode or Discord connection is invalid.");
        RoutingValidation.RequireOptionalOpaqueId(
            candidate.LegacyWebhookIdentity,
            128,
            "watched clip legacy webhook identity");
        RoutingValidation.RequireText(candidate.GameName, 1, 160,
            "watched clip game name");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.ClipsRoot));
        var path = Path.GetFullPath(candidate.FilePath);
        var relative = Path.GetRelativePath(root, path);
        RoutingValidation.Require(!Path.IsPathRooted(relative) && relative != ".." &&
                                  !relative.StartsWith(
                                      ".." + Path.DirectorySeparatorChar,
                                      StringComparison.Ordinal) &&
                                  Path.GetExtension(path).Equals(
                                      ".mp4",
                                       StringComparison.OrdinalIgnoreCase),
            "The watched clip must remain an MP4 inside its configured root.");
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var exactLayout = candidate.CaptureSource switch
        {
            ClipCaptureSource.SteelSeriesGg => components.Length == 1,
            ClipCaptureSource.Nvidia => components.Length == 2 &&
                                        !AppSettings.ManagedChildFolderNames.Contains(
                                            components[0],
                                            StringComparer.OrdinalIgnoreCase),
            _ => false
        };
        RoutingValidation.Require(exactLayout,
            "The watched clip does not match its capture source layout.");
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "watched clip");
        var file = new FileInfo(path);
        file.Refresh();
        RoutingValidation.Require(file.Exists && file.Length == candidate.ByteLength &&
                                  file.LastWriteTimeUtc.Ticks == candidate.LastWriteUtcTicks &&
                                  WatchStateStore.FileKey(file).Equals(
                                      candidate.FileKey,
                                      StringComparison.OrdinalIgnoreCase),
            "The watched clip changed after legacy readiness and hashing.");
        return candidate with
        {
            ClipsRoot = root,
            FilePath = path,
            ContentSha256 = candidate.ContentSha256.ToLowerInvariant(),
            GameName = candidate.CaptureSource == ClipCaptureSource.Nvidia
                ? UploadedFolder.SanitizeGameFolderName(components[0])
                : UploadedFolder.SanitizeGameFolderName(
                    UploadedFolder.GetGameFolderName(components[0]))
        };
    }

    internal static async Task<RoutingWatchedFolderCandidate> RevalidateAfterProbeAsync(
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken)
    {
        var beforeHash = ValidateCandidate(candidate);
        var actualHash = (await ContentIdentity.ComputeSha256Async(
                beforeHash.FilePath,
                cancellationToken)
            .ConfigureAwait(false)).ToLowerInvariant();
        var afterHash = ValidateCandidate(beforeHash);
        RoutingValidation.Require(actualHash.Equals(
                afterHash.ContentSha256,
                StringComparison.Ordinal),
            "The watched clip content changed while shadow media facts were collected.");
        return afterHash;
    }

    internal bool MatchesMarkerSource(
        LegacyRoutingMigrationMarker marker,
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (marker.Mode != candidate.LegacyMode) return false;
        string? connectionId = null;
        if (candidate.LegacyMode == LegacyRoutingMode.DiscordUpload)
        {
            if (candidate.LegacyWebhookIdentity is null) return false;
            connectionId = _connectionAuthority.ResolveCurrentConnectionId(
                candidate.LegacyWebhookIdentity,
                cancellationToken);
            if (connectionId is null) return false;
        }
        var expected = LegacyRoutingMigrationPlanner.CreateSourceFingerprint(
            candidate.LegacyMode,
            candidate.ClipsRoot,
            candidate.CaptureSource,
            connectionId,
            marker.CaptureLibraryBinding,
            marker.ContentHashExclusions);
        return marker.SourceFingerprint.Equals(expected, StringComparison.Ordinal);
    }

    internal static RoutingWatchedShadowObservationKind? ClassifyLegacyExclusion(
        LegacyContentHashExclusions exclusions,
        string contentSha256)
    {
        if (exclusions.Uploaded.Contains(contentSha256, StringComparer.OrdinalIgnoreCase))
        {
            return RoutingWatchedShadowObservationKind.LegacyUploadedExcluded;
        }
        if (exclusions.LocalOnly.Contains(contentSha256, StringComparer.OrdinalIgnoreCase))
        {
            return RoutingWatchedShadowObservationKind.LegacyLocalOnlyExcluded;
        }
        return exclusions.Known.Contains(contentSha256, StringComparer.OrdinalIgnoreCase)
            ? RoutingWatchedShadowObservationKind.LegacyKnownExcluded
            : null;
    }

    private static string CreateOccurrenceIdentity(RoutingWatchedFolderCandidate candidate)
    {
        var relative = Path.GetRelativePath(candidate.ClipsRoot, candidate.FilePath)
            .Replace('\\', '/')
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"clipcord-watched-occurrence-v1\n{candidate.CaptureSource}\n{relative}\n" +
            $"{candidate.ByteLength}\n{candidate.LastWriteUtcTicks}\n{candidate.ContentSha256}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    private static string CreatePlanFingerprint(
        long routingGeneration,
        IReadOnlyList<Guid> matchedRouteIds,
        IReadOnlyList<RoutingWatchedShadowDelivery> deliveries,
        RoutingLibraryArea? libraryArea)
    {
        var text = new StringBuilder()
            .Append("clipcord-watched-shadow-plan-v1\n")
            .Append(routingGeneration.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (var routeId in matchedRouteIds) text.Append(routeId.ToString("N")).Append('\n');
        foreach (var delivery in deliveries)
        {
            text.Append(delivery.Destination).Append('|')
                .Append(delivery.ConnectionId).Append('|')
                .Append(delivery.RequestedOutput).Append('|')
                .Append(delivery.EffectiveOutput).Append('|')
                .Append(delivery.Mode).Append('|')
                .Append(delivery.State).Append('|')
                .Append(delivery.ArtifactOutcome).Append('\n');
        }
        text.Append(libraryArea?.ToString() ?? "none");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
            .ToLowerInvariant();
    }
}
