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
    MigrationMarkerMissing,
    MigrationMarkerNotCommitted,
    LegacyQueuesPending,
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
    private readonly WatchState? _legacyState;

    private RoutingRuntimeFeatureGate(
        bool requestedEnabled,
        LegacyRoutingMigrationMarkerStore? migrationMarkers,
        WatchState? legacyState)
    {
        _requestedEnabled = requestedEnabled;
        _migrationMarkers = migrationMarkers;
        _legacyState = legacyState;
    }

    internal static RoutingRuntimeFeatureGate Disabled { get; } = new(
        requestedEnabled: false,
        migrationMarkers: null,
        legacyState: null);

    internal RoutingRuntimeGateState State => Inspect().State;
    internal int PendingLegacyMoves => Inspect().PendingLegacyMoves;
    internal int PendingLegacyLocalOnlyMoves => Inspect().PendingLegacyLocalOnlyMoves;
    internal int PendingLegacyEditedUploads => Inspect().PendingLegacyEditedUploads;
    internal int IgnoredLegacyFileKeys => Inspect().IgnoredLegacyFileKeys;
    internal bool Enabled => State == RoutingRuntimeGateState.Enabled;

    internal static RoutingRuntimeFeatureGate Evaluate(
        bool requestedEnabled,
        LegacyRoutingMigrationMarkerStore migrationMarkers,
        WatchState legacyState)
    {
        ArgumentNullException.ThrowIfNull(migrationMarkers);
        ArgumentNullException.ThrowIfNull(legacyState);
        return new RoutingRuntimeFeatureGate(
            requestedEnabled,
            migrationMarkers,
            legacyState);
    }

    internal RoutingRuntimeGateInspection Inspect()
    {
        if (!_requestedEnabled)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.DisabledByDefault,
                0,
                0,
                0,
                0);
        }

        var legacyState = _legacyState;
        if (legacyState is null)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.LegacyQueuesPending,
                0,
                0,
                0,
                0);
        }

        var pendingMoves = legacyState.PendingMoves?.Count ?? -1;
        var pendingLocalOnlyMoves = legacyState.PendingLocalOnlyMoves?.Count ?? -1;
        var pendingEditedUploads = legacyState.PendingEditedUploads?.Count ?? -1;
        var ignoredFileKeys = legacyState.IgnoredFileKeys?.Count ?? -1;
        if (pendingMoves != 0 || pendingLocalOnlyMoves != 0 || pendingEditedUploads != 0 ||
            ignoredFileKeys != 0)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.LegacyQueuesPending,
                Math.Max(0, pendingMoves),
                Math.Max(0, pendingLocalOnlyMoves),
                Math.Max(0, pendingEditedUploads),
                Math.Max(0, ignoredFileKeys));
        }

        var markerStore = _migrationMarkers;
        if (markerStore is null)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.MigrationMarkerMissing,
                0,
                0,
                0,
                0);
        }

        var marker = markerStore.Load();
        if (marker.Status == RoutingDocumentLoadStatus.Missing)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.MigrationMarkerMissing,
                0,
                0,
                0,
                0);
        }
        if (!marker.LoadedFromDisk || marker.Document is null ||
            marker.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed)
        {
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.MigrationMarkerNotCommitted,
                0,
                0,
                0,
                0);
        }

        return new RoutingRuntimeGateInspection(
            RoutingRuntimeGateState.Enabled,
            0,
            0,
            0,
            0);
    }

}

internal sealed record RoutingRuntimeGateInspection(
    RoutingRuntimeGateState State,
    int PendingLegacyMoves,
    int PendingLegacyLocalOnlyMoves,
    int PendingLegacyEditedUploads,
    int IgnoredLegacyFileKeys);

internal enum RoutingRuntimePlanStatus
{
    Disabled,
    IgnoredNonSourceEvent,
    AlreadyPlanned,
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
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingRuntimeBridge(
        string libraryRoot,
        RoutingSnapshotStore snapshotStore,
        RoutingOutboxStore outboxStore,
        IRoutingRuntimePlanner planner,
        RoutingRuntimeFeatureGate? featureGate = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.GetFullPath(libraryRoot);
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _featureGate = featureGate ?? RoutingRuntimeFeatureGate.Disabled;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal RoutingRuntimeFeatureGate FeatureGate => _featureGate;

    public async ValueTask ReconcileAsync(
        CaptureJournalReconciliationItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanPlanDeliveries) return;
        _ = await PlanAsync(
                new RoutingRuntimeSourceEvent(
                    RoutingRuntimeSourceEventKind.CaptureJournalReconciled,
                    item),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RoutingRuntimePlanResult> PlanAsync(
        RoutingRuntimeSourceEvent sourceEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent);
        if (!_featureGate.Enabled)
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.Disabled,
                null,
                null,
                null);
        }
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
        var current = await _outboxStore.LoadOrCreateAsync(
                _utcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (TryFindExistingPlan(current, sourceClipId, out var existingPlan))
        {
            return new RoutingRuntimePlanResult(
                RoutingRuntimePlanStatus.AlreadyPlanned,
                sourceClipId,
                existingPlan,
                current.Generation);
        }

        var snapshot = await _snapshotStore.LoadOrCreateAsync(
                _utcNow(),
                cancellationToken)
            .ConfigureAwait(false);
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
            if (!_featureGate.Enabled)
            {
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.Disabled,
                    sourceClipId,
                    null,
                    current.Generation);
            }
            if (TryFindExistingPlan(current, sourceClipId, out existingPlan))
            {
                return new RoutingRuntimePlanResult(
                    RoutingRuntimePlanStatus.AlreadyPlanned,
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
            try
            {
                if (!_featureGate.Enabled)
                {
                    return new RoutingRuntimePlanResult(
                        RoutingRuntimePlanStatus.Disabled,
                        sourceClipId,
                        null,
                        current.Generation);
                }
                var saved = await _outboxStore.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken)
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
}
