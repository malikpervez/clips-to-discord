namespace ClipsToDiscord;

internal enum RoutingPlanCommitStatus
{
    Disabled,
    Committed,
    AlreadyCommitted,
    AlreadyArchived,
    CapacityNeedsAttention
}

internal sealed record RoutingPlanCommitResult(
    RoutingPlanCommitStatus Status,
    string SourceClipId,
    Guid PlanId,
    long? OutboxGeneration);

/// <summary>
/// Source-neutral exact-plan append seam. Ingress owns validation and freezes the complete proposal
/// before calling this class; this class only proves authority, idempotence, archive overlap, and
/// the one-generation outbox CAS. It never re-evaluates routes.
/// </summary>
internal sealed class RoutingPlanCommitter
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingOutboxStore _outboxStore;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly RoutingCaptureLibraryPermit? _captureLibraryPermit;

    internal RoutingPlanCommitter(
        RoutingOutboxStore outboxStore,
        RoutingRuntimeFeatureGate featureGate,
        RoutingCaptureLibraryPermit? captureLibraryPermit = null)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _captureLibraryPermit = captureLibraryPermit;
    }

    internal async Task<RoutingPlanCommitResult> CommitExactAsync(
        RoutingPlanProposal proposal,
        DateTimeOffset planCreatedUtc,
        RoutingRuntimeGateInspection expectedPermit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(expectedPermit);
        var createdUtc = RoutingValidation.Utc(planCreatedUtc);
        ValidateFrozenProposal(proposal, createdUtc);
        if (!expectedPermit.Enabled || !expectedPermit.SamePermit(_featureGate.Inspect()))
        {
            return Result(RoutingPlanCommitStatus.Disabled, proposal, null);
        }

        RequireCaptureLibraryPermit("watched-source outbox planning admission");
        var admission = await _outboxStore.PrepareForPlanningAsync(
                createdUtc,
                cancellationToken,
                () => RequireCaptureLibraryPermit(
                    "watched-source planning-admission commit"))
            .ConfigureAwait(false);
        var current = admission.Document;
        var existingStatus = RequireExistingExactIfPresent(
            current,
            proposal,
            createdUtc,
            cancellationToken);
        if (existingStatus is { } existing)
        {
            return Result(existing, proposal, current.Generation);
        }
        if (!admission.CanAcceptNewPlan)
        {
            return Result(
                RoutingPlanCommitStatus.CapacityNeedsAttention,
                proposal,
                current.Generation);
        }

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expectedPermit.SamePermit(_featureGate.Inspect()))
            {
                return Result(RoutingPlanCommitStatus.Disabled, proposal, current.Generation);
            }
            existingStatus = RequireExistingExactIfPresent(
                current,
                proposal,
                createdUtc,
                cancellationToken);
            if (existingStatus is { } concurrentExisting)
            {
                return Result(concurrentExisting, proposal, current.Generation);
            }

            var appendUtc = current.UpdatedUtc > createdUtc
                ? current.UpdatedUtc
                : createdUtc;
            var candidate = RoutingOutboxModel.AppendEvaluatedPlan(
                current,
                proposal,
                createdUtc,
                appendUtc);
            if (_outboxStore.MeasureSerializedBytes(candidate) >
                _outboxStore.PlanningAdmissionLimitBytes)
            {
                return Result(
                    RoutingPlanCommitStatus.CapacityNeedsAttention,
                    proposal,
                    current.Generation);
            }
            try
            {
                if (!expectedPermit.SamePermit(_featureGate.Inspect()))
                {
                    return Result(RoutingPlanCommitStatus.Disabled, proposal, current.Generation);
                }
                RequireCaptureLibraryPermit("watched-source outbox plan commit");
                var saved = await _outboxStore.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken,
                        () => RequireCaptureLibraryPermit(
                            "watched-source outbox plan commit"))
                    .ConfigureAwait(false);
                return Result(
                    RoutingPlanCommitStatus.Committed,
                    proposal,
                    saved.Generation);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                var reloaded = _outboxStore.Load(cancellationToken);
                if (!reloaded.LoadedFromDisk || reloaded.Document is null)
                {
                    throw new InvalidDataException(
                        $"The routing outbox could not be reloaded after a concurrent exact-plan save ({reloaded.Status}).");
                }
                current = reloaded.Document;
            }
        }
        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while an exact frozen plan was appended.");
    }

    private void RequireCaptureLibraryPermit(string operation)
    {
        _ = _captureLibraryPermit?.RequireCurrent(operation);
    }

    private RoutingPlanCommitStatus? RequireExistingExactIfPresent(
        RoutingOutboxDocument current,
        RoutingPlanProposal proposal,
        DateTimeOffset planCreatedUtc,
        CancellationToken cancellationToken)
    {
        var hotPlans = current.Plans.Where(plan => plan.SourceClipId.Equals(
                proposal.SourceClipId, StringComparison.Ordinal))
            .ToArray();
        if (hotPlans.Length > 1)
        {
            throw new InvalidDataException(
                "The routing outbox contains multiple decisions for one frozen source.");
        }
        if (hotPlans.Length == 1)
        {
            RequirePlanIdentity(
                hotPlans[0],
                current.Deliveries.Where(item => item.PlanId == hotPlans[0].PlanId).ToArray(),
                current.FileDispositions.Where(item => item.PlanId == hotPlans[0].PlanId).ToArray(),
                proposal,
                planCreatedUtc);
            var archived = _outboxStore.ArchiveStore.LoadBySource(
                proposal.SourceClipId,
                cancellationToken);
            if (archived.Status != RoutingDocumentLoadStatus.Missing)
            {
                if (!archived.LoadedFromDisk || archived.Document is null)
                {
                    throw new InvalidDataException(
                        $"The overlapping routing archive cannot be trusted ({archived.Status}).");
                }
                RequirePlanIdentity(
                    archived.Document.Plan,
                    archived.Document.Deliveries,
                    archived.Document.FileDispositions,
                    proposal,
                    planCreatedUtc);
            }
            return RoutingPlanCommitStatus.AlreadyCommitted;
        }

        var archive = _outboxStore.ArchiveStore.LoadBySource(
            proposal.SourceClipId,
            cancellationToken);
        if (archive.Status == RoutingDocumentLoadStatus.Missing) return null;
        if (!archive.LoadedFromDisk || archive.Document is null)
        {
            throw new InvalidDataException(
                $"The routing plan archive cannot be trusted ({archive.Status}).");
        }
        RequirePlanIdentity(
            archive.Document.Plan,
            archive.Document.Deliveries,
            archive.Document.FileDispositions,
            proposal,
            planCreatedUtc);
        return RoutingPlanCommitStatus.AlreadyArchived;
    }

    private static void RequirePlanIdentity(
        RoutingPlanDecision actualPlan,
        IReadOnlyList<PlannedDelivery> actualDeliveries,
        IReadOnlyList<PlannedFileDisposition> actualDispositions,
        RoutingPlanProposal expected,
        DateTimeOffset planCreatedUtc)
    {
        var expectedPlan = new RoutingPlanDecision(
            expected.PlanId,
            expected.SourceClipId,
            expected.RoutingGeneration,
            expected.MatchedRouteIds.ToArray(),
            expected.ImmediateMissingResolutions.ToArray(),
            expected.LatentDuplicateAuthorizations.ToArray(),
            planCreatedUtc,
            expected.LocalOnlyOverride);
        RoutingValidation.Require(actualPlan == expectedPlan,
            "The durable routing decision conflicts with the watched source journal.");
        RoutingValidation.Require(actualDeliveries.Count == expected.Deliveries.Count &&
                                  actualDispositions.Count ==
                                  (expected.FileDisposition is null ? 0 : 1),
            "The durable routing plan member set conflicts with the watched source journal.");
        foreach (var expectedDelivery in expected.Deliveries)
        {
            var actual = actualDeliveries.SingleOrDefault(item =>
                item.DeliveryId == expectedDelivery.DeliveryId) ??
                         throw new InvalidDataException(
                             "A durable routing delivery is missing from its frozen plan.");
            RoutingValidation.Require(
                actual.PlanId == expectedDelivery.PlanId &&
                actual.SourceClipId == expectedDelivery.SourceClipId &&
                actual.Route == expectedDelivery.Route &&
                actual.Destination == expectedDelivery.Destination &&
                actual.ConnectionId == expectedDelivery.ConnectionId &&
                actual.RequestedOutput == expectedDelivery.RequestedOutput &&
                actual.OriginalOutput == expectedDelivery.OriginalOutput &&
                actual.OnMissingOutput == expectedDelivery.OnMissingOutput &&
                actual.Mode == expectedDelivery.Mode &&
                actual.Settings == expectedDelivery.Settings &&
                actual.CreatedUtc == expectedDelivery.CreatedUtc,
                "A durable routing delivery changed immutable frozen-plan data.");
        }
        if (expected.FileDisposition is { } expectedDisposition)
        {
            var actual = actualDispositions.SingleOrDefault(item =>
                item.DispositionId == expectedDisposition.DispositionId) ??
                         throw new InvalidDataException(
                             "A durable file disposition is missing from its frozen plan.");
            RoutingValidation.Require(
                actual.PlanId == expectedDisposition.PlanId &&
                actual.SourceClipId == expectedDisposition.SourceClipId &&
                actual.SourceContentSha256 == expectedDisposition.SourceContentSha256 &&
                actual.Route == expectedDisposition.Route &&
                actual.LibraryArea == expectedDisposition.LibraryArea &&
                actual.PrerequisiteDeliveryIds.SequenceEqual(
                    expectedDisposition.PrerequisiteDeliveryIds) &&
                actual.CreatedUtc == expectedDisposition.CreatedUtc,
                "A durable file disposition changed immutable frozen-plan data.");
        }
        RoutingValidation.Require(actualPlan.LocalOnlyOverride == expected.LocalOnlyOverride,
            "The durable routing decision changed its frozen local-only override state.");
    }

    private static void ValidateFrozenProposal(
        RoutingPlanProposal proposal,
        DateTimeOffset planCreatedUtc)
    {
        var empty = RoutingOutboxModel.CreateEmpty(planCreatedUtc);
        _ = RoutingOutboxModel.AppendEvaluatedPlan(empty, proposal, planCreatedUtc);
    }

    private static RoutingPlanCommitResult Result(
        RoutingPlanCommitStatus status,
        RoutingPlanProposal proposal,
        long? generation) => new(
        status,
        proposal.SourceClipId,
        proposal.PlanId,
        generation);
}
