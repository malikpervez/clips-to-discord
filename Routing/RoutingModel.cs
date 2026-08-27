using System.Globalization;

namespace ClipsToDiscord;

internal static class RoutingSnapshotModel
{
    internal const int MaximumRoutes = 256;
    internal const int MaximumConditionsPerRoute = 16;
    internal const int MaximumActionsPerRoute = 16;

    internal static RoutingSnapshotDocument CreateEmpty(DateTimeOffset? now = null)
    {
        var utcNow = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        return new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [],
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
    }

    internal static RoutingSnapshotDocument ReplaceRoutes(
        RoutingSnapshotDocument current,
        IReadOnlyList<RoutingRoute> routes,
        DateTimeOffset now)
    {
        Validate(current);
        ArgumentNullException.ThrowIfNull(routes);
        var next = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Routes = routes.ToArray(),
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        Validate(next);
        ValidateSuccessor(current, next);
        return next;
    }

    internal static void Validate(RoutingSnapshotDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingSnapshotStore.CurrentSchemaVersion,
            "The routing snapshot schema is unsupported.");
        RoutingValidation.Require(document.Generation > 0,
            "The routing generation is invalid.");
        var routes = document.Routes ??
            throw new InvalidDataException("The routing route collection is missing.");
        RoutingValidation.Require(routes.Count <= MaximumRoutes,
            "The routing snapshot contains too many routes.");
        RoutingValidation.RequireUtc(document.CreatedUtc, "routing creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc, "routing update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The routing timestamps are inconsistent.");

        var ids = new HashSet<Guid>();
        var priorities = new HashSet<int>();
        var fallbackCount = 0;
        foreach (var route in routes)
        {
            if (route is null) throw new InvalidDataException("A routing entry is missing.");
            ValidateRoute(route);
            RoutingValidation.Require(ids.Add(route.RouteId), "Routing route ids must be unique.");
            RoutingValidation.Require(priorities.Add(route.Priority),
                "Routing priorities must be unique and deterministic.");
            if (route.Kind == RoutingRouteKind.Fallback) fallbackCount++;
        }
        RoutingValidation.Require(fallbackCount <= 1,
            "A routing snapshot can contain only one fallback route.");
    }

    internal static void ValidateSuccessor(
        RoutingSnapshotDocument current,
        RoutingSnapshotDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(candidate.Generation == checked(current.Generation + 1),
            "The routing snapshot does not advance one generation.");
        RoutingValidation.Require(candidate.CreatedUtc == current.CreatedUtc &&
                                  candidate.UpdatedUtc >= current.UpdatedUtc,
            "The routing snapshot changed immutable timestamps.");

        var currentById = current.Routes.ToDictionary(route => route.RouteId);
        foreach (var route in candidate.Routes)
        {
            if (!currentById.TryGetValue(route.RouteId, out var previous))
            {
                RoutingValidation.Require(route.Revision == 1,
                    "A new route must begin at revision one.");
                continue;
            }

            RoutingValidation.Require(route.CreatedUtc == previous.CreatedUtc,
                "A route cannot change its creation timestamp.");
            RoutingValidation.Require(route.Revision is var revision &&
                                      (revision == previous.Revision ||
                                       revision == checked(previous.Revision + 1)),
                "A route revision may advance by exactly one.");
            if (route.Revision == previous.Revision)
            {
                RoutingValidation.Require(route == previous,
                    "A route changed without advancing its revision.");
            }
            else
            {
                RoutingValidation.Require(route.ModifiedUtc >= previous.ModifiedUtc,
                    "A revised route moved its timestamp backwards.");
            }
        }
    }

    private static void ValidateRoute(RoutingRoute route)
    {
        RoutingValidation.Require(route.RouteId != Guid.Empty, "A route id is missing.");
        RoutingValidation.RequireText(route.Name, 1, 80, "route name");
        RoutingValidation.Require(Enum.IsDefined(route.Source) && Enum.IsDefined(route.Kind) &&
                                  Enum.IsDefined(route.Trigger),
            "A route source, kind, or trigger is unsupported.");
        var prepare = route.Prepare ??
            throw new InvalidDataException("A route prepare contract is missing.");
        RoutingValidation.Require(Enum.IsDefined(prepare.DefaultOnMissing),
            "A route default missing-output behavior is unsupported.");
        RoutingValidation.Require(route.Priority >= 0, "A route priority is invalid.");
        RoutingValidation.Require(route.Revision > 0, "A route revision is invalid.");
        RoutingValidation.RequireUtc(route.CreatedUtc, "route creation timestamp");
        RoutingValidation.RequireUtc(route.ModifiedUtc, "route modification timestamp");
        RoutingValidation.Require(route.ModifiedUtc >= route.CreatedUtc,
            "The route timestamps are inconsistent.");
        var conditions = route.Conditions ??
            throw new InvalidDataException("A route condition collection is missing.");
        var actions = route.Actions ??
            throw new InvalidDataException("A route action collection is missing.");
        RoutingValidation.Require(conditions.Count <= MaximumConditionsPerRoute,
            "A route contains too many conditions.");
        RoutingValidation.Require(actions.Count is > 0 and <= MaximumActionsPerRoute,
            "A route must contain at least one action.");
        RoutingValidation.Require(actions.Any(action => action is { Enabled: true }),
            "A route must contain at least one enabled action.");
        if (route.Kind == RoutingRouteKind.Fallback)
        {
            RoutingValidation.Require(route.Trigger == RoutingTriggerKind.AnyNewSourceClip &&
                                      conditions.Count == 0,
                "A fallback route must use Any new source clip without conditions.");
        }
        else
        {
            RoutingValidation.Require(route.Trigger != RoutingTriggerKind.AnyNewSourceClip ||
                                      conditions.Count > 0,
                "A specific Any new source clip route needs at least one condition.");
        }

        var conditionIds = new HashSet<Guid>();
        foreach (var condition in conditions)
        {
            if (condition is null) throw new InvalidDataException("A route condition is missing.");
            ValidateCondition(condition);
            RoutingValidation.Require(conditionIds.Add(condition.ConditionId),
                "Condition ids must be unique within a route.");
        }

        var actionIds = new HashSet<Guid>();
        var terminalCount = 0;
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (action is null) throw new InvalidDataException("A route action is missing.");
            ValidateAction(action);
            if (action.Kind == RoutingActionKind.Deliver && action.OutputRef is { } output)
            {
                RoutingValidation.Require(
                    output == RoutingOutputKind.Original ||
                    output == RoutingOutputKind.Landscape && prepare.Landscape ||
                    output == RoutingOutputKind.Portrait && prepare.Portrait,
                    "A delivery action references an output not enabled by the route prepare contract.");
            }
            RoutingValidation.Require(actionIds.Add(action.ActionId),
                "Action ids must be unique within a route.");
            if (!action.IsTerminalSourceDisposition) continue;
            terminalCount++;
            RoutingValidation.Require(index == actions.Count - 1,
                "File into Library must be the final route action.");
        }
        RoutingValidation.Require(terminalCount <= 1,
            "A route can contain only one terminal file disposition.");
    }

    private static void ValidateCondition(RoutingCondition condition)
    {
        RoutingValidation.Require(condition.ConditionId != Guid.Empty,
            "A condition id is missing.");
        RoutingValidation.RequireText(condition.Value, 1, 256, "condition value");
        switch (condition.Field)
        {
            case RoutingConditionField.Game:
                RoutingValidation.Require(
                    condition.Operator is RoutingConditionOperator.Equals or
                        RoutingConditionOperator.DoesNotEqual or
                        RoutingConditionOperator.Contains,
                    "A text condition uses an unsupported operator.");
                break;
            case RoutingConditionField.ClipSource:
                RoutingValidation.Require(
                    (condition.Operator is RoutingConditionOperator.Equals or
                        RoutingConditionOperator.DoesNotEqual) &&
                    RoutingValidation.IsNamedEnumValue<RoutingClipSource>(condition.Value),
                    "A clip-source condition is invalid.");
                break;
            case RoutingConditionField.CaptureType:
                RoutingValidation.Require(
                    (condition.Operator is RoutingConditionOperator.Equals or
                        RoutingConditionOperator.DoesNotEqual) &&
                    RoutingValidation.IsNamedEnumValue<RoutingCaptureType>(condition.Value),
                    "A capture-type condition is invalid.");
                break;
            case RoutingConditionField.Duration:
                RoutingValidation.Require(
                    (condition.Operator is RoutingConditionOperator.GreaterThanOrEqual or
                        RoutingConditionOperator.LessThanOrEqual) &&
                    long.TryParse(condition.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var duration) &&
                    duration >= 0,
                    "A duration condition is invalid.");
                break;
            case RoutingConditionField.ReactionCamera:
                RoutingValidation.Require(
                    (condition.Operator is RoutingConditionOperator.Equals or
                        RoutingConditionOperator.DoesNotEqual) &&
                    bool.TryParse(condition.Value, out _),
                    "A reaction-camera condition is invalid.");
                break;
            default:
                throw new InvalidDataException("A route condition kind is unsupported.");
        }
    }

    private static void ValidateAction(RoutingAction action)
    {
        RoutingValidation.Require(action.ActionId != Guid.Empty, "An action id is missing.");
        RoutingValidation.Require(Enum.IsDefined(action.Kind) && Enum.IsDefined(action.Mode),
            "An action kind or delivery mode is unsupported.");
        if (action.Kind == RoutingActionKind.Deliver)
        {
            RoutingValidation.Require(action.Destination is not null,
                "A delivery destination is missing.");
            RoutingValidation.Require(action.Destination is { } destination &&
                                      Enum.IsDefined(destination),
                "A delivery destination is unsupported.");
            RoutingValidation.RequireOpaqueId(action.ConnectionId, 128, "connection id");
            RoutingValidation.Require(action.LibraryArea is null,
                "A delivery cannot carry a library disposition.");
            RoutingValidation.Require(action.OutputRef is { } outputRef &&
                                      Enum.IsDefined(outputRef) &&
                                      action.OnMissingOutput is { } onMissingOutput &&
                                      Enum.IsDefined(onMissingOutput),
                "Delivery output preparation is unsupported.");
            RoutingValidation.Require(action.DeliverySettings is not null,
                "Resolved delivery settings are missing.");
            RoutingValidation.ValidateDeliverySettings(action.DeliverySettings!);
            return;
        }

        RoutingValidation.Require(action.Kind == RoutingActionKind.FileIntoLibrary,
            "A route action kind is unsupported.");
        RoutingValidation.Require(action.Destination is null && action.ConnectionId is null,
            "File into Library cannot carry an external destination or connection.");
        RoutingValidation.Require(action.OutputRef is null && action.OnMissingOutput is null,
            "File into Library cannot carry an output reference or missing-output behavior.");
        RoutingValidation.Require(action.Mode == RoutingDeliveryMode.Automatic,
            "File into Library is always automatic.");
        RoutingValidation.Require(action.LibraryArea is not null,
            "File into Library requires a library area.");
        RoutingValidation.Require(action.LibraryArea is { } libraryArea &&
                                  Enum.IsDefined(libraryArea),
            "A library area is unsupported.");
        RoutingValidation.Require(action.DeliverySettings is null,
            "File into Library cannot carry delivery metadata.");
    }
}

internal static class RoutingOutboxModel
{
    internal const int MaximumPlans = 10_000;
    internal const int MaximumDeliveries = 10_000;
    internal const int MaximumFileDispositions = 10_000;

    internal static RoutingOutboxDocument CreateEmpty(DateTimeOffset? now = null)
    {
        var utcNow = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        return new RoutingOutboxDocument(
            RoutingOutboxStore.CurrentSchemaVersion,
            Generation: 1,
            Plans: [],
            Deliveries: [],
            FileDispositions: [],
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
    }

    internal static PlannedDelivery CreateDelivery(
        Guid deliveryId,
        Guid planId,
        string sourceClipId,
        RoutingRouteSnapshotReference route,
        RoutingDestinationKind destination,
        string connectionId,
        RoutingOutputReference output,
        RoutingOutputReference originalOutput,
        RoutingMissingOutputBehavior onMissingOutput,
        RoutingDeliveryMode mode,
        RoutingDeliverySettings settings,
        bool artifactReady,
        IntentionalDuplicateProvenance? intentionalDuplicate,
        DateTimeOffset now)
    {
        var utcNow = RoutingValidation.Utc(now);
        var state = !artifactReady
            ? PlannedDeliveryState.WaitingForArtifact
            : mode == RoutingDeliveryMode.Approval
                ? PlannedDeliveryState.WaitingForApproval
                : PlannedDeliveryState.Ready;
        var result = new PlannedDelivery(
            deliveryId,
            planId,
            sourceClipId,
            route,
            destination,
            connectionId,
            RequestedOutput: output,
            output,
            originalOutput,
            onMissingOutput,
            RoutingMissingArtifactOutcome.RequestedOutput,
            ArtifactErrorCode: null,
            mode,
            settings,
            state,
            Attempts: 0,
            CurrentAttemptId: null,
            ProviderResumeReference: null,
            RemoteReceiptReference: null,
            ErrorCode: null,
            ApprovedUtc: null,
            AttemptStartedUtc: null,
            CompletedUtc: null,
            DuplicateRiskAcceptedUtc: null,
            intentionalDuplicate,
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
        ValidateDelivery(result);
        ValidateInitialDelivery(result);
        return result;
    }

    internal static PlannedFileDisposition CreateFileDisposition(
        Guid dispositionId,
        Guid planId,
        string sourceClipId,
        string sourceContentSha256,
        RoutingRouteSnapshotReference route,
        RoutingLibraryArea libraryArea,
        IReadOnlyList<Guid> prerequisiteDeliveryIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prerequisiteDeliveryIds);
        var utcNow = RoutingValidation.Utc(now);
        var result = new PlannedFileDisposition(
            dispositionId,
            planId,
            sourceClipId,
            sourceContentSha256,
            route,
            libraryArea,
            prerequisiteDeliveryIds.Distinct().ToArray(),
            prerequisiteDeliveryIds.Count == 0
                ? PlannedFileDispositionState.Ready
                : PlannedFileDispositionState.WaitingForDependencies,
            Attempts: 0,
            CurrentAttemptId: null,
            FinalLibraryItemReference: null,
            ErrorCode: null,
            AttemptStartedUtc: null,
            CompletedUtc: null,
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
        ValidateDisposition(result);
        ValidateInitialDisposition(result);
        return result;
    }

    internal static RoutingOutboxDocument AppendPlan(
        RoutingOutboxDocument current,
        Guid planId,
        IReadOnlyList<PlannedDelivery> deliveries,
        IReadOnlyList<PlannedFileDisposition> dispositions,
        DateTimeOffset now)
    {
        Validate(current);
        RoutingValidation.Require(planId != Guid.Empty, "A routing plan id is missing.");
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(dispositions);
        RoutingValidation.Require(deliveries.Count > 0 || dispositions.Count > 0,
            "A routing plan must contain work.");
        RoutingValidation.Require(current.Plans.All(item => item.PlanId != planId) &&
                                  current.Deliveries.All(item => item.PlanId != planId) &&
                                  current.FileDispositions.All(item => item.PlanId != planId),
            "A routing plan is append-only and can be planned only once.");
        foreach (var delivery in deliveries)
        {
            RoutingValidation.Require(delivery.PlanId == planId,
                "A delivery belongs to a different plan.");
            ValidateInitialDelivery(delivery);
        }
        foreach (var disposition in dispositions)
        {
            RoutingValidation.Require(disposition.PlanId == planId,
                "A file disposition belongs to a different plan.");
            ValidateInitialDisposition(disposition);
        }

        var sourceIds = deliveries.Select(item => item.SourceClipId)
            .Concat(dispositions.Select(item => item.SourceClipId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RoutingValidation.Require(sourceIds.Length == 1 &&
                                  deliveries.All(item =>
                                      item.Output.ClipId.Equals(sourceIds[0], StringComparison.Ordinal)),
            "One planning operation must bind one plan id, one source clip, and its own outputs.");
        RoutingValidation.Require(current.Plans.All(item =>
                                      !item.SourceClipId.Equals(sourceIds[0], StringComparison.Ordinal)) &&
                                  current.Deliveries.All(item =>
                                      !item.SourceClipId.Equals(sourceIds[0], StringComparison.Ordinal)) &&
                                  current.FileDispositions.All(item =>
                                      !item.SourceClipId.Equals(sourceIds[0], StringComparison.Ordinal)),
            "A source clip can be frozen into exactly one durable routing plan.");
        var routeReferences = deliveries.Select(item => item.Route)
            .Concat(dispositions.Select(item => item.Route))
            .ToArray();
        RoutingValidation.Require(routeReferences.Select(item => item.RoutingGeneration)
                                      .Distinct().Count() == 1,
            "Every member of a routing plan must use one frozen routing generation.");
        RoutingValidation.Require(routeReferences.Select(item => (item.RouteId, item.ActionId))
                                      .Distinct().Count() == routeReferences.Length,
            "A route action can contribute at most one member to a routing plan.");
        RoutingValidation.Require(dispositions.Count <= 1,
            "A plan/source can have only one terminal file disposition.");
        if (dispositions.Count == 1)
        {
            var expectedDependencies = deliveries.Select(item => item.DeliveryId).ToHashSet();
            RoutingValidation.Require(
                dispositions[0].PrerequisiteDeliveryIds.ToHashSet().SetEquals(expectedDependencies),
                "A terminal file disposition must depend on every sibling delivery.");
        }

        var utcNow = RoutingValidation.Utc(now);
        var planDecision = new RoutingPlanDecision(
            planId,
            sourceIds[0],
            routeReferences[0].RoutingGeneration,
            routeReferences.OrderBy(item => item.Priority).ThenBy(item => item.Order)
                .Select(item => item.RouteId).Distinct().ToArray(),
            [],
            deliveries.Select(item => item.IntentionalDuplicate)
                .Where(item => item is not null).Cast<IntentionalDuplicateProvenance>()
                .Distinct().ToArray(),
            utcNow);
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Plans = current.Plans.Concat([planDecision]).ToArray(),
            Deliveries = current.Deliveries.Concat(deliveries).ToArray(),
            FileDispositions = current.FileDispositions.Concat(dispositions).ToArray(),
            UpdatedUtc = utcNow
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    /// <summary>
    /// Appends one evaluator-produced plan in one durable generation. Unlike the ordinary
    /// AppendPlan seam, this accepts only the exact, explicitly-described results of applying a
    /// route's missing-output policy before the plan becomes visible. That prevents a crash from
    /// exposing a transient WaitingForArtifact item for an output Capture will never produce.
    /// </summary>
    internal static RoutingOutboxDocument AppendEvaluatedPlan(
        RoutingOutboxDocument current,
        RoutingPlanProposal proposal,
        DateTimeOffset now)
    {
        Validate(current);
        ArgumentNullException.ThrowIfNull(proposal);
        RoutingValidation.Require(proposal.PlanId != Guid.Empty,
            "An evaluated routing plan id is missing.");
        RoutingValidation.RequireOpaqueId(proposal.SourceClipId, 256,
            "evaluated source clip id");
        RoutingValidation.Require(proposal.RoutingGeneration > 0,
            "An evaluated routing generation is invalid.");
        var deliveries = proposal.Deliveries ??
            throw new InvalidDataException("Evaluated plan deliveries are missing.");
        var dispositions = proposal.FileDisposition is null
            ? Array.Empty<PlannedFileDisposition>()
            : new[] { proposal.FileDisposition };
        var resolutions = proposal.ImmediateMissingResolutions ??
            throw new InvalidDataException("Evaluated missing-output evidence is missing.");
        var matchedRouteIds = proposal.MatchedRouteIds ??
            throw new InvalidDataException("Evaluated matched-route evidence is missing.");
        var latentAuthorizations = proposal.LatentDuplicateAuthorizations ??
            throw new InvalidDataException("Evaluated duplicate authorization evidence is missing.");
        RoutingValidation.Require(matchedRouteIds.All(id => id != Guid.Empty) &&
                                  matchedRouteIds.Distinct().Count() == matchedRouteIds.Count,
            "Evaluated matched-route evidence is invalid.");
        RoutingValidation.Require(
            proposal.RequiresAtomicResolvedAppend == (resolutions.Count > 0),
            "The evaluated plan's atomic missing-output marker is inconsistent.");

        var resolutionByDelivery = new Dictionary<Guid, RoutingImmediateMissingResolution>();
        foreach (var resolution in resolutions)
        {
            if (resolution is null || resolution.DeliveryId == Guid.Empty ||
                !resolutionByDelivery.TryAdd(resolution.DeliveryId, resolution))
            {
                throw new InvalidDataException(
                    "Evaluated missing-output evidence is incomplete or duplicated.");
            }
        }
        foreach (var authorization in latentAuthorizations)
        {
            ValidateDuplicate(authorization);
            RoutingValidation.Require(
                authorization.DeliveryKey.OutputRef.ClipId.Equals(
                    proposal.SourceClipId, StringComparison.Ordinal) &&
                matchedRouteIds.Contains(authorization.FirstRouteId) &&
                matchedRouteIds.Contains(authorization.SecondRouteId),
                "Evaluated duplicate authorization is outside the frozen plan.");
        }
        RoutingValidation.Require(
            latentAuthorizations.Select(item => item.GroupId).Distinct().Count() ==
            latentAuthorizations.Count,
            "Evaluated duplicate authorization ids must be unique.");

        foreach (var delivery in deliveries)
        {
            RoutingValidation.Require(delivery.PlanId == proposal.PlanId &&
                                      delivery.SourceClipId.Equals(
                                          proposal.SourceClipId, StringComparison.Ordinal) &&
                                      delivery.Route.RoutingGeneration ==
                                          proposal.RoutingGeneration &&
                                      matchedRouteIds.Contains(delivery.Route.RouteId),
                "An evaluated delivery does not belong to its frozen plan.");
            if (resolutionByDelivery.TryGetValue(delivery.DeliveryId, out var resolution))
            {
                ValidateImmediateMissingResolution(delivery, resolution);
            }
            else
            {
                ValidateInitialDelivery(delivery);
            }
        }
        RoutingValidation.Require(
            resolutionByDelivery.Keys.All(id =>
                deliveries.Any(delivery => delivery.DeliveryId == id)),
            "Missing-output evidence names a delivery outside the evaluated plan.");

        foreach (var disposition in dispositions)
        {
            RoutingValidation.Require(disposition.PlanId == proposal.PlanId &&
                                      disposition.SourceClipId.Equals(
                                          proposal.SourceClipId, StringComparison.Ordinal) &&
                                      disposition.Route.RoutingGeneration ==
                                          proposal.RoutingGeneration &&
                                      matchedRouteIds.Contains(disposition.Route.RouteId),
                "An evaluated file disposition does not belong to its frozen plan.");
            ValidateInitialDisposition(disposition);
        }
        ValidateAppendEnvelope(
            current,
            proposal.PlanId,
            proposal.SourceClipId,
            deliveries,
            dispositions);
        ValidateInitialDuplicateSuppression(deliveries);

        var planDecision = new RoutingPlanDecision(
            proposal.PlanId,
            proposal.SourceClipId,
            proposal.RoutingGeneration,
            matchedRouteIds.ToArray(),
            resolutions.ToArray(),
            latentAuthorizations.ToArray(),
            RoutingValidation.Utc(now));
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Plans = current.Plans.Concat([planDecision]).ToArray(),
            Deliveries = current.Deliveries.Concat(deliveries).ToArray(),
            FileDispositions = current.FileDispositions.Concat(dispositions).ToArray(),
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static RoutingOutboxDocument MarkArtifactReady(
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.WaitingForArtifact,
                "Only an artifact-waiting delivery can become artifact-ready.");
            return delivery with
            {
                State = delivery.Mode == RoutingDeliveryMode.Approval
                    ? PlannedDeliveryState.WaitingForApproval
                    : PlannedDeliveryState.Ready,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    /// <summary>
    /// Applies one validated capture-journal artifact snapshot to an already frozen plan. Every
    /// delivery waiting on the same ready or failed logical output advances in one outbox
    /// generation, so an executor can never observe only part of a rendition update. Replaying
    /// the same snapshot is an exact no-op and does not manufacture another generation.
    /// </summary>
    internal static RoutingOutboxDocument ReconcilePlanArtifactAvailability(
        RoutingOutboxDocument current,
        Guid planId,
        IReadOnlyList<RoutingOutputReference> readyOutputs,
        IReadOnlyDictionary<RoutingOutputReference, string> failedOutputs,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(readyOutputs);
        ArgumentNullException.ThrowIfNull(failedOutputs);
        Validate(current);
        RoutingValidation.Require(planId != Guid.Empty, "A routing plan id is missing.");
        var plan = current.Plans.SingleOrDefault(item => item.PlanId == planId) ??
                   throw new InvalidDataException("The routing plan decision does not exist.");
        RoutingValidation.Require(
            readyOutputs.All(output => output is not null) &&
            readyOutputs.Distinct().Count() == readyOutputs.Count,
            "Ready artifact evidence must contain unique output references.");
        var readySet = readyOutputs.ToHashSet();
        RoutingValidation.Require(
            failedOutputs.Keys.All(output => output is not null) &&
            !failedOutputs.Keys.Any(readySet.Contains),
            "An output cannot be both ready and permanently missing.");
        foreach (var output in readyOutputs.Concat(failedOutputs.Keys))
        {
            ValidateOutputReference(output);
            RoutingValidation.Require(
                output.ClipId.Equals(plan.SourceClipId, StringComparison.Ordinal),
                "Artifact evidence belongs to a different source clip.");
        }
        foreach (var failed in failedOutputs)
        {
            RoutingValidation.Require(failed.Key.Kind != RoutingOutputKind.Original,
                "A missing source original cannot use a rendition fallback policy.");
            RoutingValidation.RequireErrorCode(failed.Value);
        }

        if (current.Deliveries.All(item => item.PlanId != planId)) return current;
        var utcNow = RoutingValidation.Utc(now);
        return UpdatePlanArtifactStates(current, planId, delivery =>
        {
            if (delivery.State != PlannedDeliveryState.WaitingForArtifact) return delivery;
            if (readySet.Contains(delivery.RequestedOutput))
            {
                return delivery with
                {
                    State = delivery.Mode == RoutingDeliveryMode.Approval
                        ? PlannedDeliveryState.WaitingForApproval
                        : PlannedDeliveryState.Ready,
                    UpdatedUtc = utcNow
                };
            }
            return failedOutputs.TryGetValue(delivery.RequestedOutput, out var errorCode)
                ? ResolveMissingArtifact(delivery, errorCode, utcNow)
                : delivery;
        }, requireChange: false, utcNow);
    }

    /// <summary>
    /// Persists the route's selected missing-output policy after a requested rendition fails.
    /// The decision is part of the outbox and therefore survives restart without re-evaluation.
    /// </summary>
    internal static RoutingOutboxDocument ResolveMissingArtifacts(
        RoutingOutboxDocument current,
        Guid planId,
        IReadOnlyList<RoutingOutputReference> failedOutputs,
        string errorCode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failedOutputs);
        RoutingValidation.Require(failedOutputs.Count > 0 &&
                                  failedOutputs.Distinct().Count() == failedOutputs.Count,
            "A missing-artifact report must contain a non-empty unique output-reference set.");
        RoutingValidation.RequireErrorCode(errorCode);
        Validate(current);
        var planDeliveries = current.Deliveries.Where(item => item.PlanId == planId).ToArray();
        RoutingValidation.Require(failedOutputs.All(output =>
                planDeliveries.Any(delivery =>
                    delivery.State == PlannedDeliveryState.WaitingForArtifact &&
                    delivery.RequestedOutput == output)),
            "Every failed output reference must identify a waiting artifact in this exact plan.");
        var failedSet = failedOutputs.ToHashSet();
        return UpdatePlanArtifactStates(current, planId, delivery =>
        {
            if (delivery.State != PlannedDeliveryState.WaitingForArtifact ||
                !failedSet.Contains(delivery.RequestedOutput)) return delivery;
            return ResolveMissingArtifact(
                delivery,
                errorCode,
                RoutingValidation.Utc(now));
        }, requireChange: true, now);
    }

    private static PlannedDelivery ResolveMissingArtifact(
        PlannedDelivery delivery,
        string errorCode,
        DateTimeOffset utcNow)
    {
        RoutingValidation.Require(delivery.RequestedOutput.Kind != RoutingOutputKind.Original,
            "A missing source original cannot use a rendition fallback policy.");
        return delivery.OnMissingOutput switch
        {
            RoutingMissingOutputBehavior.UseOriginal => delivery with
            {
                Output = delivery.OriginalOutput,
                ArtifactOutcome = RoutingMissingArtifactOutcome.OriginalFallback,
                ArtifactErrorCode = errorCode,
                State = delivery.Mode == RoutingDeliveryMode.Approval
                    ? PlannedDeliveryState.WaitingForApproval
                    : PlannedDeliveryState.Ready,
                UpdatedUtc = utcNow
            },
            RoutingMissingOutputBehavior.Skip => delivery with
            {
                ArtifactOutcome = RoutingMissingArtifactOutcome.Skipped,
                ArtifactErrorCode = errorCode,
                State = PlannedDeliveryState.Skipped,
                CompletedUtc = utcNow,
                UpdatedUtc = utcNow
            },
            RoutingMissingOutputBehavior.NeedsAttention => delivery with
            {
                ArtifactOutcome = RoutingMissingArtifactOutcome.NeedsAttention,
                ArtifactErrorCode = errorCode,
                State = PlannedDeliveryState.NeedsAttention,
                UpdatedUtc = utcNow
            },
            _ => throw new InvalidDataException("The missing-output behavior is unsupported.")
        };
    }

    internal static RoutingOutboxDocument ResolveNeedsAttention(
        RoutingOutboxDocument current,
        Guid deliveryId,
        RoutingNeedsAttentionResolution resolution,
        DateTimeOffset now)
    {
        Validate(current);
        var target = current.Deliveries.SingleOrDefault(item => item.DeliveryId == deliveryId)
            ?? throw new InvalidDataException("The needs-attention delivery does not exist.");
        RoutingValidation.Require(target.State == PlannedDeliveryState.NeedsAttention,
            "Only a needs-attention delivery can use an explicit artifact recovery action.");
        var utcNow = RoutingValidation.Utc(now);
        return UpdatePlanArtifactStates(current, target.PlanId, delivery =>
        {
            if (delivery.DeliveryId != deliveryId) return delivery;
            return resolution switch
            {
                RoutingNeedsAttentionResolution.UseOriginal => delivery with
                {
                    Output = delivery.OriginalOutput,
                    ArtifactOutcome = RoutingMissingArtifactOutcome.OriginalFallback,
                    State = delivery.Mode == RoutingDeliveryMode.Approval
                        ? PlannedDeliveryState.WaitingForApproval
                        : PlannedDeliveryState.Ready,
                    UpdatedUtc = utcNow
                },
                RoutingNeedsAttentionResolution.Skip => delivery with
                {
                    ArtifactOutcome = RoutingMissingArtifactOutcome.Skipped,
                    State = PlannedDeliveryState.Skipped,
                    CompletedUtc = utcNow,
                    UpdatedUtc = utcNow
                },
                RoutingNeedsAttentionResolution.RetryOrRebuild => delivery with
                {
                    Output = delivery.RequestedOutput,
                    ArtifactOutcome = RoutingMissingArtifactOutcome.RequestedOutput,
                    ArtifactErrorCode = null,
                    State = PlannedDeliveryState.WaitingForArtifact,
                    UpdatedUtc = utcNow
                },
                _ => throw new InvalidDataException(
                    "The needs-attention recovery decision is unsupported.")
            };
        }, requireChange: true, now);
    }

    internal static RoutingOutboxDocument ApproveDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.WaitingForApproval,
                "Only an approval-waiting delivery can be approved.");
            var utcNow = RoutingValidation.Utc(now);
            return delivery with
            {
                State = PlannedDeliveryState.Ready,
                ApprovedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    /// <summary>
    /// Persist this transition before making a provider request. DeliveryId is the stable provider
    /// idempotency key; CurrentAttemptId rejects completions from an abandoned earlier attempt.
    /// </summary>
    internal static RoutingOutboxDocument StartDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        Guid attemptId,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.Ready,
                "Only a ready delivery can start.");
            RoutingValidation.Require(attemptId != Guid.Empty, "A delivery attempt id is missing.");
            var utcNow = RoutingValidation.Utc(now);
            return delivery with
            {
                State = PlannedDeliveryState.Sending,
                Attempts = checked(delivery.Attempts + 1),
                CurrentAttemptId = attemptId,
                AttemptStartedUtc = utcNow,
                CompletedUtc = null,
                ErrorCode = null,
                UpdatedUtc = utcNow
            };
        }, now);

    /// <summary>
    /// Records a definite local artifact-resolution failure before any provider request starts.
    /// This is intentionally not a delivery attempt: retry remains safe and the attempt counter
    /// continues to describe only requests that reached a provider boundary.
    /// </summary>
    internal static RoutingOutboxDocument FailDeliveryBeforeProvider(
        RoutingOutboxDocument current,
        Guid deliveryId,
        string errorCode,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.Ready,
                "Only a ready delivery can fail before provider entry.");
            RoutingValidation.Require(delivery.CurrentAttemptId is null &&
                                      delivery.AttemptStartedUtc is null,
                "A pre-provider failure cannot replace an active attempt.");
            RoutingValidation.RequireErrorCode(errorCode);
            return delivery with
            {
                State = PlannedDeliveryState.Failed,
                ErrorCode = errorCode,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    internal static RoutingOutboxDocument CompleteDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        Guid attemptId,
        string remoteReceiptReference,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RequireCurrentAttempt(delivery, attemptId);
            RoutingValidation.RequireOpaqueId(remoteReceiptReference, 256,
                "remote receipt reference");
            var utcNow = RoutingValidation.Utc(now);
            return delivery with
            {
                State = PlannedDeliveryState.Delivered,
                RemoteReceiptReference = remoteReceiptReference,
                ProviderResumeReference = null,
                ErrorCode = null,
                CompletedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    internal static RoutingOutboxDocument FailDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        Guid attemptId,
        string errorCode,
        string? providerResumeReference,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RequireCurrentAttempt(delivery, attemptId);
            RoutingValidation.RequireErrorCode(errorCode);
            RoutingValidation.RequireOptionalOpaqueId(providerResumeReference, 256,
                "provider resume reference");
            return delivery with
            {
                State = PlannedDeliveryState.Failed,
                ProviderResumeReference = providerResumeReference,
                ErrorCode = errorCode,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    /// <summary>
    /// Records an attempt whose provider result is ambiguous. Unlike an ordinary failure, this
    /// state can never retry without an explicit duplicate-risk decision from the user.
    /// </summary>
    internal static RoutingOutboxDocument MarkDeliveryUnknown(
        RoutingOutboxDocument current,
        Guid deliveryId,
        Guid attemptId,
        string errorCode,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RequireCurrentAttempt(delivery, attemptId);
            RoutingValidation.RequireErrorCode(errorCode);
            return delivery with
            {
                State = PlannedDeliveryState.DeliveryUnknown,
                ErrorCode = errorCode,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    internal static RoutingOutboxDocument RetryFailedDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.Failed,
                "Only a failed delivery may retry without duplicate-risk confirmation.");
            return delivery with
            {
                State = PlannedDeliveryState.Ready,
                CurrentAttemptId = null,
                AttemptStartedUtc = null,
                ErrorCode = null,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    internal static RoutingOutboxDocument ResolveUnknownAsDelivered(
        RoutingOutboxDocument current,
        Guid deliveryId,
        string remoteReceiptReference,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.DeliveryUnknown,
                "Only an unknown delivery can be reconciled as delivered.");
            RoutingValidation.RequireOpaqueId(remoteReceiptReference, 256,
                "remote receipt reference");
            var utcNow = RoutingValidation.Utc(now);
            return delivery with
            {
                State = PlannedDeliveryState.Delivered,
                RemoteReceiptReference = remoteReceiptReference,
                ProviderResumeReference = null,
                ErrorCode = null,
                CompletedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    internal static RoutingOutboxDocument AuthorizeUnknownSendAgain(
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now) =>
        UpdateDelivery(current, deliveryId, delivery =>
        {
            RoutingValidation.Require(delivery.State == PlannedDeliveryState.DeliveryUnknown,
                "Only an unknown delivery needs duplicate-risk confirmation.");
            var utcNow = RoutingValidation.Utc(now);
            return delivery with
            {
                State = PlannedDeliveryState.Ready,
                CurrentAttemptId = null,
                AttemptStartedUtc = null,
                ErrorCode = null,
                DuplicateRiskAcceptedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    internal static RoutingOutboxDocument RefreshFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RoutingValidation.Require(
                disposition.State == PlannedFileDispositionState.WaitingForDependencies,
                "Only a waiting file disposition can refresh dependencies.");
            var deliveryById = current.Deliveries.ToDictionary(delivery => delivery.DeliveryId);
            RoutingValidation.Require(disposition.PrerequisiteDeliveryIds.All(id =>
                    deliveryById.TryGetValue(id, out var delivery) &&
                    (disposition.LibraryArea == RoutingLibraryArea.Uploaded
                        ? IsUploadedDependencySettled(delivery)
                        : delivery.State is PlannedDeliveryState.Delivered or
                            PlannedDeliveryState.Failed or PlannedDeliveryState.Cancelled or
                            PlannedDeliveryState.Expired)),
                disposition.LibraryArea == RoutingLibraryArea.Uploaded
                    ? "Uploaded filing requires every sibling to be delivered or deliberately skipped."
                    : "The terminal file disposition still has active delivery dependencies.");
            if (disposition.LibraryArea == RoutingLibraryArea.Uploaded)
            {
                RoutingValidation.Require(disposition.PrerequisiteDeliveryIds.Any(id =>
                        deliveryById[id].State == PlannedDeliveryState.Delivered),
                    "Uploaded filing requires at least one confirmed delivered sibling.");
            }
            return disposition with
            {
                State = PlannedFileDispositionState.Ready,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    /// <summary>Persist before moving the source file.</summary>
    internal static RoutingOutboxDocument StartFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        Guid attemptId,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RoutingValidation.Require(disposition.State == PlannedFileDispositionState.Ready,
                "Only a ready file disposition can start.");
            RoutingValidation.Require(attemptId != Guid.Empty, "A file-move attempt id is missing.");
            var utcNow = RoutingValidation.Utc(now);
            return disposition with
            {
                State = PlannedFileDispositionState.Moving,
                Attempts = checked(disposition.Attempts + 1),
                CurrentAttemptId = attemptId,
                AttemptStartedUtc = utcNow,
                ErrorCode = null,
                UpdatedUtc = utcNow
            };
        }, now);

    internal static RoutingOutboxDocument CompleteFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        Guid attemptId,
        string finalLibraryItemReference,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RequireCurrentAttempt(disposition, attemptId);
            RoutingValidation.RequireOpaqueId(finalLibraryItemReference, 256,
                "final library item reference");
            var utcNow = RoutingValidation.Utc(now);
            return disposition with
            {
                State = PlannedFileDispositionState.Completed,
                FinalLibraryItemReference = finalLibraryItemReference,
                ErrorCode = null,
                CompletedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    /// <summary>
    /// Completes startup reconciliation after the library index proves that the intended
    /// destination owns the exact source hash. No move or second attempt is performed.
    /// </summary>
    internal static RoutingOutboxDocument CompleteRecoveredFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        string finalLibraryItemReference,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RoutingValidation.Require(
                disposition.State == PlannedFileDispositionState.RecoveryPending,
                "Only a recovery-pending file disposition can be reconciled as completed.");
            RoutingValidation.RequireOpaqueId(finalLibraryItemReference, 256,
                "final library item reference");
            var utcNow = RoutingValidation.Utc(now);
            return disposition with
            {
                State = PlannedFileDispositionState.Completed,
                FinalLibraryItemReference = finalLibraryItemReference,
                ErrorCode = null,
                CompletedUtc = utcNow,
                UpdatedUtc = utcNow
            };
        }, now);

    internal static RoutingOutboxDocument FailFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        Guid attemptId,
        string errorCode,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RequireCurrentAttempt(disposition, attemptId);
            RoutingValidation.RequireErrorCode(errorCode);
            return disposition with
            {
                State = PlannedFileDispositionState.Failed,
                ErrorCode = errorCode,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    /// <summary>
    /// Records a file operation whose result must be reconciled by exact library identity before
    /// retrying. This prevents an ambiguous move from selecting a second destination.
    /// </summary>
    internal static RoutingOutboxDocument MarkFileDispositionRecoveryPending(
        RoutingOutboxDocument current,
        Guid dispositionId,
        Guid attemptId,
        string errorCode,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RequireCurrentAttempt(disposition, attemptId);
            RoutingValidation.RequireErrorCode(errorCode);
            return disposition with
            {
                State = PlannedFileDispositionState.RecoveryPending,
                ErrorCode = errorCode,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    internal static RoutingOutboxDocument RetryFileDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        DateTimeOffset now) =>
        UpdateDisposition(current, dispositionId, disposition =>
        {
            RoutingValidation.Require(
                disposition.State is PlannedFileDispositionState.Failed or
                    PlannedFileDispositionState.RecoveryPending,
                "Only a failed or recovery-pending file disposition can retry.");
            return disposition with
            {
                State = PlannedFileDispositionState.Ready,
                CurrentAttemptId = null,
                AttemptStartedUtc = null,
                ErrorCode = null,
                UpdatedUtc = RoutingValidation.Utc(now)
            };
        }, now);

    /// <summary>
    /// Startup recovery never repeats a possibly accepted network request. Sending becomes
    /// DeliveryUnknown. A moving file is handed to a content-hash reconciler, which can prove
    /// whether source or destination owns the bytes before selecting Complete or Retry.
    /// </summary>
    internal static RoutingOutboxDocument RecoverInterruptedWork(
        RoutingOutboxDocument current,
        DateTimeOffset now)
    {
        Validate(current);
        var utcNow = RoutingValidation.Utc(now);
        var changed = false;
        var deliveries = current.Deliveries.Select(delivery =>
        {
            if (delivery.State != PlannedDeliveryState.Sending) return delivery;
            changed = true;
            return delivery with
            {
                State = PlannedDeliveryState.DeliveryUnknown,
                ErrorCode = "interrupted-after-send-started",
                UpdatedUtc = utcNow
            };
        }).ToArray();
        var dispositions = current.FileDispositions.Select(disposition =>
        {
            if (disposition.State != PlannedFileDispositionState.Moving) return disposition;
            changed = true;
            return disposition with
            {
                State = PlannedFileDispositionState.RecoveryPending,
                ErrorCode = "interrupted-during-library-move",
                UpdatedUtc = utcNow
            };
        }).ToArray();
        if (!changed) return current;
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Deliveries = deliveries,
            FileDispositions = dispositions,
            UpdatedUtc = utcNow
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static void Validate(RoutingOutboxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(document.SchemaVersion == RoutingOutboxStore.CurrentSchemaVersion,
            "The routing outbox schema is unsupported.");
        RoutingValidation.Require(document.Generation > 0, "The outbox generation is invalid.");
        var deliveries = document.Deliveries ??
            throw new InvalidDataException("The planned delivery collection is missing.");
        var dispositions = document.FileDispositions ??
            throw new InvalidDataException("The file disposition collection is missing.");
        var plans = document.Plans ??
            throw new InvalidDataException("The routing plan decision collection is missing.");
        RoutingValidation.Require(plans.Count <= MaximumPlans,
            "The routing outbox contains too many plan decisions.");
        RoutingValidation.Require(deliveries.Count <= MaximumDeliveries,
            "The routing outbox contains too many deliveries.");
        RoutingValidation.Require(dispositions.Count <= MaximumFileDispositions,
            "The routing outbox contains too many file dispositions.");
        RoutingValidation.RequireUtc(document.CreatedUtc, "outbox creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc, "outbox update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The outbox timestamps are inconsistent.");

        var planIds = new HashSet<Guid>();
        var planSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            if (plan is null) throw new InvalidDataException("A routing plan decision is missing.");
            ValidatePlanDecision(plan);
            RoutingValidation.Require(planIds.Add(plan.PlanId),
                "Routing plan decision ids must be unique.");
            RoutingValidation.Require(planSources.Add(plan.SourceClipId),
                "A source clip can have only one durable routing decision.");
        }

        var deliveryIds = new HashSet<Guid>();
        foreach (var delivery in deliveries)
        {
            if (delivery is null) throw new InvalidDataException("A planned delivery is missing.");
            ValidateDelivery(delivery);
            RoutingValidation.Require(deliveryIds.Add(delivery.DeliveryId),
                "Planned delivery ids must be unique.");
        }
        ValidateDuplicateProvenance(deliveries);

        var dispositionIds = new HashSet<Guid>();
        foreach (var disposition in dispositions)
        {
            if (disposition is null) throw new InvalidDataException("A file disposition is missing.");
            ValidateDisposition(disposition);
            RoutingValidation.Require(dispositionIds.Add(disposition.DispositionId),
                "File disposition ids must be unique.");
            foreach (var dependencyId in disposition.PrerequisiteDeliveryIds)
            {
                RoutingValidation.Require(deliveries.Any(delivery =>
                        delivery.DeliveryId == dependencyId &&
                        delivery.PlanId == disposition.PlanId &&
                        delivery.SourceClipId.Equals(disposition.SourceClipId, StringComparison.Ordinal)),
                    "A file disposition dependency is missing or belongs to another plan.");
            }
        }


        RoutingValidation.Require(deliveries.All(item => planIds.Contains(item.PlanId)) &&
                                  dispositions.All(item => planIds.Contains(item.PlanId)),
            "Every persisted plan member requires a durable plan decision header.");

        foreach (var plan in plans)
        {
            var planDeliveries = deliveries.Where(item => item.PlanId == plan.PlanId).ToArray();
            var planDispositions = dispositions.Where(item => item.PlanId == plan.PlanId).ToArray();
            var duplicateAuthorizations = plan.LatentDuplicateAuthorizations.ToHashSet();
            RoutingValidation.Require(planDeliveries.All(delivery =>
                    delivery.IntentionalDuplicate is null ||
                    duplicateAuthorizations.Contains(delivery.IntentionalDuplicate)),
                "A delivery duplicate proof is not retained by its immutable plan decision.");
            var sources = planDeliveries.Select(item => item.SourceClipId)
                .Concat(planDispositions.Select(item => item.SourceClipId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            RoutingValidation.Require((sources.Length == 0 ||
                                      sources.Length == 1 && sources[0].Equals(
                                          plan.SourceClipId, StringComparison.Ordinal)) &&
                                      planDeliveries.All(item =>
                    item.Output.ClipId.Equals(plan.SourceClipId, StringComparison.Ordinal)),
                "Each outbox plan must describe one coherent source and its outputs.");
            foreach (var resolution in plan.InitialMissingResolutions)
            {
                RoutingValidation.Require(planDeliveries.Any(delivery =>
                        delivery.DeliveryId == resolution.DeliveryId &&
                        delivery.RequestedOutput == resolution.RequestedOutput),
                    "Initial missing-output evidence does not name its immutable plan delivery.");
            }
            var routeReferences = planDeliveries.Select(item => item.Route)
                .Concat(planDispositions.Select(item => item.Route))
                .ToArray();
            RoutingValidation.Require(routeReferences.All(item =>
                                          item.RoutingGeneration == plan.RoutingGeneration &&
                                          plan.MatchedRouteIds.Contains(item.RouteId)),
                "Every persisted plan member must share one frozen routing generation.");
            RoutingValidation.Require(routeReferences.Select(item => (item.RouteId, item.ActionId))
                                          .Distinct().Count() == routeReferences.Length,
                "A persisted plan cannot repeat the same route action.");
            RoutingValidation.Require(planDispositions.Length <= 1,
                "Each outbox plan/source can have only one terminal disposition.");
            if (planDispositions.Length == 0) continue;

            var disposition = planDispositions[0];
            var siblings = planDeliveries.Select(item => item.DeliveryId).ToHashSet();
            RoutingValidation.Require(
                disposition.PrerequisiteDeliveryIds.ToHashSet().SetEquals(siblings),
                "A file disposition dependency list must cover every sibling delivery exactly.");
            if (disposition.LibraryArea == RoutingLibraryArea.LocalOnly)
            {
                RoutingValidation.Require(planDeliveries.Length == 0,
                    "Local-only filing is deliberate and cannot share a plan with external delivery.");
            }
            else
            {
                RoutingValidation.Require(planDeliveries.Length > 0,
                    "Uploaded filing requires at least one planned external delivery.");
                if (disposition.State is not PlannedFileDispositionState.WaitingForDependencies)
                {
                    RoutingValidation.Require(planDeliveries.All(IsUploadedDependencySettled) &&
                                              planDeliveries.Any(item =>
                                                  item.State == PlannedDeliveryState.Delivered),
                        "Uploaded filing requires all siblings settled and at least one confirmed delivery.");
                }
            }
        }
    }

    internal static void ValidateSuccessor(
        RoutingOutboxDocument current,
        RoutingOutboxDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(candidate.Generation == checked(current.Generation + 1),
            "The routing outbox does not advance one generation.");
        RoutingValidation.Require(candidate.CreatedUtc == current.CreatedUtc &&
                                  candidate.UpdatedUtc >= current.UpdatedUtc,
            "The routing outbox changed immutable timestamps.");
        RoutingValidation.Require(candidate.Plans.Count >= current.Plans.Count &&
                                  candidate.Deliveries.Count >= current.Deliveries.Count &&
                                  candidate.FileDispositions.Count >= current.FileDispositions.Count,
            "Routing outbox history cannot be removed by an ordinary transition.");

        var candidatePlans = candidate.Plans.ToDictionary(item => item.PlanId);
        foreach (var previous in current.Plans)
        {
            RoutingValidation.Require(candidatePlans.TryGetValue(previous.PlanId, out var next) &&
                                      next == previous,
                "A durable routing plan decision changed or disappeared.");
        }
        var addedPlans = candidate.Plans.Where(item =>
                current.Plans.All(previous => previous.PlanId != item.PlanId))
            .ToArray();
        RoutingValidation.Require(addedPlans.Length <= 1,
            "At most one new source routing decision may be appended per generation.");

        var candidateDeliveries = candidate.Deliveries.ToDictionary(item => item.DeliveryId);
        foreach (var previous in current.Deliveries)
        {
            RoutingValidation.Require(candidateDeliveries.TryGetValue(previous.DeliveryId, out var next),
                "A planned delivery disappeared.");
            ValidateDeliverySuccessor(previous, next!);
        }
        var addedDeliveries = candidate.Deliveries.Where(item =>
                current.Deliveries.All(previous => previous.DeliveryId != item.DeliveryId))
            .ToArray();

        var candidateDispositions = candidate.FileDispositions.ToDictionary(item => item.DispositionId);
        foreach (var previous in current.FileDispositions)
        {
            RoutingValidation.Require(candidateDispositions.TryGetValue(previous.DispositionId, out var next),
                "A file disposition disappeared.");
            ValidateDispositionSuccessor(previous, next!);
        }
        var addedDispositions = candidate.FileDispositions.Where(item =>
                current.FileDispositions.All(previous => previous.DispositionId != item.DispositionId))
            .ToArray();

        if (addedPlans.Length == 0)
        {
            RoutingValidation.Require(addedDeliveries.Length == 0 && addedDispositions.Length == 0,
                "New outbox work requires one durable plan decision in the same generation.");
            return;
        }

        var addedPlan = addedPlans[0];
        RoutingValidation.Require(addedDeliveries.All(item => item.PlanId == addedPlan.PlanId) &&
                                  addedDispositions.All(item => item.PlanId == addedPlan.PlanId),
            "One generation cannot mix work from multiple new plans.");
        var resolutionByDelivery = addedPlan.InitialMissingResolutions
            .ToDictionary(item => item.DeliveryId);
        foreach (var added in addedDeliveries)
        {
            if (resolutionByDelivery.TryGetValue(added.DeliveryId, out var resolution))
                ValidateImmediateMissingResolution(added, resolution);
            else
                ValidateInitialDelivery(added);
        }
        RoutingValidation.Require(resolutionByDelivery.Keys.All(id =>
                addedDeliveries.Any(item => item.DeliveryId == id)),
            "Initial missing-output evidence names work outside its new plan generation.");
        ValidateInitialDuplicateSuppression(addedDeliveries);
        foreach (var added in addedDispositions) ValidateInitialDisposition(added);
    }

    private static RoutingOutboxDocument UpdateDelivery(
        RoutingOutboxDocument current,
        Guid deliveryId,
        Func<PlannedDelivery, PlannedDelivery> update,
        DateTimeOffset now)
    {
        Validate(current);
        RoutingValidation.Require(deliveryId != Guid.Empty, "A delivery id is missing.");
        var found = false;
        var deliveries = current.Deliveries.Select(delivery =>
        {
            if (delivery.DeliveryId != deliveryId) return delivery;
            found = true;
            return update(delivery);
        }).ToArray();
        RoutingValidation.Require(found, "The planned delivery does not exist.");
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Deliveries = deliveries,
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    private static RoutingOutboxDocument UpdatePlanArtifactStates(
        RoutingOutboxDocument current,
        Guid planId,
        Func<PlannedDelivery, PlannedDelivery> update,
        bool requireChange,
        DateTimeOffset now)
    {
        Validate(current);
        RoutingValidation.Require(planId != Guid.Empty, "A routing plan id is missing.");
        var originalPlan = current.Deliveries.Where(item => item.PlanId == planId).ToArray();
        RoutingValidation.Require(originalPlan.Length > 0,
            "The routing plan has no deliveries to resolve.");
        var utcNow = RoutingValidation.Utc(now);
        var changed = false;
        var updatedPlan = originalPlan.Select(delivery =>
        {
            var next = update(delivery);
            if (next != delivery) changed = true;
            return next;
        }).ToArray();
        RoutingValidation.Require(!requireChange || changed,
            "The routing plan has no matching artifact state to resolve.");
        if (!changed) return current;

        var planDecision = current.Plans.Single(item => item.PlanId == planId);
        updatedPlan = ReconcilePlanDuplicateKeys(
            originalPlan, updatedPlan, planDecision.LatentDuplicateAuthorizations, utcNow);
        var byId = updatedPlan.ToDictionary(item => item.DeliveryId);
        var deliveries = current.Deliveries.Select(item =>
            item.PlanId == planId ? byId[item.DeliveryId] : item).ToArray();
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Deliveries = deliveries,
            UpdatedUtc = utcNow
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    private static PlannedDelivery[] ReconcilePlanDuplicateKeys(
        IReadOnlyList<PlannedDelivery> originalPlan,
        IReadOnlyList<PlannedDelivery> updatedPlan,
        IReadOnlyList<IntentionalDuplicateProvenance> latentAuthorizations,
        DateTimeOffset now)
    {
        var originalById = originalPlan.ToDictionary(item => item.DeliveryId);
        var result = updatedPlan.Select(item => item with { IntentionalDuplicate = null }).ToArray();
        foreach (var group in result.Where(IsDuplicateRelevant).GroupBy(item => item.DeliveryKey))
        {
            var members = group.ToArray();
            if (members.Length == 1) continue;

            if (members.Length == 2 && TryGetExactPairAuthorization(
                    originalById[members[0].DeliveryId],
                    originalById[members[1].DeliveryId],
                    group.Key,
                    latentAuthorizations,
                    out var proof))
            {
                for (var index = 0; index < result.Length; index++)
                {
                    if (members.Any(member => member.DeliveryId == result[index].DeliveryId))
                        result[index] = result[index] with { IntentionalDuplicate = proof };
                }
                continue;
            }

            var winner = members
                .OrderBy(item => item.Route.Priority)
                .ThenBy(item => item.Route.Order)
                .ThenBy(item => item.Route.RouteId)
                .First();
            foreach (var loser in members.Where(item => item.DeliveryId != winner.DeliveryId))
            {
                RoutingValidation.Require(loser.State is not PlannedDeliveryState.Sending and
                    not PlannedDeliveryState.Delivered and not PlannedDeliveryState.Failed and
                    not PlannedDeliveryState.DeliveryUnknown,
                    "A fallback duplicate collision must be resolved before a provider attempt starts.");
                for (var index = 0; index < result.Length; index++)
                {
                    if (result[index].DeliveryId != loser.DeliveryId) continue;
                    result[index] = result[index] with
                    {
                        State = PlannedDeliveryState.Skipped,
                        ArtifactOutcome = RoutingMissingArtifactOutcome.DuplicateSuppressed,
                        ArtifactErrorCode = "duplicate-fallback-suppressed",
                        CompletedUtc = now,
                        IntentionalDuplicate = null,
                        UpdatedUtc = now
                    };
                }
            }
        }
        return result;
    }

    private static bool IsDuplicateRelevant(PlannedDelivery delivery) =>
        delivery.State is not PlannedDeliveryState.Skipped and
            not PlannedDeliveryState.Cancelled and not PlannedDeliveryState.Expired;

    private static bool IsUploadedDependencySettled(PlannedDelivery delivery) =>
        delivery.State == PlannedDeliveryState.Delivered ||
        delivery.State == PlannedDeliveryState.Skipped &&
        delivery.ArtifactOutcome is RoutingMissingArtifactOutcome.Skipped or
            RoutingMissingArtifactOutcome.DuplicateSuppressed;

    private static bool TryGetExactPairAuthorization(
        PlannedDelivery first,
        PlannedDelivery second,
        RoutingDeliveryKey finalKey,
        IReadOnlyList<IntentionalDuplicateProvenance> latentAuthorizations,
        out IntentionalDuplicateProvenance proof)
    {
        var routes = new HashSet<Guid> { first.Route.RouteId, second.Route.RouteId };
        var attached = first.IntentionalDuplicate;
        if (attached is not null && second.IntentionalDuplicate == attached &&
            attached.DeliveryKey == finalKey &&
            latentAuthorizations.Contains(attached) &&
            routes.SetEquals([attached.FirstRouteId, attached.SecondRouteId]))
        {
            proof = attached;
            return true;
        }

        var matches = latentAuthorizations.Where(candidate =>
                candidate.DeliveryKey == finalKey &&
                routes.SetEquals([candidate.FirstRouteId, candidate.SecondRouteId]))
            .ToArray();
        RoutingValidation.Require(matches.Length <= 1,
            "Latent duplicate-delivery authorization is ambiguous.");
        proof = matches.SingleOrDefault()!;
        return proof is not null;
    }

    private static RoutingOutboxDocument UpdateDisposition(
        RoutingOutboxDocument current,
        Guid dispositionId,
        Func<PlannedFileDisposition, PlannedFileDisposition> update,
        DateTimeOffset now)
    {
        Validate(current);
        RoutingValidation.Require(dispositionId != Guid.Empty, "A file disposition id is missing.");
        var found = false;
        var dispositions = current.FileDispositions.Select(disposition =>
        {
            if (disposition.DispositionId != dispositionId) return disposition;
            found = true;
            return update(disposition);
        }).ToArray();
        RoutingValidation.Require(found, "The file disposition does not exist.");
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            FileDispositions = dispositions,
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        Validate(candidate);
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    private static void ValidateDelivery(PlannedDelivery delivery)
    {
        RoutingValidation.Require(delivery.DeliveryId != Guid.Empty && delivery.PlanId != Guid.Empty,
            "A planned delivery identity is missing.");
        RoutingValidation.Require(Enum.IsDefined(delivery.Destination) &&
                                  Enum.IsDefined(delivery.Mode) &&
                                  Enum.IsDefined(delivery.State),
            "A delivery destination, mode, or state is unsupported.");
        RoutingValidation.RequireOpaqueId(delivery.SourceClipId, 256, "source clip id");
        ValidateRouteReference(delivery.Route);
        RoutingValidation.RequireOpaqueId(delivery.ConnectionId, 128, "connection id");
        ValidateOutputReference(delivery.RequestedOutput);
        ValidateOutputReference(delivery.Output);
        ValidateOutputReference(delivery.OriginalOutput);
        RoutingValidation.Require(Enum.IsDefined(delivery.OnMissingOutput) &&
                                  Enum.IsDefined(delivery.ArtifactOutcome),
            "A delivery missing-artifact policy or outcome is unsupported.");
        if (delivery.ArtifactErrorCode is not null)
            RoutingValidation.RequireErrorCode(delivery.ArtifactErrorCode);
        RoutingValidation.ValidateDeliverySettings(delivery.Settings);
        RoutingValidation.Require(delivery.Attempts >= 0, "A delivery attempt count is invalid.");
        RoutingValidation.RequireOptionalOpaqueId(delivery.ProviderResumeReference, 256,
            "provider resume reference");
        RoutingValidation.RequireOptionalOpaqueId(delivery.RemoteReceiptReference, 256,
            "remote receipt reference");
        if (delivery.ErrorCode is not null) RoutingValidation.RequireErrorCode(delivery.ErrorCode);
        RoutingValidation.RequireUtc(delivery.CreatedUtc, "delivery creation timestamp");
        RoutingValidation.RequireUtc(delivery.UpdatedUtc, "delivery update timestamp");
        RoutingValidation.RequireOptionalUtc(delivery.ApprovedUtc, "delivery approval timestamp");
        RoutingValidation.RequireOptionalUtc(delivery.AttemptStartedUtc, "delivery attempt timestamp");
        RoutingValidation.RequireOptionalUtc(delivery.CompletedUtc, "delivery completion timestamp");
        RoutingValidation.RequireOptionalUtc(delivery.DuplicateRiskAcceptedUtc,
            "duplicate-risk confirmation timestamp");
        RoutingValidation.Require(delivery.UpdatedUtc >= delivery.CreatedUtc,
            "The delivery timestamps are inconsistent.");
        RoutingValidation.Require(delivery.Output.ClipId.Equals(
                delivery.SourceClipId, StringComparison.Ordinal),
            "A delivery output must belong to its source clip.");
        RoutingValidation.Require(delivery.RequestedOutput.ClipId.Equals(
                                      delivery.SourceClipId, StringComparison.Ordinal) &&
                                  delivery.OriginalOutput.ClipId.Equals(
                                      delivery.SourceClipId, StringComparison.Ordinal) &&
                                  delivery.OriginalOutput.Kind == RoutingOutputKind.Original,
            "A delivery must retain one requested output and its source original.");
        RoutingValidation.Require(
            (delivery.CurrentAttemptId is null) == (delivery.AttemptStartedUtc is null),
            "A delivery attempt id and start time must appear together.");
        RoutingValidation.Require(delivery.ApprovedUtc is null ||
                                  delivery.ApprovedUtc is var approved &&
                                  approved >= delivery.CreatedUtc && approved <= delivery.UpdatedUtc,
            "The delivery approval timestamp is outside its history.");
        RoutingValidation.Require(delivery.AttemptStartedUtc is null ||
                                  delivery.AttemptStartedUtc is var started &&
                                  started >= delivery.CreatedUtc && started <= delivery.UpdatedUtc,
            "The delivery attempt timestamp is outside its history.");
        RoutingValidation.Require(delivery.CompletedUtc is null ||
                                  delivery.CompletedUtc is var completed &&
                                  completed >= delivery.CreatedUtc && completed <= delivery.UpdatedUtc &&
                                  (delivery.AttemptStartedUtc is null ||
                                   completed >= delivery.AttemptStartedUtc),
            "The delivery completion timestamp is outside its history.");
        RoutingValidation.Require(delivery.DuplicateRiskAcceptedUtc is null ||
                                  delivery.DuplicateRiskAcceptedUtc is var accepted &&
                                  accepted >= delivery.CreatedUtc && accepted <= delivery.UpdatedUtc,
            "The duplicate-risk timestamp is outside its history.");
        if (delivery.Attempts == 0)
        {
            RoutingValidation.Require(delivery.CurrentAttemptId is null,
                "An unattempted delivery cannot contain attempt state.");
        }

        var hasAttempt = delivery.Attempts > 0 && delivery.CurrentAttemptId is not null;
        var noReceiptOrCompletion = delivery.RemoteReceiptReference is null &&
                                    delivery.CompletedUtc is null;
        switch (delivery.State)
        {
            case PlannedDeliveryState.WaitingForArtifact:
            case PlannedDeliveryState.WaitingForApproval:
                RoutingValidation.Require(delivery.Attempts == 0 && !hasAttempt &&
                                          delivery.ProviderResumeReference is null &&
                                          noReceiptOrCompletion && delivery.ErrorCode is null &&
                                          delivery.DuplicateRiskAcceptedUtc is null,
                    "A waiting delivery contains attempt, receipt, or error state.");
                break;
            case PlannedDeliveryState.NeedsAttention:
                RoutingValidation.Require(delivery.Attempts == 0 && !hasAttempt &&
                                          noReceiptOrCompletion &&
                                          delivery.ProviderResumeReference is null &&
                                          delivery.ErrorCode is null &&
                                          delivery.DuplicateRiskAcceptedUtc is null &&
                                          delivery.ArtifactOutcome ==
                                          RoutingMissingArtifactOutcome.NeedsAttention &&
                                          delivery.ArtifactErrorCode is not null,
                    "A needs-attention delivery must retain its missing-artifact reason.");
                break;
            case PlannedDeliveryState.Skipped:
                RoutingValidation.Require(delivery.Attempts == 0 && !hasAttempt &&
                                          delivery.RemoteReceiptReference is null &&
                                          delivery.ProviderResumeReference is null &&
                                          delivery.ErrorCode is null &&
                                          delivery.DuplicateRiskAcceptedUtc is null &&
                                          delivery.CompletedUtc is not null &&
                                          delivery.ArtifactOutcome is
                                              RoutingMissingArtifactOutcome.Skipped or
                                              RoutingMissingArtifactOutcome.DuplicateSuppressed &&
                                          delivery.ArtifactErrorCode is not null,
                    "A skipped delivery must retain its durable missing-artifact outcome.");
                break;
            case PlannedDeliveryState.Ready:
                RoutingValidation.Require(delivery.CurrentAttemptId is null &&
                                          delivery.AttemptStartedUtc is null &&
                                          noReceiptOrCompletion && delivery.ErrorCode is null,
                    "A ready delivery has an active or completed attempt.");
                break;
            case PlannedDeliveryState.Sending:
                RoutingValidation.Require(hasAttempt && noReceiptOrCompletion &&
                                          delivery.ErrorCode is null,
                    "A sending delivery has an invalid attempt shape.");
                break;
            case PlannedDeliveryState.Delivered:
                RoutingValidation.Require(hasAttempt &&
                                          delivery.RemoteReceiptReference is not null &&
                                          delivery.CompletedUtc is not null &&
                                          delivery.ProviderResumeReference is null &&
                                          delivery.ErrorCode is null,
                    "A delivered item requires one completed attempt and durable receipt.");
                break;
            case PlannedDeliveryState.Failed:
                RoutingValidation.Require(noReceiptOrCompletion &&
                                           delivery.ErrorCode is not null &&
                                           (hasAttempt || delivery.CurrentAttemptId is null &&
                                               delivery.AttemptStartedUtc is null),
                    "A failed delivery requires a safe error and either an attempted send or a definite pre-provider failure.");
                break;
            case PlannedDeliveryState.DeliveryUnknown:
                RoutingValidation.Require(hasAttempt && noReceiptOrCompletion &&
                                           delivery.ErrorCode is not null,
                    "An unknown delivery requires an attempted send and safe error.");
                break;
            case PlannedDeliveryState.Cancelled:
            case PlannedDeliveryState.Expired:
                RoutingValidation.Require(noReceiptOrCompletion,
                    "A cancelled or expired delivery cannot contain a delivery receipt.");
                break;
            default:
                throw new InvalidDataException("A planned delivery state is unsupported.");
        }

        switch (delivery.ArtifactOutcome)
        {
            case RoutingMissingArtifactOutcome.RequestedOutput:
                RoutingValidation.Require(delivery.Output == delivery.RequestedOutput &&
                                          delivery.ArtifactErrorCode is null,
                    "A requested-output delivery contains fallback evidence.");
                break;
            case RoutingMissingArtifactOutcome.OriginalFallback:
                RoutingValidation.Require(delivery.Output == delivery.OriginalOutput &&
                                          delivery.ArtifactErrorCode is not null,
                    "An original fallback does not match its persisted policy and source.");
                break;
            case RoutingMissingArtifactOutcome.Skipped:
                RoutingValidation.Require(delivery.State == PlannedDeliveryState.Skipped,
                    "A skipped artifact outcome does not match its persisted policy.");
                break;
            case RoutingMissingArtifactOutcome.NeedsAttention:
                RoutingValidation.Require(delivery.OnMissingOutput ==
                                              RoutingMissingOutputBehavior.NeedsAttention &&
                                          delivery.State == PlannedDeliveryState.NeedsAttention,
                    "A needs-attention outcome does not match its persisted policy.");
                break;
            case RoutingMissingArtifactOutcome.DuplicateSuppressed:
                RoutingValidation.Require(delivery.State == PlannedDeliveryState.Skipped &&
                                          delivery.ArtifactErrorCode ==
                                              "duplicate-fallback-suppressed",
                    "A duplicate-suppressed outcome is not a deterministic original fallback.");
                break;
        }

        if (delivery.Mode == RoutingDeliveryMode.Automatic)
        {
            RoutingValidation.Require(delivery.ApprovedUtc is null,
                "An automatic delivery cannot carry an approval timestamp.");
        }
        else if (delivery.State is PlannedDeliveryState.Ready or PlannedDeliveryState.Sending or
                 PlannedDeliveryState.Delivered or PlannedDeliveryState.Failed or
                 PlannedDeliveryState.DeliveryUnknown)
        {
            RoutingValidation.Require(delivery.ApprovedUtc is not null,
                "An approval-gated delivery cannot advance without approval.");
        }
        if (delivery.IntentionalDuplicate is not null)
        {
            ValidateDuplicate(delivery.IntentionalDuplicate);
        }
    }

    private static void ValidateDisposition(PlannedFileDisposition disposition)
    {
        RoutingValidation.Require(disposition.DispositionId != Guid.Empty && disposition.PlanId != Guid.Empty,
            "A file disposition identity is missing.");
        RoutingValidation.Require(Enum.IsDefined(disposition.LibraryArea) &&
                                  Enum.IsDefined(disposition.State),
            "A file disposition area or state is unsupported.");
        RoutingValidation.RequireOpaqueId(disposition.SourceClipId, 256, "source clip id");
        RoutingValidation.RequireSha256(disposition.SourceContentSha256, "source content hash");
        ValidateRouteReference(disposition.Route);
        var dependencies = disposition.PrerequisiteDeliveryIds ??
            throw new InvalidDataException("The file disposition dependency list is missing.");
        RoutingValidation.Require(dependencies.Count == dependencies.Distinct().Count(),
            "File disposition dependencies must be unique.");
        RoutingValidation.Require(disposition.Attempts >= 0,
            "A file disposition attempt count is invalid.");
        RoutingValidation.RequireOptionalOpaqueId(disposition.FinalLibraryItemReference, 256,
            "final library item reference");
        if (disposition.ErrorCode is not null) RoutingValidation.RequireErrorCode(disposition.ErrorCode);
        RoutingValidation.RequireUtc(disposition.CreatedUtc, "file disposition creation timestamp");
        RoutingValidation.RequireUtc(disposition.UpdatedUtc, "file disposition update timestamp");
        RoutingValidation.RequireOptionalUtc(disposition.AttemptStartedUtc,
            "file disposition attempt timestamp");
        RoutingValidation.RequireOptionalUtc(disposition.CompletedUtc,
            "file disposition completion timestamp");
        RoutingValidation.Require(disposition.UpdatedUtc >= disposition.CreatedUtc,
            "The file disposition timestamps are inconsistent.");
        RoutingValidation.Require(
            (disposition.CurrentAttemptId is null) == (disposition.AttemptStartedUtc is null),
            "A file disposition attempt id and start time must appear together.");
        RoutingValidation.Require(disposition.AttemptStartedUtc is null ||
                                  disposition.AttemptStartedUtc is var started &&
                                  started >= disposition.CreatedUtc && started <= disposition.UpdatedUtc,
            "The file disposition attempt timestamp is outside its history.");
        RoutingValidation.Require(disposition.CompletedUtc is null ||
                                  disposition.CompletedUtc is var completed &&
                                  completed >= disposition.CreatedUtc && completed <= disposition.UpdatedUtc &&
                                  (disposition.AttemptStartedUtc is null ||
                                   completed >= disposition.AttemptStartedUtc),
            "The file disposition completion timestamp is outside its history.");

        var hasMoveAttempt = disposition.Attempts > 0 && disposition.CurrentAttemptId is not null;
        var noCompletion = disposition.FinalLibraryItemReference is null &&
                           disposition.CompletedUtc is null;
        switch (disposition.State)
        {
            case PlannedFileDispositionState.WaitingForDependencies:
                RoutingValidation.Require(dependencies.Count > 0 &&
                                          disposition.Attempts == 0 && !hasMoveAttempt &&
                                          noCompletion && disposition.ErrorCode is null,
                    "A dependency-waiting disposition contains move state.");
                break;
            case PlannedFileDispositionState.Ready:
                RoutingValidation.Require(disposition.CurrentAttemptId is null &&
                                          disposition.AttemptStartedUtc is null &&
                                          noCompletion && disposition.ErrorCode is null,
                    "A ready disposition contains active or completed move state.");
                break;
            case PlannedFileDispositionState.Moving:
                RoutingValidation.Require(hasMoveAttempt && noCompletion &&
                                          disposition.ErrorCode is null,
                    "A moving disposition requires one active move attempt.");
                break;
            case PlannedFileDispositionState.RecoveryPending:
            case PlannedFileDispositionState.Failed:
                RoutingValidation.Require(hasMoveAttempt && noCompletion &&
                                          disposition.ErrorCode is not null,
                    "A failed or recovery-pending disposition requires attempted move state.");
                break;
            case PlannedFileDispositionState.Completed:
                RoutingValidation.Require(hasMoveAttempt &&
                                          disposition.FinalLibraryItemReference is not null &&
                                          disposition.CompletedUtc is not null &&
                                          disposition.ErrorCode is null,
                    "A completed disposition requires one move attempt and library reference.");
                break;
            case PlannedFileDispositionState.Cancelled:
                RoutingValidation.Require(noCompletion,
                    "A cancelled disposition cannot contain a library completion.");
                break;
            default:
                throw new InvalidDataException("A file disposition state is unsupported.");
        }
    }

    private static void ValidateAppendEnvelope(
        RoutingOutboxDocument current,
        Guid planId,
        string sourceClipId,
        IReadOnlyList<PlannedDelivery> deliveries,
        IReadOnlyList<PlannedFileDisposition> dispositions)
    {
        RoutingValidation.Require(current.Plans.All(item => item.PlanId != planId) &&
                                  current.Deliveries.All(item => item.PlanId != planId) &&
                                  current.FileDispositions.All(item => item.PlanId != planId),
            "A routing plan is append-only and can be planned only once.");
        RoutingValidation.Require(current.Plans.All(item =>
                                      !item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)) &&
                                  current.Deliveries.All(item =>
                                      !item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)) &&
                                  current.FileDispositions.All(item =>
                                      !item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)),
            "A source clip can be frozen into exactly one durable routing plan.");
        RoutingValidation.Require(deliveries.All(item => item.PlanId == planId &&
                                      item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal) &&
                                      item.Output.ClipId.Equals(sourceClipId, StringComparison.Ordinal)) &&
                                  dispositions.All(item => item.PlanId == planId &&
                                      item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)),
            "One planning operation must bind one plan id, one source clip, and its own outputs.");
        var routeReferences = deliveries.Select(item => item.Route)
            .Concat(dispositions.Select(item => item.Route))
            .ToArray();
        RoutingValidation.Require(routeReferences.Length == 0 ||
                                  routeReferences.Select(item => item.RoutingGeneration)
                                      .Distinct().Count() == 1,
            "Every member of a routing plan must use one frozen routing generation.");
        RoutingValidation.Require(routeReferences.Select(item => (item.RouteId, item.ActionId))
                                      .Distinct().Count() == routeReferences.Length,
            "A route action can contribute at most one member to a routing plan.");
        RoutingValidation.Require(dispositions.Count <= 1,
            "A plan/source can have only one terminal file disposition.");
        if (dispositions.Count == 1)
        {
            var expectedDependencies = deliveries.Select(item => item.DeliveryId).ToHashSet();
            RoutingValidation.Require(
                dispositions[0].PrerequisiteDeliveryIds.ToHashSet().SetEquals(expectedDependencies),
                "A terminal file disposition must depend on every sibling delivery.");
        }
    }

    private static void ValidatePlanDecision(RoutingPlanDecision plan)
    {
        RoutingValidation.Require(plan.PlanId != Guid.Empty && plan.RoutingGeneration > 0,
            "A routing plan decision identity is invalid.");
        RoutingValidation.RequireOpaqueId(plan.SourceClipId, 256, "plan source clip id");
        RoutingValidation.RequireUtc(plan.CreatedUtc, "plan decision timestamp");
        var routeIds = plan.MatchedRouteIds ??
            throw new InvalidDataException("Plan matched-route evidence is missing.");
        var resolutions = plan.InitialMissingResolutions ??
            throw new InvalidDataException("Plan missing-output evidence is missing.");
        var authorizations = plan.LatentDuplicateAuthorizations ??
            throw new InvalidDataException("Plan duplicate authorization evidence is missing.");
        RoutingValidation.Require(routeIds.All(id => id != Guid.Empty) &&
                                  routeIds.Distinct().Count() == routeIds.Count,
            "Plan matched-route evidence is invalid.");
        var resolutionIds = new HashSet<Guid>();
        foreach (var resolution in resolutions)
        {
            if (resolution is null)
                throw new InvalidDataException("A plan missing-output resolution is missing.");
            RoutingValidation.Require(resolution.DeliveryId != Guid.Empty &&
                                      resolutionIds.Add(resolution.DeliveryId) &&
                                      Enum.IsDefined(resolution.Outcome) &&
                                      resolution.Outcome != RoutingMissingArtifactOutcome.RequestedOutput &&
                                      Enum.IsDefined(resolution.State),
                "Plan missing-output evidence is invalid.");
            ValidateOutputReference(resolution.RequestedOutput);
            ValidateOutputReference(resolution.EffectiveOutput);
            RoutingValidation.Require(
                resolution.RequestedOutput.ClipId.Equals(plan.SourceClipId, StringComparison.Ordinal) &&
                resolution.EffectiveOutput.ClipId.Equals(plan.SourceClipId, StringComparison.Ordinal),
                "Plan missing-output evidence belongs to another source clip.");
            RoutingValidation.RequireErrorCode(resolution.ErrorCode);
        }

        var authorizationIds = new HashSet<Guid>();
        var authorizationShapes = new HashSet<(RoutingDeliveryKey, Guid, Guid)>();
        foreach (var authorization in authorizations)
        {
            ValidateDuplicate(authorization);
            var first = authorization.FirstRouteId.CompareTo(authorization.SecondRouteId) <= 0
                ? authorization.FirstRouteId
                : authorization.SecondRouteId;
            var second = first == authorization.FirstRouteId
                ? authorization.SecondRouteId
                : authorization.FirstRouteId;
            RoutingValidation.Require(authorizationIds.Add(authorization.GroupId) &&
                                      authorizationShapes.Add((authorization.DeliveryKey, first, second)) &&
                                      authorization.DeliveryKey.OutputRef.ClipId.Equals(
                                          plan.SourceClipId, StringComparison.Ordinal) &&
                                      routeIds.Contains(authorization.FirstRouteId) &&
                                      routeIds.Contains(authorization.SecondRouteId),
                "Plan duplicate authorization evidence is invalid or ambiguous.");
        }
    }

    private static void ValidateImmediateMissingResolution(
        PlannedDelivery delivery,
        RoutingImmediateMissingResolution resolution)
    {
        ValidateResolvedInitialDelivery(delivery);
        RoutingValidation.Require(
            resolution.DeliveryId == delivery.DeliveryId &&
            resolution.RequestedOutput == delivery.RequestedOutput &&
            resolution.Outcome == delivery.ArtifactOutcome &&
            resolution.EffectiveOutput == delivery.Output &&
            resolution.State == delivery.State &&
            resolution.ErrorCode == delivery.ArtifactErrorCode,
            "Immediate missing-output evidence does not match its planned delivery.");
    }

    private static void ValidateNewDelivery(PlannedDelivery delivery)
    {
        if (delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput)
        {
            ValidateInitialDelivery(delivery);
            return;
        }
        ValidateResolvedInitialDelivery(delivery);
    }

    private static void ValidateResolvedInitialDelivery(PlannedDelivery delivery)
    {
        ValidateDelivery(delivery);
        RoutingValidation.Require(delivery.Attempts == 0 &&
                                  delivery.CurrentAttemptId is null &&
                                  delivery.ProviderResumeReference is null &&
                                  delivery.RemoteReceiptReference is null &&
                                  delivery.ErrorCode is null &&
                                  delivery.ApprovedUtc is null &&
                                  delivery.AttemptStartedUtc is null &&
                                  delivery.DuplicateRiskAcceptedUtc is null &&
                                  delivery.ArtifactErrorCode is not null &&
                                  delivery.CreatedUtc == delivery.UpdatedUtc,
            "A pre-resolved delivery contains attempt, receipt, or mutable history.");
        switch (delivery.ArtifactOutcome)
        {
            case RoutingMissingArtifactOutcome.OriginalFallback:
                RoutingValidation.Require(
                    delivery.OnMissingOutput == RoutingMissingOutputBehavior.UseOriginal &&
                    delivery.Output == delivery.OriginalOutput &&
                    delivery.CompletedUtc is null &&
                    delivery.State == (delivery.Mode == RoutingDeliveryMode.Approval
                        ? PlannedDeliveryState.WaitingForApproval
                        : PlannedDeliveryState.Ready),
                    "An initial original fallback is inconsistent with its route policy.");
                break;
            case RoutingMissingArtifactOutcome.Skipped:
                RoutingValidation.Require(
                    delivery.OnMissingOutput == RoutingMissingOutputBehavior.Skip &&
                    delivery.Output == delivery.RequestedOutput &&
                    delivery.State == PlannedDeliveryState.Skipped &&
                    delivery.CompletedUtc == delivery.UpdatedUtc,
                    "An initial skipped output is inconsistent with its route policy.");
                break;
            case RoutingMissingArtifactOutcome.NeedsAttention:
                RoutingValidation.Require(
                    delivery.OnMissingOutput == RoutingMissingOutputBehavior.NeedsAttention &&
                    delivery.Output == delivery.RequestedOutput &&
                    delivery.State == PlannedDeliveryState.NeedsAttention &&
                    delivery.CompletedUtc is null,
                    "An initial needs-attention output is inconsistent with its route policy.");
                break;
            case RoutingMissingArtifactOutcome.DuplicateSuppressed:
                RoutingValidation.Require(
                    delivery.State == PlannedDeliveryState.Skipped &&
                    delivery.CompletedUtc == delivery.UpdatedUtc &&
                    delivery.ArtifactErrorCode == "duplicate-fallback-suppressed",
                    "An initial duplicate-suppressed output is inconsistent.");
                break;
            default:
                throw new InvalidDataException(
                    "A pre-resolved delivery must retain a missing-output outcome.");
        }
    }

    private static void ValidateInitialDuplicateSuppression(
        IReadOnlyList<PlannedDelivery> deliveries)
    {
        foreach (var suppressed in deliveries.Where(item =>
                     item.ArtifactOutcome == RoutingMissingArtifactOutcome.DuplicateSuppressed))
        {
            RoutingValidation.Require(deliveries.Any(candidate =>
                    candidate.DeliveryId != suppressed.DeliveryId &&
                    candidate.DeliveryKey == suppressed.DeliveryKey &&
                    candidate.State != PlannedDeliveryState.Skipped &&
                    (candidate.Route.Priority < suppressed.Route.Priority ||
                     candidate.Route.Priority == suppressed.Route.Priority &&
                     candidate.Route.Order < suppressed.Route.Order)),
                "A duplicate-suppressed initial delivery has no surviving higher-precedence sibling.");
        }
    }

    private static void ValidateInitialDelivery(PlannedDelivery delivery)
    {
        ValidateDelivery(delivery);
        RoutingValidation.Require(delivery.State is PlannedDeliveryState.WaitingForArtifact or
                                  PlannedDeliveryState.WaitingForApproval or
                                  PlannedDeliveryState.Ready,
            "A new delivery has an invalid initial state.");
        RoutingValidation.Require(delivery.Attempts == 0 && delivery.CurrentAttemptId is null &&
                                  delivery.ProviderResumeReference is null &&
                                  delivery.RemoteReceiptReference is null &&
                                  delivery.ErrorCode is null &&
                                  delivery.ApprovedUtc is null &&
                                  delivery.AttemptStartedUtc is null &&
                                  delivery.CompletedUtc is null &&
                                  delivery.DuplicateRiskAcceptedUtc is null &&
                                  delivery.ArtifactOutcome ==
                                      RoutingMissingArtifactOutcome.RequestedOutput &&
                                  delivery.ArtifactErrorCode is null &&
                                  delivery.CreatedUtc == delivery.UpdatedUtc,
            "A new delivery contains attempt or completion state.");
    }

    private static void ValidateInitialDisposition(PlannedFileDisposition disposition)
    {
        ValidateDisposition(disposition);
        var expectedState = disposition.PrerequisiteDeliveryIds.Count == 0
            ? PlannedFileDispositionState.Ready
            : PlannedFileDispositionState.WaitingForDependencies;
        RoutingValidation.Require(disposition.State == expectedState &&
                                  disposition.Attempts == 0 &&
                                  disposition.CurrentAttemptId is null &&
                                  disposition.FinalLibraryItemReference is null &&
                                  disposition.ErrorCode is null &&
                                  disposition.AttemptStartedUtc is null &&
                                  disposition.CompletedUtc is null &&
                                  disposition.CreatedUtc == disposition.UpdatedUtc,
            "A new file disposition contains attempt or completion state.");
    }

    private static void ValidateDeliverySuccessor(PlannedDelivery previous, PlannedDelivery next)
    {
        RoutingValidation.Require(
            previous.DeliveryId == next.DeliveryId && previous.PlanId == next.PlanId &&
            previous.SourceClipId == next.SourceClipId && previous.Route == next.Route &&
            previous.Destination == next.Destination && previous.ConnectionId == next.ConnectionId &&
            previous.RequestedOutput == next.RequestedOutput &&
            previous.OriginalOutput == next.OriginalOutput &&
            previous.OnMissingOutput == next.OnMissingOutput && previous.Mode == next.Mode &&
            previous.Settings == next.Settings &&
            previous.CreatedUtc == next.CreatedUtc,
            "A delivery transition changed immutable plan data.");
        RoutingValidation.Require(next.UpdatedUtc >= previous.UpdatedUtc,
            "A delivery transition moved its timestamp backwards.");
        if (previous == next) return;
        if (previous with { IntentionalDuplicate = next.IntentionalDuplicate } == next)
        {
            return;
        }
        RoutingValidation.Require(IsLegalDeliveryStateTransition(previous.State, next.State),
            "A delivery state transition is invalid.");
        var resolvingMissing = previous.State == PlannedDeliveryState.WaitingForArtifact &&
                               previous.ArtifactOutcome ==
                                   RoutingMissingArtifactOutcome.RequestedOutput &&
                               next.ArtifactOutcome !=
                                   RoutingMissingArtifactOutcome.RequestedOutput;
        var recoveringNeedsAttention = previous.State == PlannedDeliveryState.NeedsAttention &&
                                       next.State is PlannedDeliveryState.WaitingForArtifact or
                                           PlannedDeliveryState.WaitingForApproval or
                                           PlannedDeliveryState.Ready or
                                           PlannedDeliveryState.Skipped;
        var duplicateSuppressed = next.ArtifactOutcome ==
                                  RoutingMissingArtifactOutcome.DuplicateSuppressed &&
                                  next.State == PlannedDeliveryState.Skipped &&
                                  next.ArtifactErrorCode == "duplicate-fallback-suppressed";
        RoutingValidation.Require(resolvingMissing
                ? next.ArtifactErrorCode is not null &&
                  (next.ArtifactOutcome == (next.OnMissingOutput switch
                  {
                      RoutingMissingOutputBehavior.UseOriginal =>
                          RoutingMissingArtifactOutcome.OriginalFallback,
                      RoutingMissingOutputBehavior.Skip => RoutingMissingArtifactOutcome.Skipped,
                      RoutingMissingOutputBehavior.NeedsAttention =>
                          RoutingMissingArtifactOutcome.NeedsAttention,
                      _ => throw new InvalidDataException(
                          "The missing-output behavior is unsupported.")
                  }) || duplicateSuppressed)
                : recoveringNeedsAttention
                    ? next.ArtifactOutcome switch
                    {
                        RoutingMissingArtifactOutcome.OriginalFallback =>
                            next.Output == next.OriginalOutput &&
                            next.ArtifactErrorCode == previous.ArtifactErrorCode,
                        RoutingMissingArtifactOutcome.Skipped =>
                            next.ArtifactErrorCode == previous.ArtifactErrorCode,
                        RoutingMissingArtifactOutcome.RequestedOutput =>
                            next.Output == next.RequestedOutput &&
                            next.ArtifactErrorCode is null,
                        RoutingMissingArtifactOutcome.DuplicateSuppressed =>
                            duplicateSuppressed,
                        _ => false
                    }
                    : duplicateSuppressed ||
                      previous.ArtifactOutcome == next.ArtifactOutcome &&
                      previous.ArtifactErrorCode == next.ArtifactErrorCode &&
                      previous.Output == next.Output,
            "A delivery transition changed its durable artifact resolution incorrectly.");
        var starting = previous.State == PlannedDeliveryState.Ready &&
                       next.State == PlannedDeliveryState.Sending;
        RoutingValidation.Require(next.Attempts == (starting
                ? checked(previous.Attempts + 1)
                : previous.Attempts),
            "Only starting a provider request may increment the delivery attempt count.");
        RoutingValidation.Require(
            previous.ApprovedUtc == next.ApprovedUtc ||
            previous.State == PlannedDeliveryState.WaitingForApproval &&
            next.State == PlannedDeliveryState.Ready && previous.ApprovedUtc is null &&
            next.ApprovedUtc == next.UpdatedUtc,
            "A delivery transition changed approval evidence incorrectly.");
        RoutingValidation.Require(
            previous.DuplicateRiskAcceptedUtc == next.DuplicateRiskAcceptedUtc ||
            previous.State == PlannedDeliveryState.DeliveryUnknown &&
            next.State == PlannedDeliveryState.Ready &&
            previous.DuplicateRiskAcceptedUtc is null &&
            next.DuplicateRiskAcceptedUtc == next.UpdatedUtc,
            "A delivery transition changed duplicate-risk evidence incorrectly.");
        if (starting)
        {
            RoutingValidation.Require(next.CurrentAttemptId is not null &&
                                      next.CurrentAttemptId != previous.CurrentAttemptId &&
                                      next.AttemptStartedUtc == next.UpdatedUtc,
                "Starting delivery requires a fresh fenced attempt.");
        }
        else if (next.State == PlannedDeliveryState.Ready &&
                 previous.State is PlannedDeliveryState.Failed or
                     PlannedDeliveryState.DeliveryUnknown)
        {
            RoutingValidation.Require(next.CurrentAttemptId is null &&
                                      next.AttemptStartedUtc is null,
                "Preparing a retry must clear the prior attempt fence.");
        }
        else
        {
            RoutingValidation.Require(previous.CurrentAttemptId == next.CurrentAttemptId &&
                                      previous.AttemptStartedUtc == next.AttemptStartedUtc,
                "A delivery transition changed its attempt fence unexpectedly.");
        }

        var completing = next.State == PlannedDeliveryState.Delivered;
        var skipping = previous.State != PlannedDeliveryState.Skipped &&
                       next.State == PlannedDeliveryState.Skipped;
        RoutingValidation.Require(completing
                ? next.RemoteReceiptReference is not null &&
                  next.CompletedUtc == next.UpdatedUtc && next.ErrorCode is null &&
                  next.ProviderResumeReference is null
                : skipping
                    ? next.RemoteReceiptReference is null &&
                      next.CompletedUtc == next.UpdatedUtc
                : next.RemoteReceiptReference == previous.RemoteReceiptReference &&
                  next.CompletedUtc == previous.CompletedUtc,
            "A delivery transition changed completion evidence incorrectly.");
        var failing = previous.State == PlannedDeliveryState.Sending &&
                      next.State is PlannedDeliveryState.Failed or
                          PlannedDeliveryState.DeliveryUnknown ||
                      previous.State == PlannedDeliveryState.Ready &&
                      next.State == PlannedDeliveryState.Failed &&
                      next.Attempts == previous.Attempts;
        var clearingFailure = previous.State is PlannedDeliveryState.Failed or
                                  PlannedDeliveryState.DeliveryUnknown &&
                              next.State is PlannedDeliveryState.Ready or
                                  PlannedDeliveryState.Delivered;
        RoutingValidation.Require(failing
                ? next.ErrorCode is not null
                : clearingFailure
                    ? next.ErrorCode is null
                    : next.ErrorCode == previous.ErrorCode,
            "A delivery transition changed failure evidence incorrectly.");
        RoutingValidation.Require(
            next.ProviderResumeReference == previous.ProviderResumeReference ||
            previous.State == PlannedDeliveryState.Sending &&
            next.State == PlannedDeliveryState.Failed || completing,
            "A delivery transition changed provider resume state unexpectedly.");
        if (next.State == PlannedDeliveryState.Ready &&
            previous.State == PlannedDeliveryState.DeliveryUnknown)
        {
            RoutingValidation.Require(next.DuplicateRiskAcceptedUtc is not null &&
                                      next.DuplicateRiskAcceptedUtc >= previous.UpdatedUtc,
                "Sending an unknown delivery again requires explicit duplicate-risk confirmation.");
        }
    }

    private static void ValidateDispositionSuccessor(
        PlannedFileDisposition previous,
        PlannedFileDisposition next)
    {
        RoutingValidation.Require(
            previous.DispositionId == next.DispositionId && previous.PlanId == next.PlanId &&
            previous.SourceClipId == next.SourceClipId &&
            previous.SourceContentSha256 == next.SourceContentSha256 &&
            previous.Route == next.Route &&
            previous.LibraryArea == next.LibraryArea &&
            previous.PrerequisiteDeliveryIds.SequenceEqual(next.PrerequisiteDeliveryIds) &&
            previous.CreatedUtc == next.CreatedUtc,
            "A file disposition transition changed immutable plan data.");
        RoutingValidation.Require(next.UpdatedUtc >= previous.UpdatedUtc,
            "A file disposition transition moved its timestamp backwards.");
        if (previous == next) return;
        RoutingValidation.Require(IsLegalDispositionStateTransition(previous.State, next.State),
            "A file disposition state transition is invalid.");
        var starting = previous.State == PlannedFileDispositionState.Ready &&
                       next.State == PlannedFileDispositionState.Moving;
        RoutingValidation.Require(next.Attempts == (starting
                ? checked(previous.Attempts + 1)
                : previous.Attempts),
            "Only starting a file move may increment its attempt count.");
        if (starting)
        {
            RoutingValidation.Require(next.CurrentAttemptId is not null &&
                                      next.CurrentAttemptId != previous.CurrentAttemptId &&
                                      next.AttemptStartedUtc == next.UpdatedUtc,
                "Starting a file move requires a fresh fenced attempt.");
        }
        else if (next.State == PlannedFileDispositionState.Ready &&
                 previous.State is PlannedFileDispositionState.Failed or
                     PlannedFileDispositionState.RecoveryPending)
        {
            RoutingValidation.Require(next.CurrentAttemptId is null &&
                                      next.AttemptStartedUtc is null,
                "Retrying a file move must clear the prior attempt fence.");
        }
        else
        {
            RoutingValidation.Require(previous.CurrentAttemptId == next.CurrentAttemptId &&
                                      previous.AttemptStartedUtc == next.AttemptStartedUtc,
                "A file disposition transition changed its attempt fence unexpectedly.");
        }

        var completing = next.State == PlannedFileDispositionState.Completed;
        RoutingValidation.Require(completing
                ? next.FinalLibraryItemReference is not null &&
                  next.CompletedUtc == next.UpdatedUtc && next.ErrorCode is null
                : next.FinalLibraryItemReference == previous.FinalLibraryItemReference &&
                  next.CompletedUtc == previous.CompletedUtc,
            "A file disposition transition changed completion evidence incorrectly.");
        var failing = previous.State == PlannedFileDispositionState.Moving &&
                      next.State is PlannedFileDispositionState.Failed or
                          PlannedFileDispositionState.RecoveryPending;
        var clearingFailure = previous.State is PlannedFileDispositionState.Failed or
                                  PlannedFileDispositionState.RecoveryPending &&
                              next.State is PlannedFileDispositionState.Ready or
                                  PlannedFileDispositionState.Completed;
        RoutingValidation.Require(failing
                ? next.ErrorCode is not null
                : clearingFailure
                    ? next.ErrorCode is null
                    : next.ErrorCode == previous.ErrorCode,
            "A file disposition transition changed failure evidence incorrectly.");
    }

    private static bool IsLegalDeliveryStateTransition(
        PlannedDeliveryState previous,
        PlannedDeliveryState next) => previous switch
        {
            PlannedDeliveryState.WaitingForArtifact => next is PlannedDeliveryState.WaitingForApproval or
                PlannedDeliveryState.Ready or PlannedDeliveryState.NeedsAttention or
                PlannedDeliveryState.Skipped or PlannedDeliveryState.Cancelled or
                PlannedDeliveryState.Expired,
            PlannedDeliveryState.WaitingForApproval => next is PlannedDeliveryState.Ready or
                PlannedDeliveryState.Skipped or PlannedDeliveryState.Cancelled or
                PlannedDeliveryState.Expired,
            PlannedDeliveryState.Ready => next is PlannedDeliveryState.Sending or
                PlannedDeliveryState.Failed or
                PlannedDeliveryState.Skipped or PlannedDeliveryState.Cancelled or
                PlannedDeliveryState.Expired,
            PlannedDeliveryState.Sending => next is PlannedDeliveryState.Delivered or
                PlannedDeliveryState.Failed or PlannedDeliveryState.DeliveryUnknown,
            PlannedDeliveryState.Failed => next is PlannedDeliveryState.Ready or
                PlannedDeliveryState.Cancelled or PlannedDeliveryState.Expired,
            PlannedDeliveryState.DeliveryUnknown => next is PlannedDeliveryState.Delivered or
                PlannedDeliveryState.Ready or PlannedDeliveryState.Cancelled,
            PlannedDeliveryState.NeedsAttention => next is PlannedDeliveryState.WaitingForArtifact or
                PlannedDeliveryState.WaitingForApproval or PlannedDeliveryState.Ready or
                PlannedDeliveryState.Skipped,
            _ => false
        };

    private static bool IsLegalDispositionStateTransition(
        PlannedFileDispositionState previous,
        PlannedFileDispositionState next) => previous switch
        {
            PlannedFileDispositionState.WaitingForDependencies => next is PlannedFileDispositionState.Ready or
                PlannedFileDispositionState.Cancelled,
            PlannedFileDispositionState.Ready => next is PlannedFileDispositionState.Moving or
                PlannedFileDispositionState.Cancelled,
            PlannedFileDispositionState.Moving => next is PlannedFileDispositionState.Completed or
                PlannedFileDispositionState.Failed or PlannedFileDispositionState.RecoveryPending,
            PlannedFileDispositionState.RecoveryPending => next is PlannedFileDispositionState.Ready or
                PlannedFileDispositionState.Completed or PlannedFileDispositionState.Failed or
                PlannedFileDispositionState.Cancelled,
            PlannedFileDispositionState.Failed => next is PlannedFileDispositionState.Ready or
                PlannedFileDispositionState.Cancelled,
            _ => false
        };

    private static void ValidateDuplicateProvenance(IReadOnlyList<PlannedDelivery> deliveries)
    {
        RoutingValidation.Require(deliveries.Where(item => !IsDuplicateRelevant(item))
                                      .All(item => item.IntentionalDuplicate is null),
            "A non-deliverable item cannot retain duplicate-delivery authorization.");
        foreach (var keyedGroup in deliveries.Where(IsDuplicateRelevant)
                     .GroupBy(delivery => delivery.DeliveryKey))
        {
            var members = keyedGroup.ToArray();
            if (members.Length == 1)
            {
                RoutingValidation.Require(members[0].IntentionalDuplicate is null,
                    "A non-duplicate delivery cannot claim duplicate-group provenance.");
                continue;
            }

            RoutingValidation.Require(members.Length == 2,
                "Deliver twice authorizes exactly two deliveries, never a larger duplicate group.");
            var firstProof = members[0].IntentionalDuplicate;
            var secondProof = members[1].IntentionalDuplicate;
            RoutingValidation.Require(firstProof is not null && secondProof is not null &&
                                      firstProof == secondProof,
                "Duplicate deliveries require the same durable user-confirmed proof.");
            ValidateDuplicate(firstProof!);
            var actualRoutes = members.Select(member => member.Route.RouteId).ToHashSet();
            var approvedRoutes = new HashSet<Guid>
            {
                firstProof!.FirstRouteId,
                firstProof.SecondRouteId
            };
            RoutingValidation.Require(firstProof.DeliveryKey == keyedGroup.Key &&
                                      members[0].PlanId == members[1].PlanId &&
                                      members[0].SourceClipId == members[1].SourceClipId &&
                                      actualRoutes.SetEquals(approvedRoutes),
                "Duplicate proof is not bound to this exact key, plan, source, and route pair.");
        }
    }

    private static void ValidateDuplicate(IntentionalDuplicateProvenance provenance)
    {
        RoutingValidation.Require(provenance.GroupId != Guid.Empty &&
                                  provenance.Decision ==
                                  IntentionalDuplicateDecision.UserConfirmedDeliverTwice &&
                                  provenance.DeliveryKey is not null &&
                                  provenance.FirstRouteId != Guid.Empty &&
                                  provenance.SecondRouteId != Guid.Empty &&
                                  provenance.FirstRouteId != provenance.SecondRouteId,
            "Intentional duplicate provenance must identify one exact route pair and key.");
        var deliveryKey = provenance.DeliveryKey ??
            throw new InvalidDataException("Intentional duplicate provenance is missing its key.");
        RoutingValidation.RequireOpaqueId(
            deliveryKey.ConnectionId, 128, "duplicate connection id");
        ValidateOutputReference(deliveryKey.OutputRef);
        RoutingValidation.RequireUtc(provenance.ConfirmedUtc,
            "intentional duplicate confirmation timestamp");
    }

    private static void ValidateRouteReference(RoutingRouteSnapshotReference route)
    {
        if (route is null) throw new InvalidDataException("A delivery route snapshot reference is missing.");
        RoutingValidation.Require(route.RoutingGeneration > 0 && route.RouteId != Guid.Empty &&
                                  route.RouteRevision > 0 && route.ActionId != Guid.Empty &&
                                  route.Priority >= 0 && route.Order >= 0,
            "A delivery route snapshot reference is invalid.");
        RoutingValidation.RequireText(route.RouteName, 1, 80, "route snapshot name");
    }

    private static void ValidateOutputReference(RoutingOutputReference output)
    {
        if (output is null) throw new InvalidDataException("A routing output reference is missing.");
        RoutingValidation.RequireOpaqueId(output.ClipId, 256, "output clip id");
        RoutingValidation.Require(Enum.IsDefined(output.Kind),
            "A routing output kind is unsupported.");
        RoutingValidation.RequireSha256(output.Revision, "output revision");
        RoutingValidation.Require(output.Revision.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The output revision must use canonical lowercase SHA-256 text.");
    }

    private static void RequireCurrentAttempt(PlannedDelivery delivery, Guid attemptId)
    {
        RoutingValidation.Require(delivery.State == PlannedDeliveryState.Sending &&
                                  attemptId != Guid.Empty &&
                                  delivery.CurrentAttemptId == attemptId,
            "The delivery completion belongs to an abandoned attempt.");
    }

    private static void RequireCurrentAttempt(PlannedFileDisposition disposition, Guid attemptId)
    {
        RoutingValidation.Require(disposition.State == PlannedFileDispositionState.Moving &&
                                  attemptId != Guid.Empty &&
                                  disposition.CurrentAttemptId == attemptId,
            "The file disposition completion belongs to an abandoned attempt.");
    }
}

internal static class RoutingValidation
{
    internal static DateTimeOffset Utc(DateTimeOffset timestamp) => timestamp.ToUniversalTime();

    internal static long NextGeneration(long generation)
    {
        Require(generation > 0 && generation < long.MaxValue,
            "The routing generation cannot advance.");
        return generation + 1;
    }

    internal static void ValidateDeliverySettings(RoutingDeliverySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Require(Enum.IsDefined(settings.Visibility),
            "The delivery visibility is unsupported.");
        RequireOptionalText(settings.Message, 2_000, "delivery message");
        RequireOptionalText(settings.Title, 200, "delivery title");
        RequireOptionalText(settings.Caption, 10_000, "delivery caption");
    }

    internal static void RequireText(string? value, int minimum, int maximum, string description)
    {
        Require(value is not null && value.Length >= minimum && value.Length <= maximum &&
                !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0,
            $"The {description} is invalid.");
    }

    internal static void RequireOptionalText(string? value, int maximum, string description)
    {
        if (value is null) return;
        Require(value.Length <= maximum && value.IndexOf('\0') < 0,
            $"The {description} is invalid.");
    }

    internal static void RequireOpaqueId(string? value, int maximum, string description)
    {
        RequireText(value, 1, maximum, description);
        Require(value!.All(character => char.IsAsciiLetterOrDigit(character) ||
                                           character is '.' or '_' or ':' or '-'),
            $"The {description} is not an opaque identifier.");
    }

    internal static void RequireOptionalOpaqueId(string? value, int maximum, string description)
    {
        if (value is null) return;
        RequireOpaqueId(value, maximum, description);
    }

    internal static bool IsNamedEnumValue<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Any(name =>
            name.Equals(value, StringComparison.OrdinalIgnoreCase));

    internal static void RequireSha256(string? value, string description)
    {
        Require(value is { Length: 64 } && value.All(Uri.IsHexDigit),
            $"The {description} is not a SHA-256 value.");
    }

    internal static void RequireErrorCode(string value)
    {
        RequireOpaqueId(value, 64, "routing error code");
    }

    internal static void RequireUtc(DateTimeOffset value, string description)
    {
        Require(value.Offset == TimeSpan.Zero, $"The {description} must be UTC.");
    }

    internal static void RequireOptionalUtc(DateTimeOffset? value, string description)
    {
        if (value is not null) RequireUtc(value.Value, description);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
