using System.Collections.Frozen;

namespace ClipsToDiscord;

internal enum RoutingRuntimeSourceEventKind
{
    SourceClipCommitted,
    CaptureJournalReconciled,
    DerivedArtifactCommitted,
    FileArchived
}

/// <summary>
/// A routing source is always a durable capture-journal identity. Derived-artifact and archive
/// notifications may wake artifact/disposition workers in a later slice, but they can never
/// create a second plan for the same source clip.
/// </summary>
internal sealed record RoutingRuntimeSourceEvent(
    RoutingRuntimeSourceEventKind Kind,
    CaptureJournalReconciliationItem? JournalItem);

internal enum RoutingRuntimeArtifactReadiness
{
    Ready,
    Pending,
    Unavailable
}

internal sealed record RoutingRuntimeOutput(
    RoutingOutputReference Reference,
    RoutingRuntimeArtifactReadiness Readiness,
    CaptureJournalArtifact? CommittedArtifact);

internal sealed record RoutingRuntimePlanningContext(
    RoutingSnapshotDocument RoutingSnapshot,
    CaptureJournalDocument CaptureJournal,
    IReadOnlyDictionary<RoutingOutputKind, RoutingRuntimeOutput> Outputs,
    DateTimeOffset PlannedUtc)
{
    internal RoutingRuntimeOutput GetOutput(RoutingOutputKind kind) =>
        Outputs.TryGetValue(kind, out var output)
            ? output
            : throw new InvalidDataException($"The {kind} output inventory is missing.");
}

/// <summary>
/// The evaluator remains a pure dependency. It receives one immutable route generation and one
/// validated artifact inventory, and returns the complete plan to append in a single outbox CAS.
/// It must not perform I/O, uploads, moves, or configuration writes.
/// </summary>
internal interface IRoutingRuntimePlanner
{
    RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context);
}

/// <summary>Adapter from validated journal facts to the pure deterministic route evaluator.</summary>
internal sealed class RoutingEvaluatorRuntimePlanner : IRoutingRuntimePlanner
{
    private readonly Func<Guid> _createPlanId;
    private readonly Func<string, IReadOnlyList<IntentionalDuplicateProvenance>>
        _loadDuplicateAuthorizations;

    internal RoutingEvaluatorRuntimePlanner(
        Func<Guid>? createPlanId = null,
        Func<string, IReadOnlyList<IntentionalDuplicateProvenance>>?
            loadDuplicateAuthorizations = null)
    {
        _createPlanId = createPlanId ?? Guid.NewGuid;
        _loadDuplicateAuthorizations = loadDuplicateAuthorizations ?? (_ => []);
    }

    public RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var facts = CreateFacts(context);
        var journal = context.CaptureJournal;
        var authorizations = _loadDuplicateAuthorizations(journal.Clip.ClipId) ??
                             throw new InvalidDataException(
                                 "Intentional duplicate authorization evidence is missing.");
        return RoutingEvaluator.CreatePlan(
            context.RoutingSnapshot,
            facts,
            _createPlanId(),
            authorizations,
            context.PlannedUtc);
    }

    internal static RoutingClipFacts CreateFacts(RoutingRuntimePlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var journal = context.CaptureJournal;
        var original = context.GetOutput(RoutingOutputKind.Original);
        var outputs = context.Outputs.Values
            .OrderBy(candidate => candidate.Reference.Kind)
            .Select(candidate => new RoutingClipOutputRevision(
                candidate.Reference,
                candidate.Readiness switch
                {
                    RoutingRuntimeArtifactReadiness.Ready => RoutingOutputAvailability.Ready,
                    RoutingRuntimeArtifactReadiness.Pending => RoutingOutputAvailability.Pending,
                    RoutingRuntimeArtifactReadiness.Unavailable =>
                        RoutingOutputAvailability.PermanentlyMissing,
                    _ => throw new InvalidDataException(
                        "The runtime output readiness is unsupported.")
                },
                candidate.Readiness == RoutingRuntimeArtifactReadiness.Unavailable
                    ? IsRequested(journal, candidate.Reference.Kind) &&
                      journal.State == CaptureJournalState.RenditionsFailed
                        ? journal.FailureCode ?? "rendition-failed"
                        : "output-not-produced"
                    : null))
            .ToArray();
        var sourceKind = journal.Clip.SourceKind;
        return new RoutingClipFacts(
            journal.Clip.ClipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.ClipCordCapture,
            sourceKind == CaptureJournalSourceKind.InstantReplay
                ? RoutingTriggerKind.InstantReplay
                : RoutingTriggerKind.ManualRecording,
            sourceKind == CaptureJournalSourceKind.InstantReplay
                ? RoutingCaptureType.InstantReplay
                : RoutingCaptureType.ManualRecording,
            journal.Clip.GameName,
            journal.Clip.ReactionCameraRequested,
            checked(journal.Clip.DurationTicks / TimeSpan.TicksPerMillisecond),
            original.CommittedArtifact!.Fingerprint.Sha256,
            outputs);
    }

    private static bool IsRequested(CaptureJournalDocument journal, RoutingOutputKind kind) =>
        kind switch
        {
            RoutingOutputKind.Original => true,
            RoutingOutputKind.Landscape => journal.Clip.RequestedRenditions.Contains(
                CaptureJournalArtifactKinds.Landscape, StringComparer.Ordinal),
            RoutingOutputKind.Portrait => journal.Clip.RequestedRenditions.Contains(
                CaptureJournalArtifactKinds.Portrait, StringComparer.Ordinal),
            _ => false
        };
}

internal enum RoutingRuntimeGateState
{
    DisabledByDefault,
    OwnershipUnavailable,
    MigrationMarkerMissing,
    MigrationMarkerNotCommitted,
    LegacyStateUnavailable,
    LegacyQueuesPending,
    MigrationEvidenceMismatch,
    RoutingSnapshotUnavailable,
    MigrationRouteMismatch,
    SourceCoverageMissing,
    ExecutionAuthorityUnavailable,
    ExecutionAuthorityMismatch,
    Enabled
}

/// <summary>
/// Fail-closed cutover evidence. There is deliberately no method here that creates the migration
/// marker or drains legacy work: those are separate migration responsibilities. Merely
/// constructing the runtime bridge never activates it.
/// </summary>
internal sealed class RoutingRuntimeFeatureGate
{
    private readonly bool _requestedEnabled;
    private readonly LegacyRoutingMigrationMarkerStore? _migrationMarkers;
    private readonly RoutingSnapshotStore? _routingSnapshots;
    private readonly ILegacyRoutingActivationEvidenceSource? _activationEvidence;
    private readonly ClipProcessingOwnershipLease? _ownership;
    private readonly IReadOnlySet<ClipCaptureSource> _coveredLegacySources;
    private readonly RoutingExecutionAuthorityStore? _executionAuthority;

    private RoutingRuntimeFeatureGate(
        bool requestedEnabled,
        LegacyRoutingMigrationMarkerStore? migrationMarkers,
        RoutingSnapshotStore? routingSnapshots,
        ILegacyRoutingActivationEvidenceSource? activationEvidence,
        ClipProcessingOwnershipLease? ownership,
        IReadOnlySet<ClipCaptureSource> coveredLegacySources,
        RoutingExecutionAuthorityStore? executionAuthority)
    {
        _requestedEnabled = requestedEnabled;
        _migrationMarkers = migrationMarkers;
        _routingSnapshots = routingSnapshots;
        _activationEvidence = activationEvidence;
        _ownership = ownership;
        foreach (var source in coveredLegacySources)
        {
            RoutingValidation.Require(
                Enum.IsDefined(source) && AppSettings.NormalizeCaptureSource(source) == source,
                "Routing runtime source coverage contains an unsupported adapter claim.");
        }
        _coveredLegacySources = coveredLegacySources.ToFrozenSet();
        _executionAuthority = executionAuthority;
    }

    internal static RoutingRuntimeFeatureGate Disabled { get; } = new(
        requestedEnabled: false,
        migrationMarkers: null,
        routingSnapshots: null,
        activationEvidence: null,
        ownership: null,
        coveredLegacySources: FrozenSet<ClipCaptureSource>.Empty,
        executionAuthority: null);

    internal RoutingRuntimeGateState State => Inspect().State;
    internal int PendingLegacyMoves => Inspect().PendingLegacyMoves;
    internal int PendingLegacyLocalOnlyMoves => Inspect().PendingLegacyLocalOnlyMoves;
    internal int PendingLegacyEditedUploads => Inspect().PendingLegacyEditedUploads;
    internal int IgnoredLegacyFileKeys => Inspect().IgnoredLegacyFileKeys;
    internal bool Enabled => Inspect().Enabled;

    internal static RoutingRuntimeFeatureGate Evaluate(
        bool requestedEnabled,
        LegacyRoutingMigrationMarkerStore migrationMarkers,
        RoutingSnapshotStore routingSnapshots,
        ILegacyRoutingActivationEvidenceSource activationEvidence,
        ClipProcessingOwnershipLease ownership,
        IReadOnlySet<ClipCaptureSource> coveredLegacySources,
        RoutingExecutionAuthorityStore? executionAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(migrationMarkers);
        ArgumentNullException.ThrowIfNull(routingSnapshots);
        ArgumentNullException.ThrowIfNull(activationEvidence);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(coveredLegacySources);
        return new RoutingRuntimeFeatureGate(
            requestedEnabled,
            migrationMarkers,
            routingSnapshots,
            activationEvidence,
            ownership,
            coveredLegacySources,
            executionAuthority);
    }

    internal RoutingRuntimeGateInspection Inspect()
    {
        if (!_requestedEnabled)
        {
            return Inspection(RoutingRuntimeGateState.DisabledByDefault);
        }

        var ownership = _ownership;
        if (ownership is null ||
            ownership.Owner != ClipProcessingRuntimeOwner.Routing ||
            !ownership.IsCurrent)
        {
            return Inspection(RoutingRuntimeGateState.OwnershipUnavailable);
        }

        var executionAuthority = _executionAuthority?.Inspect();
        if (executionAuthority is null || !executionAuthority.RoutingRequired ||
            executionAuthority.Document is null)
        {
            return Inspection(RoutingRuntimeGateState.ExecutionAuthorityUnavailable,
                ownership.Epoch);
        }
        var authorityDocument = executionAuthority.Document;

        var markerStore = _migrationMarkers;
        if (markerStore is null)
        {
            return Inspection(RoutingRuntimeGateState.MigrationMarkerMissing, ownership.Epoch);
        }

        var marker = markerStore.Load();
        if (marker.Status == RoutingDocumentLoadStatus.Missing)
        {
            return Inspection(RoutingRuntimeGateState.MigrationMarkerMissing, ownership.Epoch);
        }
        if (!marker.LoadedFromDisk || marker.Document is null ||
            marker.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed)
        {
            return Inspection(
                RoutingRuntimeGateState.MigrationMarkerNotCommitted,
                ownership.Epoch);
        }
        if (authorityDocument.MigrationId != marker.Document.MigrationId ||
            !authorityDocument.MigrationPayloadFingerprint.Equals(
                marker.Document.PayloadFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !authorityDocument.SourceFingerprint.Equals(
                marker.Document.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            authorityDocument.CaptureLibraryBinding !=
            marker.Document.CaptureLibraryBinding)
        {
            return Inspection(
                RoutingRuntimeGateState.ExecutionAuthorityMismatch,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                executionAuthorityActivationId: authorityDocument.ActivationId);
        }

        var evidence = _activationEvidence?.Inspect();
        if (evidence is null || !evidence.Loaded)
        {
            return Inspection(
                evidence?.Status == LegacyRoutingActivationEvidenceStatus.LegacyStateUnavailable
                    ? RoutingRuntimeGateState.LegacyStateUnavailable
                    : RoutingRuntimeGateState.MigrationEvidenceMismatch,
                ownership.Epoch,
                marker.Document.PayloadFingerprint);
        }

        var legacyState = evidence.LegacyState;
        var requiredLegacySource = legacyState.State is null
            ? (ClipCaptureSource?)null
            : AppSettings.NormalizeCaptureSource(legacyState.State.CaptureSource);
        if (requiredLegacySource is not { } authoritySource ||
            authoritySource != authorityDocument.RequiredLegacySource)
        {
            return Inspection(
                RoutingRuntimeGateState.ExecutionAuthorityMismatch,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                requiredLegacySource: requiredLegacySource,
                executionAuthorityActivationId: authorityDocument.ActivationId);
        }
        var pendingMoves = legacyState.PendingMoves;
        var pendingLocalOnlyMoves = legacyState.PendingLocalOnlyMoves;
        var pendingEditedUploads = legacyState.PendingEditedUploads;
        var ignoredFileKeys = legacyState.IgnoredFileKeys;
        if (pendingMoves != 0 || pendingLocalOnlyMoves != 0 || pendingEditedUploads != 0 ||
            ignoredFileKeys != 0)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.LegacyQueuesPending,
                pendingMoves,
                pendingLocalOnlyMoves,
                pendingEditedUploads,
                ignoredFileKeys,
                null,
                marker.Document.PayloadFingerprint,
                ownership.Epoch,
                requiredLegacySource,
                _coveredLegacySources,
                authorityDocument.ActivationId);
        }

        var readiness = evidence.Readiness!;
        var plan = readiness.Plan;
        if (!readiness.CanCommit || plan is null ||
            plan.MigrationId != marker.Document.MigrationId ||
            plan.Mode != marker.Document.Mode || plan.Scope != marker.Document.Scope ||
            plan.CaptureLibraryBinding != marker.Document.CaptureLibraryBinding ||
            !plan.SourceFingerprint.Equals(
                marker.Document.SourceFingerprint,
                StringComparison.Ordinal) ||
            plan.ContentHashExclusions != marker.Document.ContentHashExclusions ||
            !LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                plan.Route,
                marker.Document.Route))
        {
            return Inspection(
                RoutingRuntimeGateState.MigrationEvidenceMismatch,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                requiredLegacySource: requiredLegacySource);
        }

        var snapshot = _routingSnapshots?.Load();
        if (snapshot is null || !snapshot.LoadedFromDisk || snapshot.Document is null)
        {
            return Inspection(
                RoutingRuntimeGateState.RoutingSnapshotUnavailable,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                requiredLegacySource: requiredLegacySource);
        }
        var migrationRoute = snapshot.Document.Routes.SingleOrDefault(route =>
            route.RouteId == marker.Document.Route.RouteId);
        if (migrationRoute is null ||
            !LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                migrationRoute,
                marker.Document.Route))
        {
            return Inspection(
                RoutingRuntimeGateState.MigrationRouteMismatch,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                snapshot.Document.Generation,
                requiredLegacySource);
        }

        if (requiredLegacySource is not { } required ||
            !_coveredLegacySources.Contains(required))
        {
            return Inspection(
                RoutingRuntimeGateState.SourceCoverageMissing,
                ownership.Epoch,
                marker.Document.PayloadFingerprint,
                snapshot.Document.Generation,
                requiredLegacySource);
        }

        return new RoutingRuntimeGateInspection(
            RoutingRuntimeGateState.Enabled,
            0,
            0,
            0,
            0,
            snapshot.Document.Generation,
            marker.Document.PayloadFingerprint,
            ownership.Epoch,
            requiredLegacySource,
            _coveredLegacySources,
            authorityDocument.ActivationId);
    }

    private RoutingRuntimeGateInspection Inspection(
        RoutingRuntimeGateState state,
        long? ownershipEpoch = null,
        string? markerPayloadFingerprint = null,
        long? routingGeneration = null,
        ClipCaptureSource? requiredLegacySource = null,
        Guid? executionAuthorityActivationId = null) => new(
        state,
        0,
        0,
        0,
        0,
        routingGeneration,
        markerPayloadFingerprint,
        ownershipEpoch,
        requiredLegacySource,
        _coveredLegacySources,
        executionAuthorityActivationId);
}

internal sealed record RoutingRuntimeGateInspection(
    RoutingRuntimeGateState State,
    int PendingLegacyMoves,
    int PendingLegacyLocalOnlyMoves,
    int PendingLegacyEditedUploads,
    int IgnoredLegacyFileKeys,
    long? RoutingGeneration,
    string? MarkerPayloadFingerprint,
    long? OwnershipEpoch,
    ClipCaptureSource? RequiredLegacySource,
    IReadOnlySet<ClipCaptureSource> CoveredLegacySources,
    Guid? ExecutionAuthorityActivationId = null)
{
    internal bool HasRequiredSourceCoverage =>
        RequiredLegacySource is { } required && CoveredLegacySources.Contains(required);

    internal bool Enabled =>
        State == RoutingRuntimeGateState.Enabled && HasRequiredSourceCoverage;

    internal bool SamePermit(RoutingRuntimeGateInspection other) =>
        SameAuthority(other) && RoutingGeneration == other.RoutingGeneration &&
        RequiredLegacySource == other.RequiredLegacySource &&
        CoveredLegacySources.SetEquals(other.CoveredLegacySources);

    internal bool SameAuthority(RoutingRuntimeGateInspection other) =>
        other.Enabled &&
        ExecutionAuthorityActivationId is not null &&
        ExecutionAuthorityActivationId == other.ExecutionAuthorityActivationId &&
        MarkerPayloadFingerprint == other.MarkerPayloadFingerprint &&
        OwnershipEpoch == other.OwnershipEpoch &&
        RequiredLegacySource == other.RequiredLegacySource &&
        CoveredLegacySources.SetEquals(other.CoveredLegacySources);
}

internal enum RoutingRuntimePlanStatus
{
    Disabled,
    IgnoredNonSourceEvent,
    AlreadyPlanned,
    AlreadyArchived,
    CapacityNeedsAttention,
    Planned
}

internal sealed record RoutingRuntimePlanResult(
    RoutingRuntimePlanStatus Status,
    string? SourceClipId,
    Guid? PlanId,
    long? OutboxGeneration);

/// <summary>
/// Disabled-by-default durability bridge for the routing foundation. It validates a capture
/// journal event, freezes one route generation through an injected pure planner, and atomically
/// appends exactly one plan for that SourceClipId. This type performs no provider, filesystem
/// disposition, or UI work and currently has no live production caller.
/// </summary>
internal sealed class RoutingRuntimeBridge : ICaptureJournalReconciliationHandler
{
    private const int MaximumSaveAttempts = 4;
    private readonly string _libraryRoot;
    private readonly RoutingSnapshotStore _snapshotStore;
    private readonly RoutingOutboxStore _outboxStore;
    private readonly IRoutingRuntimePlanner _planner;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly RoutingCaptureLibraryPermit _captureLibraryPermit;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingRuntimeBridge(
        string libraryRoot,
        RoutingSnapshotStore snapshotStore,
        RoutingOutboxStore outboxStore,
        IRoutingRuntimePlanner planner,
        RoutingRuntimeFeatureGate? featureGate = null,
        Func<DateTimeOffset>? utcNow = null,
        RoutingCaptureLibraryPermit? captureLibraryPermit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.GetFullPath(libraryRoot);
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _featureGate = featureGate ?? RoutingRuntimeFeatureGate.Disabled;
        var initialBinding = captureLibraryPermit is null
            ? RoutingCaptureLibraryBindingModel.Create(_libraryRoot)
            : null;
        _captureLibraryPermit = captureLibraryPermit ?? new RoutingCaptureLibraryPermit(
            initialBinding!,
            () => RoutingCaptureLibraryBindingModel.Create(_libraryRoot));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal RoutingRuntimeFeatureGate FeatureGate => _featureGate;
    internal RoutingCaptureLibraryPermit CaptureLibraryPermit => _captureLibraryPermit;

    public async ValueTask ReconcileAsync(
        CaptureJournalReconciliationItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanPlanDeliveries) return;
        var result = await PlanAsync(
                new RoutingRuntimeSourceEvent(
                    RoutingRuntimeSourceEventKind.CaptureJournalReconciled,
                    item),
                cancellationToken)
            .ConfigureAwait(false);
        if ((result.Status is RoutingRuntimePlanStatus.Planned or
                RoutingRuntimePlanStatus.AlreadyPlanned) &&
            result.PlanId is { } planId && item.Document is not null)
        {
            await ReconcileExistingPlanArtifactsAsync(
                    item.Document,
                    planId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<RoutingRuntimePlanResult> PlanAsync(
        RoutingRuntimeSourceEvent sourceEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent);
        var activationPermit = _featureGate.Inspect();
        if (!activationPermit.Enabled)
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.Disabled,
                null,
                null,
                null);
        }
        _ = _captureLibraryPermit.RequireCurrent("Capture journal validation");
        if (sourceEvent.Kind is not RoutingRuntimeSourceEventKind.SourceClipCommitted and
            not RoutingRuntimeSourceEventKind.CaptureJournalReconciled)
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.IgnoredNonSourceEvent,
                sourceEvent.JournalItem?.ClipId,
                null,
                null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var item = await ValidateSourceEventAsync(sourceEvent, cancellationToken)
            .ConfigureAwait(false);
        var sourceClipId = item.Document!.Clip.ClipId;
        var loadedSnapshot = _snapshotStore.Load(cancellationToken);
        if (!loadedSnapshot.LoadedFromDisk || loadedSnapshot.Document is null ||
            loadedSnapshot.Document.Generation != activationPermit.RoutingGeneration)
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.Disabled,
                sourceClipId,
                null,
                null);
        }
        var snapshot = loadedSnapshot.Document;
        _ = _captureLibraryPermit.RequireCurrent("Routing outbox planning admission");
        var admission = await _outboxStore.PrepareForPlanningAsync(
                _utcNow(),
                cancellationToken,
                () => _captureLibraryPermit.RequireCurrent(
                    "Routing outbox planning-admission commit"))
            .ConfigureAwait(false);
        var current = admission.Document;
        if (TryFindExistingPlan(current, sourceClipId, out var existingPlan))
        {
            ValidateArchiveOverlap(current, sourceClipId, existingPlan, cancellationToken);
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.AlreadyPlanned,
                sourceClipId,
                existingPlan,
                current.Generation);
        }
        if (TryFindArchivedPlan(sourceClipId, cancellationToken, out existingPlan))
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.AlreadyArchived,
                sourceClipId,
                existingPlan,
                current.Generation);
        }
        if (!admission.CanAcceptNewPlan)
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.CapacityNeedsAttention,
                sourceClipId,
                null,
                current.Generation);
        }

        var plannedUtc = RoutingValidation.Utc(_utcNow());
        var context = new RoutingRuntimePlanningContext(
            snapshot,
            item.Document,
            BuildOutputInventory(item.Document),
            plannedUtc);
        var proposal = _planner.BuildPlan(context) ??
                       throw new InvalidDataException("The routing planner returned no plan.");
        ValidateProposal(context, proposal);

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!activationPermit.SamePermit(_featureGate.Inspect()))
            {
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.Disabled,
                    sourceClipId,
                    null,
                    current.Generation);
            }
            if (TryFindExistingPlan(current, sourceClipId, out existingPlan))
            {
                ValidateArchiveOverlap(current, sourceClipId, existingPlan, cancellationToken);
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.AlreadyPlanned,
                    sourceClipId,
                    existingPlan,
                    current.Generation);
            }
            if (TryFindArchivedPlan(sourceClipId, cancellationToken, out existingPlan))
            {
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.AlreadyArchived,
                    sourceClipId,
                    existingPlan,
                    current.Generation);
            }

            var appendUtc = current.UpdatedUtc > plannedUtc
                ? current.UpdatedUtc
                : plannedUtc;
            var candidate = RoutingOutboxModel.AppendEvaluatedPlan(
                current,
                proposal,
                appendUtc);
            if (_outboxStore.MeasureSerializedBytes(candidate) >
                _outboxStore.PlanningAdmissionLimitBytes)
            {
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.CapacityNeedsAttention,
                    sourceClipId,
                    null,
                    current.Generation);
            }
            try
            {
                if (!activationPermit.SamePermit(_featureGate.Inspect()))
                {
                    return new RoutingRuntimePlanResult(
                        RoutingRuntimePlanStatus.Disabled,
                        sourceClipId,
                        null,
                        current.Generation);
                }
                _ = _captureLibraryPermit.RequireCurrent("Routing outbox plan commit");
                var saved = await _outboxStore.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken,
                        () => _captureLibraryPermit.RequireCurrent(
                            "Routing outbox plan commit"))
                    .ConfigureAwait(false);
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.Planned,
                    sourceClipId,
                    proposal.PlanId,
                    saved.Generation);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                var reloaded = _outboxStore.Load(cancellationToken);
                if (!reloaded.LoadedFromDisk || reloaded.Document is null)
                {
                    throw new InvalidDataException(
                        $"The routing outbox could not be reloaded after a concurrent save ({reloaded.Status}).");
                }
                current = reloaded.Document;
            }
        }

        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while a source plan was appended.");
    }

    private async Task ReconcileExistingPlanArtifactsAsync(
        CaptureJournalDocument journal,
        Guid planId,
        CancellationToken cancellationToken)
    {
        var authority = _featureGate.Inspect();
        if (!authority.Enabled) return;

        var outputs = BuildOutputInventory(journal).Values
            .Where(output => output.Reference.Kind != RoutingOutputKind.Original)
            .ToArray();
        var readyOutputs = outputs
            .Where(output => output.Readiness == RoutingRuntimeArtifactReadiness.Ready)
            .Select(output => output.Reference)
            .ToArray();
        var failedOutputs = new Dictionary<RoutingOutputReference, string>();
        if (journal.State == CaptureJournalState.RenditionsFailed)
        {
            foreach (var output in outputs.Where(output =>
                         output.Readiness == RoutingRuntimeArtifactReadiness.Unavailable &&
                         IsRequestedRendition(journal, output.Reference.Kind)))
            {
                failedOutputs.Add(
                    output.Reference,
                    journal.FailureCode ?? throw new InvalidDataException(
                        "A failed rendition journal has no durable failure code."));
            }
        }

        var load = _outboxStore.Load(cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidDataException(
                $"The routing outbox could not be loaded for artifact reconciliation ({load.Status}).");
        }
        var current = load.Document;
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = current.Plans.SingleOrDefault(candidate => candidate.PlanId == planId) ??
                       throw new InvalidDataException(
                           "The frozen routing plan disappeared before artifact reconciliation.");
            if (!plan.SourceClipId.Equals(journal.Clip.ClipId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The capture journal does not belong to the frozen routing plan.");
            }

            var updateUtc = RoutingValidation.Utc(_utcNow());
            if (updateUtc < current.UpdatedUtc) updateUtc = current.UpdatedUtc;
            var candidate = RoutingOutboxModel.ReconcilePlanArtifactAvailability(
                current,
                planId,
                readyOutputs,
                failedOutputs,
                updateUtc);
            if (ReferenceEquals(candidate, current)) return;
            if (!authority.SameAuthority(_featureGate.Inspect())) return;
            try
            {
                _ = _captureLibraryPermit.RequireCurrent(
                    "Routing outbox artifact reconciliation");
                _ = await _outboxStore.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken,
                        () => _captureLibraryPermit.RequireCurrent(
                            "Routing outbox artifact-reconciliation commit"))
                    .ConfigureAwait(false);
                return;
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                var reloaded = _outboxStore.Load(cancellationToken);
                if (!reloaded.LoadedFromDisk || reloaded.Document is null)
                {
                    throw new InvalidDataException(
                        $"The routing outbox could not be reloaded after a concurrent artifact save ({reloaded.Status}).");
                }
                current = reloaded.Document;
            }
        }

        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while artifact availability was reconciled.");
    }

    private static bool IsRequestedRendition(
        CaptureJournalDocument journal,
        RoutingOutputKind kind) => kind switch
        {
            RoutingOutputKind.Landscape => journal.Clip.RequestedRenditions.Contains(
                CaptureJournalArtifactKinds.Landscape, StringComparer.Ordinal),
            RoutingOutputKind.Portrait => journal.Clip.RequestedRenditions.Contains(
                CaptureJournalArtifactKinds.Portrait, StringComparer.Ordinal),
            _ => false
        };

    private async Task<CaptureJournalReconciliationItem> ValidateSourceEventAsync(
        RoutingRuntimeSourceEvent sourceEvent,
        CancellationToken cancellationToken)
    {
        var item = sourceEvent.JournalItem ??
                   throw new InvalidDataException("A routing source event has no capture journal item.");
        if (item.Status != CaptureJournalLoadStatus.Loaded || item.Document is null)
        {
            throw new InvalidDataException("A routing source event must carry a loaded capture journal.");
        }
        CaptureJournalModel.ValidateDocumentShape(item.Document);
        if (!item.ClipId.Equals(item.Document.Clip.ClipId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The routing source event does not match its capture journal identity.");
        }
        if (item.ArtifactStatuses is null || !item.MediaValidated ||
            item.Document.State is not CaptureJournalState.OriginalCommitted and
                not CaptureJournalState.CameraPending and
                not CaptureJournalState.RenditionsReady and
                not CaptureJournalState.RenditionsFailed)
        {
            throw new InvalidDataException(
                "The routing source media is not fully validated or is not ready for planning.");
        }

        var expectedKinds = new HashSet<string>(StringComparer.Ordinal) { "original" };
        foreach (var artifact in item.Document.Artifacts) expectedKinds.Add(artifact.Kind);
        if (!expectedKinds.SetEquals(item.ArtifactStatuses.Keys) ||
            item.ArtifactStatuses.Values.Any(status =>
                status != CaptureJournalArtifactValidationStatus.Valid))
        {
            throw new InvalidDataException(
                "The routing source event has incomplete or stale artifact readiness evidence.");
        }

        foreach (var artifact in new[] { item.Document.Clip.Original }
                     .Concat(item.Document.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await CaptureJournalStore.ValidateArtifactAsync(
                    _libraryRoot,
                    artifact,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Status != CaptureJournalArtifactValidationStatus.Valid)
            {
                throw new InvalidDataException(
                    $"The routing source {artifact.Kind} artifact no longer matches its journal fingerprint.");
            }
        }
        return item;
    }

    private static IReadOnlyDictionary<RoutingOutputKind, RoutingRuntimeOutput> BuildOutputInventory(
        CaptureJournalDocument journal)
    {
        var outputs = new Dictionary<RoutingOutputKind, RoutingRuntimeOutput>
        {
            [RoutingOutputKind.Original] = new RoutingRuntimeOutput(
                RoutingEvaluator.CreateLogicalOutputReference(
                    journal.Clip.ClipId,
                    journal.Clip.Original.Fingerprint.Sha256,
                    RoutingOutputKind.Original),
                RoutingRuntimeArtifactReadiness.Ready,
                journal.Clip.Original)
        };
        AddDerivedOutput(
            outputs,
            journal,
            RoutingOutputKind.Landscape,
            CaptureJournalArtifactKinds.Landscape);
        AddDerivedOutput(
            outputs,
            journal,
            RoutingOutputKind.Portrait,
            CaptureJournalArtifactKinds.Portrait);
        return outputs;
    }

    private static void AddDerivedOutput(
        IDictionary<RoutingOutputKind, RoutingRuntimeOutput> outputs,
        CaptureJournalDocument journal,
        RoutingOutputKind outputKind,
        string artifactKind)
    {
        var artifact = journal.Artifacts.SingleOrDefault(candidate =>
            candidate.Kind.Equals(artifactKind, StringComparison.Ordinal));
        if (artifact is not null)
        {
            outputs[outputKind] = new RoutingRuntimeOutput(
                RoutingEvaluator.CreateLogicalOutputReference(
                    journal.Clip.ClipId,
                    journal.Clip.Original.Fingerprint.Sha256,
                    outputKind),
                RoutingRuntimeArtifactReadiness.Ready,
                artifact);
            return;
        }

        var requested = journal.Clip.RequestedRenditions.Contains(
            artifactKind,
            StringComparer.Ordinal);
        var readiness = requested && journal.State != CaptureJournalState.RenditionsFailed
            ? RoutingRuntimeArtifactReadiness.Pending
            : RoutingRuntimeArtifactReadiness.Unavailable;
        outputs[outputKind] = new RoutingRuntimeOutput(
            RoutingEvaluator.CreateLogicalOutputReference(
                journal.Clip.ClipId,
                journal.Clip.Original.Fingerprint.Sha256,
                outputKind),
            readiness,
            null);
    }

    private static void ValidateProposal(
        RoutingRuntimePlanningContext context,
        RoutingPlanProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var facts = RoutingEvaluatorRuntimePlanner.CreateFacts(context);
        var exactMatches = RoutingEvaluator.GetOrderedMatchedRouteIds(
            context.RoutingSnapshot, facts);
        if (proposal.PlanId == Guid.Empty ||
            !proposal.SourceClipId.Equals(
                context.CaptureJournal.Clip.ClipId, StringComparison.Ordinal) ||
            proposal.RoutingGeneration != context.RoutingSnapshot.Generation ||
            proposal.Deliveries is null || proposal.MatchedRouteIds is null ||
            proposal.ImmediateMissingResolutions is null ||
            proposal.LatentDuplicateAuthorizations is null ||
            !proposal.MatchedRouteIds.SequenceEqual(exactMatches))
        {
            throw new InvalidDataException(
                "The routing planner changed its frozen identity or exact route matches.");
        }

        var expected = RoutingEvaluator.CreatePlan(
            context.RoutingSnapshot,
            facts,
            proposal.PlanId,
            proposal.LatentDuplicateAuthorizations,
            context.PlannedUtc);
        if (!proposal.Deliveries.SequenceEqual(expected.Deliveries) ||
            proposal.FileDisposition != expected.FileDisposition ||
            !proposal.ImmediateMissingResolutions.SequenceEqual(
                expected.ImmediateMissingResolutions) ||
            !proposal.LatentDuplicateAuthorizations.SequenceEqual(
                expected.LatentDuplicateAuthorizations) ||
            proposal.RequiresAtomicResolvedAppend != expected.RequiresAtomicResolvedAppend)
        {
            throw new InvalidDataException(
                "The routing planner omitted or changed an exact frozen action decision.");
        }
    }

    private static bool TryFindExistingPlan(
        RoutingOutboxDocument outbox,
        string sourceClipId,
        out Guid planId)
    {
        var planIds = outbox.Plans
            .Where(candidate => candidate.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal))
            .Select(candidate => candidate.PlanId)
            .Distinct()
            .ToArray();
        if (planIds.Length > 1)
        {
            throw new InvalidDataException(
                "The routing outbox contains more than one frozen plan for a source clip.");
        }
        planId = planIds.SingleOrDefault();
        return planIds.Length == 1;
    }

    private bool TryFindArchivedPlan(
        string sourceClipId,
        CancellationToken cancellationToken,
        out Guid planId)
    {
        var archived = _outboxStore.ArchiveStore.LoadBySource(sourceClipId, cancellationToken);
        if (archived.Status == RoutingDocumentLoadStatus.Missing)
        {
            planId = Guid.Empty;
            return false;
        }
        if (!archived.LoadedFromDisk || archived.Document is null)
        {
            throw new InvalidDataException(
                $"The routing plan archive cannot be trusted ({archived.Status}).");
        }
        planId = archived.Document.Plan.PlanId;
        return true;
    }

    private void ValidateArchiveOverlap(
        RoutingOutboxDocument current,
        string sourceClipId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        var archived = _outboxStore.ArchiveStore.LoadBySource(sourceClipId, cancellationToken);
        if (archived.Status == RoutingDocumentLoadStatus.Missing) return;
        if (!archived.LoadedFromDisk || archived.Document is null)
            throw new InvalidDataException(
                $"The overlapping routing plan archive cannot be trusted ({archived.Status}).");
        var expected = RoutingArchiveModel.Create(current, planId);
        if (archived.Document != expected)
            throw new InvalidDataException(
                "The hot and archived routing decisions for a source clip do not match.");
    }
}
