using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum RoutingEvaluationEventKind
{
    SourceArrival,
    DerivedRendition,
    FileDisposition
}

internal enum RoutingOutputAvailability
{
    Pending,
    Ready,
    PermanentlyMissing
}

/// <summary>
/// A logical output revision is immutable plan identity, not a filesystem path. It may exist
/// before bytes are ready. PermanentlyMissing requires a safe error code; Pending and Ready do not.
/// </summary>
internal sealed record RoutingClipOutputRevision(
    RoutingOutputReference Output,
    RoutingOutputAvailability Availability,
    string? FailureCode);

/// <summary>
/// Immutable facts for one source-arrival evaluation. Derived renditions and library operations are
/// represented explicitly so they can be rejected as routing inputs instead of feeding back into
/// Any new source clip.
/// </summary>
internal sealed record RoutingClipFacts(
    string ClipId,
    RoutingEvaluationEventKind EventKind,
    RoutingClipSource ClipSource,
    RoutingTriggerKind ArrivalTrigger,
    RoutingCaptureType? CaptureType,
    string Game,
    bool ReactionCamera,
    long DurationMilliseconds,
    string SourceContentSha256,
    IReadOnlyList<RoutingClipOutputRevision> Outputs);

/// <summary>
/// A frozen, persistence-ready proposal. If RequiresAtomicResolvedAppend is true, one or more
/// permanently-missing outputs were resolved in this proposal. Those final delivery states must be
/// appended by the outbox in the same generation/save as the rest of the plan.
/// </summary>
internal sealed record RoutingPlanProposal(
    Guid PlanId,
    string SourceClipId,
    long RoutingGeneration,
    IReadOnlyList<Guid> MatchedRouteIds,
    IReadOnlyList<PlannedDelivery> Deliveries,
    PlannedFileDisposition? FileDisposition,
    IReadOnlyList<RoutingImmediateMissingResolution> ImmediateMissingResolutions,
    IReadOnlyList<IntentionalDuplicateProvenance> LatentDuplicateAuthorizations,
    bool RequiresAtomicResolvedAppend)
{
    internal bool HasWork => Deliveries.Count > 0 || FileDisposition is not null;
}

/// <summary>
/// Pure deterministic route evaluation. It performs no I/O and is not wired into capture, watcher,
/// upload, or UI runtime paths.
/// </summary>
internal static class RoutingEvaluator
{
    private const string UnproducedOutputError = "output-not-produced";

    internal static IReadOnlyList<Guid> GetOrderedMatchedRouteIds(
        RoutingSnapshotDocument snapshot,
        RoutingClipFacts facts)
    {
        RoutingSnapshotModel.Validate(snapshot);
        ValidateFacts(facts);
        return FindMatchedRoutes(snapshot, facts)
            .Select(item => item.Route.RouteId)
            .ToArray();
    }

    internal static RoutingPlanProposal CreatePlan(
        RoutingSnapshotDocument snapshot,
        RoutingClipFacts facts,
        Guid planId,
        IReadOnlyList<IntentionalDuplicateProvenance> deliberateDuplicateAuthorizations,
        DateTimeOffset now)
    {
        RoutingSnapshotModel.Validate(snapshot);
        ValidateFacts(facts);
        RoutingValidation.Require(planId != Guid.Empty, "A routing plan id is missing.");
        ArgumentNullException.ThrowIfNull(deliberateDuplicateAuthorizations);
        ValidateDuplicateAuthorizations(deliberateDuplicateAuthorizations, facts.ClipId);

        if (facts.EventKind != RoutingEvaluationEventKind.SourceArrival)
        {
            return Empty(planId, facts.ClipId, snapshot.Generation);
        }

        var matched = FindMatchedRoutes(snapshot, facts);

        if (matched.Length == 0)
        {
            return Empty(planId, facts.ClipId, snapshot.Generation);
        }

        var matchedRouteIds = matched.Select(item => item.Route.RouteId).ToArray();
        var matchedRouteIdSet = matchedRouteIds.ToHashSet();
        var latentAuthorizations = deliberateDuplicateAuthorizations
            .Where(proof => matchedRouteIdSet.Contains(proof.FirstRouteId) &&
                            matchedRouteIdSet.Contains(proof.SecondRouteId))
            .Select(Clone)
            .ToArray();

        var outputs = BuildOutputMap(facts);
        var deliveryCandidates = new List<DeliveryCandidate>();
        var dispositionCandidates = new List<DispositionCandidate>();
        var globalActionOrder = 0;
        foreach (var orderedRoute in matched)
        {
            for (var actionIndex = 0; actionIndex < orderedRoute.Route.Actions.Count; actionIndex++)
            {
                var action = orderedRoute.Route.Actions[actionIndex];
                var actionOrder = globalActionOrder++;
                if (!action.Enabled) continue;

                var routeReference = FreezeRouteReference(
                    snapshot.Generation, orderedRoute.Route, action.ActionId, actionOrder);
                if (action.Kind == RoutingActionKind.FileIntoLibrary)
                {
                    dispositionCandidates.Add(new DispositionCandidate(
                        orderedRoute, actionIndex, routeReference, action));
                    continue;
                }

                var outputKind = action.OutputRef ??
                    throw new InvalidDataException("A delivery action has no logical output reference.");
                var output = outputs[outputKind];
                deliveryCandidates.Add(new DeliveryCandidate(
                    orderedRoute,
                    actionIndex,
                    routeReference,
                    action,
                    output,
                    outputs[RoutingOutputKind.Original].Output));
            }
        }

        var selectedCandidates = SelectDuplicateWinners(
            deliveryCandidates, deliberateDuplicateAuthorizations);
        var initialDeliveries = selectedCandidates
            .Select(candidate => CreateInitialDelivery(candidate, planId, facts.ClipId, now))
            .ToArray();

        var selectedDisposition = dispositionCandidates
            .OrderBy(item => item.Route.Route.Priority)
            .ThenBy(item => item.Route.ListIndex)
            .ThenBy(item => item.ActionIndex)
            .FirstOrDefault();
        if (selectedDisposition is not null &&
            selectedDisposition.Action.LibraryArea == RoutingLibraryArea.LocalOnly &&
            initialDeliveries.Length > 0)
        {
            throw new InvalidDataException(
                "The highest-precedence Local-only disposition conflicts with external deliveries.");
        }
        if (selectedDisposition is not null &&
            selectedDisposition.Action.LibraryArea == RoutingLibraryArea.Uploaded &&
            initialDeliveries.Length == 0)
        {
            throw new InvalidDataException(
                "An Uploaded disposition requires at least one planned external delivery.");
        }

        var disposition = selectedDisposition is null
            ? null
            : RoutingOutboxModel.CreateFileDisposition(
                StableId(planId, selectedDisposition.Route.Route.RouteId,
                    selectedDisposition.Action.ActionId, "file-disposition"),
                planId,
                facts.ClipId,
                facts.SourceContentSha256,
                selectedDisposition.RouteReference,
                selectedDisposition.Action.LibraryArea!.Value,
                initialDeliveries.Select(item => item.DeliveryId).ToArray(),
                now);

        if (initialDeliveries.Length == 0 && disposition is null)
        {
            return new RoutingPlanProposal(
                planId,
                facts.ClipId,
                snapshot.Generation,
                matchedRouteIds,
                [],
                null,
                [],
                latentAuthorizations,
                RequiresAtomicResolvedAppend: false);
        }

        // Exercise the existing outbox creation and transition APIs so the proposal has exactly the
        // same state shapes as persisted work. The returned records remain a pure delta; this
        // temporary document is never saved.
        var provisional = new RoutingPlanProposal(
            planId,
            facts.ClipId,
            snapshot.Generation,
            matchedRouteIds,
            initialDeliveries,
            disposition,
            [],
            latentAuthorizations,
            RequiresAtomicResolvedAppend: false);
        var temporary = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(now),
            provisional,
            now);
        var missingGroups = selectedCandidates
            .Where(item => item.Output.Availability == RoutingOutputAvailability.PermanentlyMissing)
            .GroupBy(item => item.Output.FailureCode!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var missingGroup in missingGroups)
        {
            temporary = RoutingOutboxModel.ResolveMissingArtifacts(
                temporary,
                planId,
                missingGroup.Select(item => item.Output.Output).Distinct().ToArray(),
                missingGroup.Key,
                now);
        }

        var deliveries = temporary.Deliveries
            .Where(item => item.PlanId == planId)
            .OrderBy(item => item.Route.Priority)
            .ThenBy(item => item.Route.Order)
            .ToArray();
        var finalDisposition = temporary.FileDispositions
            .SingleOrDefault(item => item.PlanId == planId);
        var initialById = initialDeliveries.ToDictionary(item => item.DeliveryId);
        var resolutions = deliveries
            .Where(item =>
            {
                var initial = initialById[item.DeliveryId];
                return item.Output != initial.Output ||
                       item.ArtifactOutcome != initial.ArtifactOutcome ||
                       item.ArtifactErrorCode != initial.ArtifactErrorCode ||
                       item.State != initial.State ||
                       item.CompletedUtc != initial.CompletedUtc;
            })
            .Select(item => new RoutingImmediateMissingResolution(
                item.DeliveryId,
                item.RequestedOutput,
                item.ArtifactOutcome,
                item.Output,
                item.State,
                item.ArtifactErrorCode ?? throw new InvalidDataException(
                    "An immediate missing-output resolution lost its error code.")))
            .ToArray();

        return new RoutingPlanProposal(
            planId,
            facts.ClipId,
            snapshot.Generation,
            matchedRouteIds,
            deliveries,
            finalDisposition,
            resolutions,
            latentAuthorizations,
            RequiresAtomicResolvedAppend: resolutions.Length > 0);
    }

    internal static RoutingOutputReference CreateLogicalOutputReference(
        string clipId,
        string originalRevision,
        RoutingOutputKind kind)
    {
        RoutingValidation.RequireOpaqueId(clipId, 256, "source clip id");
        RoutingValidation.RequireSha256(originalRevision, "original output revision");
        RoutingValidation.Require(Enum.IsDefined(kind), "A routing output kind is unsupported.");
        if (kind == RoutingOutputKind.Original)
        {
            return new RoutingOutputReference(clipId, kind, originalRevision.ToLowerInvariant());
        }

        var input = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture,
                $"clipcord-routing-output-v1\n{clipId}\n{originalRevision.ToLowerInvariant()}\n{kind}"));
        return new RoutingOutputReference(
            clipId,
            kind,
            Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
    }

    private static RoutingPlanProposal Empty(Guid planId, string clipId, long generation) =>
        new(planId, clipId, generation, [], [], null, [], [], RequiresAtomicResolvedAppend: false);

    private static IntentionalDuplicateProvenance Clone(
        IntentionalDuplicateProvenance proof) => proof with
    {
        DeliveryKey = new RoutingDeliveryKey(
            string.Concat(proof.DeliveryKey.ConnectionId),
            Clone(proof.DeliveryKey.OutputRef))
    };

    private static Dictionary<RoutingOutputKind, RoutingClipOutputRevision> BuildOutputMap(
        RoutingClipFacts facts)
    {
        var result = facts.Outputs.ToDictionary(item => item.Output.Kind);
        var original = result[RoutingOutputKind.Original].Output;
        foreach (var kind in Enum.GetValues<RoutingOutputKind>())
        {
            if (result.ContainsKey(kind)) continue;
            result.Add(kind, new RoutingClipOutputRevision(
                CreateLogicalOutputReference(facts.ClipId, original.Revision, kind),
                RoutingOutputAvailability.PermanentlyMissing,
                UnproducedOutputError));
        }
        return result;
    }

    private static PlannedDelivery CreateInitialDelivery(
        DeliveryCandidate candidate,
        Guid planId,
        string clipId,
        DateTimeOffset now)
    {
        var action = candidate.Action;
        var settings = action.DeliverySettings ??
            throw new InvalidDataException("A delivery action has no frozen settings.");
        return RoutingOutboxModel.CreateDelivery(
            StableId(planId, candidate.Route.Route.RouteId, action.ActionId, "delivery"),
            planId,
            clipId,
            candidate.RouteReference,
            action.Destination!.Value,
            action.ConnectionId!,
            Clone(candidate.Output.Output),
            Clone(candidate.OriginalOutput),
            action.OnMissingOutput!.Value,
            action.Mode,
            settings with { },
            artifactReady: candidate.Output.Availability == RoutingOutputAvailability.Ready,
            candidate.Authorization is null ? null : candidate.Authorization with
            {
                DeliveryKey = new RoutingDeliveryKey(
                    candidate.Authorization.DeliveryKey.ConnectionId,
                    Clone(candidate.Authorization.DeliveryKey.OutputRef))
            },
            now);
    }

    private static DeliveryCandidate[] SelectDuplicateWinners(
        IReadOnlyList<DeliveryCandidate> candidates,
        IReadOnlyList<IntentionalDuplicateProvenance> authorizations)
    {
        var result = new List<DeliveryCandidate>();
        foreach (var group in candidates
                     .GroupBy(item => item.DeliveryKey)
                     .OrderBy(item => item.Min(candidate => candidate.Route.Route.Priority))
                     .ThenBy(item => item.Min(candidate => candidate.RouteReference.Order)))
        {
            var members = group
                .OrderBy(item => item.Route.Route.Priority)
                .ThenBy(item => item.Route.ListIndex)
                .ThenBy(item => item.ActionIndex)
                .ToArray();
            if (members.Length == 1)
            {
                result.Add(members[0]);
                continue;
            }

            if (members.Length == 2 &&
                members[0].Route.Route.RouteId != members[1].Route.Route.RouteId)
            {
                var routeIds = new HashSet<Guid>
                {
                    members[0].Route.Route.RouteId,
                    members[1].Route.Route.RouteId
                };
                var proofs = authorizations.Where(proof =>
                    proof.DeliveryKey == group.Key &&
                    routeIds.SetEquals([proof.FirstRouteId, proof.SecondRouteId])).ToArray();
                RoutingValidation.Require(proofs.Length <= 1,
                    "Duplicate delivery authorization is ambiguous.");
                if (proofs.Length == 1)
                {
                    result.Add(members[0] with { Authorization = proofs[0] });
                    result.Add(members[1] with { Authorization = proofs[0] });
                    continue;
                }
            }

            result.Add(members[0]);
        }
        return result
            .OrderBy(item => item.Route.Route.Priority)
            .ThenBy(item => item.Route.ListIndex)
            .ThenBy(item => item.ActionIndex)
            .ToArray();
    }

    private static bool TriggerMatches(RoutingTriggerKind routeTrigger, RoutingTriggerKind arrival) =>
        routeTrigger == RoutingTriggerKind.AnyNewSourceClip || routeTrigger == arrival;

    private static OrderedRoute[] FindMatchedRoutes(
        RoutingSnapshotDocument snapshot,
        RoutingClipFacts facts)
    {
        if (facts.EventKind != RoutingEvaluationEventKind.SourceArrival) return [];
        var orderedRoutes = snapshot.Routes
            .Select((route, listIndex) => new OrderedRoute(route, listIndex))
            .OrderBy(item => item.Route.Priority)
            .ThenBy(item => item.ListIndex)
            .ToArray();
        var specificMatches = orderedRoutes
            .Where(item => item.Route.Enabled && item.Route.Kind == RoutingRouteKind.Specific)
            .Where(item => TriggerMatches(item.Route.Trigger, facts.ArrivalTrigger))
            .Where(item => item.Route.Conditions.All(condition => ConditionMatches(condition, facts)))
            .ToArray();
        return specificMatches.Length > 0
            ? specificMatches
            : orderedRoutes
                .Where(item => item.Route.Enabled && item.Route.Kind == RoutingRouteKind.Fallback)
                .Take(1)
                .ToArray();
    }

    private static bool ConditionMatches(RoutingCondition condition, RoutingClipFacts facts) =>
        condition.Field switch
        {
            RoutingConditionField.Game => CompareText(
                facts.Game, condition.Operator, condition.Value),
            RoutingConditionField.ClipSource => CompareEnum(
                facts.ClipSource, condition.Operator, condition.Value),
            RoutingConditionField.ReactionCamera => CompareBoolean(
                facts.ReactionCamera, condition.Operator, condition.Value),
            RoutingConditionField.CaptureType => CompareNullableEnum(
                facts.CaptureType, condition.Operator, condition.Value),
            RoutingConditionField.Duration => CompareDuration(
                facts.DurationMilliseconds, condition.Operator, condition.Value),
            _ => throw new InvalidDataException("A route condition kind is unsupported.")
        };

    private static bool CompareText(
        string actual,
        RoutingConditionOperator comparison,
        string expected) => comparison switch
        {
            RoutingConditionOperator.Equals =>
                actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            RoutingConditionOperator.DoesNotEqual =>
                !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            RoutingConditionOperator.Contains =>
                actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => throw new InvalidDataException("A text condition uses an unsupported operator.")
        };

    private static bool CompareEnum<TEnum>(
        TEnum actual,
        RoutingConditionOperator comparison,
        string expected)
        where TEnum : struct, Enum
    {
        var parsed = Enum.Parse<TEnum>(expected, ignoreCase: true);
        return comparison switch
        {
            RoutingConditionOperator.Equals => EqualityComparer<TEnum>.Default.Equals(actual, parsed),
            RoutingConditionOperator.DoesNotEqual => !EqualityComparer<TEnum>.Default.Equals(actual, parsed),
            _ => throw new InvalidDataException("An enum condition uses an unsupported operator.")
        };
    }

    private static bool CompareNullableEnum<TEnum>(
        TEnum? actual,
        RoutingConditionOperator comparison,
        string expected)
        where TEnum : struct, Enum
    {
        var parsed = Enum.Parse<TEnum>(expected, ignoreCase: true);
        return comparison switch
        {
            RoutingConditionOperator.Equals =>
                actual.HasValue && EqualityComparer<TEnum>.Default.Equals(actual.Value, parsed),
            RoutingConditionOperator.DoesNotEqual =>
                !actual.HasValue || !EqualityComparer<TEnum>.Default.Equals(actual.Value, parsed),
            _ => throw new InvalidDataException("An enum condition uses an unsupported operator.")
        };
    }

    private static bool CompareBoolean(
        bool actual,
        RoutingConditionOperator comparison,
        string expected)
    {
        var parsed = bool.Parse(expected);
        return comparison switch
        {
            RoutingConditionOperator.Equals => actual == parsed,
            RoutingConditionOperator.DoesNotEqual => actual != parsed,
            _ => throw new InvalidDataException("A boolean condition uses an unsupported operator.")
        };
    }

    private static bool CompareDuration(
        long actual,
        RoutingConditionOperator comparison,
        string expected)
    {
        var parsed = long.Parse(expected, NumberStyles.None, CultureInfo.InvariantCulture);
        return comparison switch
        {
            RoutingConditionOperator.GreaterThanOrEqual => actual >= parsed,
            RoutingConditionOperator.LessThanOrEqual => actual <= parsed,
            _ => throw new InvalidDataException("A duration condition uses an unsupported operator.")
        };
    }

    private static RoutingRouteSnapshotReference FreezeRouteReference(
        long generation,
        RoutingRoute route,
        Guid actionId,
        int order) => new(
            generation,
            route.RouteId,
            route.Revision,
            string.Concat(route.Name),
            actionId,
            route.Priority,
            order);

    private static RoutingOutputReference Clone(RoutingOutputReference output) =>
        new(string.Concat(output.ClipId), output.Kind, string.Concat(output.Revision));

    private static Guid StableId(Guid planId, Guid routeId, Guid actionId, string purpose)
    {
        var input = Encoding.UTF8.GetBytes(
            $"clipcord-routing-id-v1\n{planId:N}\n{routeId:N}\n{actionId:N}\n{purpose}");
        var bytes = SHA256.HashData(input);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void ValidateFacts(RoutingClipFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        RoutingValidation.RequireOpaqueId(facts.ClipId, 256, "source clip id");
        RoutingValidation.Require(Enum.IsDefined(facts.EventKind) &&
                                  Enum.IsDefined(facts.ClipSource) &&
                                  Enum.IsDefined(facts.ArrivalTrigger),
            "A routing clip event, source, or trigger is unsupported.");
        RoutingValidation.RequireText(facts.Game, 1, 256, "game name");
        RoutingValidation.Require(facts.DurationMilliseconds >= 0,
            "A routing clip duration is invalid.");
        RoutingValidation.RequireSha256(facts.SourceContentSha256, "source content hash");

        switch (facts.ClipSource)
        {
            case RoutingClipSource.ClipCordCapture:
                RoutingValidation.Require(facts.CaptureType is { } captureType &&
                                          Enum.IsDefined(captureType) &&
                                          facts.ArrivalTrigger == (captureType == RoutingCaptureType.InstantReplay
                                              ? RoutingTriggerKind.InstantReplay
                                              : RoutingTriggerKind.ManualRecording),
                    "A ClipCord capture has inconsistent capture and trigger facts.");
                break;
            case RoutingClipSource.WatchedFolder:
                RoutingValidation.Require(facts.CaptureType is null &&
                                          facts.ArrivalTrigger == RoutingTriggerKind.WatchedFolder,
                    "A watched-folder clip has inconsistent capture or trigger facts.");
                break;
            case RoutingClipSource.ManualImport:
                RoutingValidation.Require(facts.CaptureType is null &&
                                          facts.ArrivalTrigger == RoutingTriggerKind.AnyNewSourceClip,
                    "A manual import has inconsistent capture or trigger facts.");
                break;
            default:
                throw new InvalidDataException("A routing clip source is unsupported.");
        }

        var outputs = facts.Outputs ??
            throw new InvalidDataException("Routing clip outputs are missing.");
        RoutingValidation.Require(outputs.Count > 0 &&
                                  outputs.All(item => item is not null && item.Output is not null),
            "A routing clip output is missing.");
        RoutingValidation.Require(
            outputs.Select(item => item.Output.Kind).Distinct().Count() == outputs.Count,
            "Routing clip outputs must contain unique logical kinds.");
        foreach (var item in outputs)
        {
            if (item is null || item.Output is null)
                throw new InvalidDataException("A routing clip output is missing.");
            RoutingValidation.Require(Enum.IsDefined(item.Availability) &&
                                      Enum.IsDefined(item.Output.Kind),
                "A routing output availability or kind is unsupported.");
            RoutingValidation.Require(item.Output.ClipId.Equals(facts.ClipId, StringComparison.Ordinal),
                "A routing output belongs to a different source clip.");
            RoutingValidation.RequireSha256(item.Output.Revision, "logical output revision");
            RoutingValidation.Require(item.Output.Revision.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "A logical output revision must use canonical lowercase SHA-256 text.");
            if (item.Availability == RoutingOutputAvailability.PermanentlyMissing)
            {
                RoutingValidation.Require(item.Output.Kind != RoutingOutputKind.Original,
                    "A permanently missing source original cannot be routed.");
                RoutingValidation.Require(item.FailureCode is not null,
                    "A permanently missing output requires a failure code.");
                RoutingValidation.RequireErrorCode(item.FailureCode!);
            }
            else
            {
                RoutingValidation.Require(item.FailureCode is null,
                    "A ready or pending output cannot carry a failure code.");
            }
        }

        var original = outputs.SingleOrDefault(item => item.Output.Kind == RoutingOutputKind.Original)
            ?? throw new InvalidDataException("A committed source original is required for routing.");
        RoutingValidation.Require(original.Availability == RoutingOutputAvailability.Ready &&
                                  original.Output.Revision.Equals(
                                      facts.SourceContentSha256, StringComparison.OrdinalIgnoreCase),
            "The source original must be ready and match the source content hash.");
    }

    private static void ValidateDuplicateAuthorizations(
        IReadOnlyList<IntentionalDuplicateProvenance> authorizations,
        string clipId)
    {
        var groupIds = new HashSet<Guid>();
        foreach (var proof in authorizations)
        {
            if (proof is null || proof.DeliveryKey is null || proof.DeliveryKey.OutputRef is null)
                throw new InvalidDataException("A duplicate delivery authorization is incomplete.");
            RoutingValidation.Require(proof.GroupId != Guid.Empty && groupIds.Add(proof.GroupId) &&
                                      proof.Decision == IntentionalDuplicateDecision.UserConfirmedDeliverTwice &&
                                      proof.FirstRouteId != Guid.Empty && proof.SecondRouteId != Guid.Empty &&
                                      proof.FirstRouteId != proof.SecondRouteId,
                "A duplicate delivery authorization is invalid.");
            RoutingValidation.RequireOpaqueId(
                proof.DeliveryKey.ConnectionId, 128, "duplicate connection id");
            RoutingValidation.Require(proof.DeliveryKey.OutputRef.ClipId.Equals(
                                          clipId, StringComparison.Ordinal) &&
                                      Enum.IsDefined(proof.DeliveryKey.OutputRef.Kind),
                "A duplicate delivery authorization belongs to another clip or output kind.");
            RoutingValidation.RequireSha256(
                proof.DeliveryKey.OutputRef.Revision, "duplicate output revision");
            RoutingValidation.Require(proof.DeliveryKey.OutputRef.Revision.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "A duplicate output revision must use canonical lowercase SHA-256 text.");
            RoutingValidation.RequireUtc(
                proof.ConfirmedUtc, "intentional duplicate confirmation timestamp");
        }
    }

    private sealed record OrderedRoute(RoutingRoute Route, int ListIndex);

    private sealed record DispositionCandidate(
        OrderedRoute Route,
        int ActionIndex,
        RoutingRouteSnapshotReference RouteReference,
        RoutingAction Action);

    private sealed record DeliveryCandidate(
        OrderedRoute Route,
        int ActionIndex,
        RoutingRouteSnapshotReference RouteReference,
        RoutingAction Action,
        RoutingClipOutputRevision Output,
        RoutingOutputReference OriginalOutput,
        IntentionalDuplicateProvenance? Authorization = null)
    {
        internal RoutingDeliveryKey DeliveryKey =>
            new(Action.ConnectionId!, Output.Output);
    }
}
