using System.Globalization;
using ClipsToDiscord;

internal static class XboxDvrRouteAdmissionTests
{
    private const string XboxSourceId = "source.11111111222233334444555555555555";
    private const string OtherSourceId = "source.aaaaaaaa222233334444555555555555";
    private const string OccurrenceId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherOccurrenceId =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string RevisionId =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string OtherRevisionId =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset CapturedUtc =
        new(2026, 9, 1, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CutoffUtc = CapturedUtc.AddDays(-7);
    private static readonly DateTimeOffset ActivationUtc = CapturedUtc.AddTicks(1);

    internal static void Run()
    {
        AssertOnlyPotentialExplicitRoutesAreAdmitted();
        AssertDurationRemainsPotentialUntilExactEvaluation();
        AssertCapturedBoundaryAndKnownOperatorsMatchExactly();
        AssertInputsFailClosed();
    }

    private static void AssertOnlyPotentialExplicitRoutesAreAdmitted()
    {
        var durationPotential = Route(
            id: 1,
            priority: 20,
            conditions:
            [
                Condition(101, RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(102, RoutingConditionField.Duration,
                    RoutingConditionOperator.GreaterThanOrEqual, "999999999")
            ]);
        var fullyKnownMatch = Route(
            id: 2,
            priority: 10,
            conditions:
            [
                Condition(201, RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(202, RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual, CutoffText()),
                Condition(203, RoutingConditionField.Game,
                    RoutingConditionOperator.Contains, "battlefield"),
                Condition(204, RoutingConditionField.ClipSource,
                    RoutingConditionOperator.Equals, nameof(RoutingClipSource.XboxOneDrive)),
                Condition(205, RoutingConditionField.ReactionCamera,
                    RoutingConditionOperator.Equals, "false"),
                Condition(206, RoutingConditionField.CaptureType,
                    RoutingConditionOperator.DoesNotEqual,
                    nameof(RoutingCaptureType.ManualRecording))
            ]);
        var disabled = Route(
            id: 3,
            priority: 0,
            conditions: [SourceCondition(301, XboxSourceId)],
            enabled: false);
        var wrongSource = Route(
            id: 4,
            priority: 1,
            conditions: [SourceCondition(401, OtherSourceId)]);
        var afterClip = Route(
            id: 5,
            priority: 2,
            conditions:
            [
                SourceCondition(501, XboxSourceId),
                Condition(502, RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual,
                    CapturedUtc.AddTicks(1).ToString("O", CultureInfo.InvariantCulture))
            ]);
        var genericWatched = Route(
            id: 6,
            priority: 3,
            conditions:
            [
                Condition(601, RoutingConditionField.Game,
                    RoutingConditionOperator.Equals, "Battlefield 6")
            ]);
        var fallback = Route(
            id: 7,
            priority: 4,
            conditions: [],
            kind: RoutingRouteKind.Fallback,
            trigger: RoutingTriggerKind.AnyNewSourceClip);
        var wrongClipSource = Route(
            id: 8,
            priority: 5,
            conditions:
            [
                SourceCondition(801, XboxSourceId),
                Condition(802, RoutingConditionField.ClipSource,
                    RoutingConditionOperator.DoesNotEqual,
                    nameof(RoutingClipSource.XboxOneDrive))
            ]);
        var requiresCamera = Route(
            id: 9,
            priority: 6,
            conditions:
            [
                SourceCondition(901, XboxSourceId),
                Condition(902, RoutingConditionField.ReactionCamera,
                    RoutingConditionOperator.Equals, "true")
            ]);
        var requiresCaptureType = Route(
            id: 10,
            priority: 7,
            conditions:
            [
                SourceCondition(1001, XboxSourceId),
                Condition(1002, RoutingConditionField.CaptureType,
                    RoutingConditionOperator.Equals,
                    nameof(RoutingCaptureType.InstantReplay))
            ]);
        var wrongGame = Route(
            id: 11,
            priority: 8,
            conditions:
            [
                SourceCondition(1101, XboxSourceId),
                Condition(1102, RoutingConditionField.Game,
                    RoutingConditionOperator.Equals, "Another game")
            ]);
        var snapshot = Snapshot(
        [
            durationPotential,
            fullyKnownMatch,
            disabled,
            wrongSource,
            afterClip,
            genericWatched,
            fallback,
            wrongClipSource,
            requiresCamera,
            requiresCaptureType,
            wrongGame
        ]);

        var potential = XboxDvrRouteAdmission.FindPotentialRoutes(
            snapshot, XboxSourceId, "Battlefield™ 6", CapturedUtc, OccurrenceId,
            RevisionId);

        Assert(potential.Select(route => route.RouteId)
                .SequenceEqual([fullyKnownMatch.RouteId, durationPotential.RouteId]),
            "Xbox admission must normalize trademark-bearing console game names and return only ordered, enabled, explicitly source-bound routes whose known metadata can match.");
    }

    private static void AssertDurationRemainsPotentialUntilExactEvaluation()
    {
        var durationRoute = Route(
            id: 20,
            priority: 0,
            conditions:
            [
                SourceCondition(2001, XboxSourceId),
                Condition(2002, RoutingConditionField.Duration,
                    RoutingConditionOperator.GreaterThanOrEqual, "60000")
            ]);
        var fallback = Route(
            id: 21,
            priority: 1,
            conditions: [],
            kind: RoutingRouteKind.Fallback,
            trigger: RoutingTriggerKind.AnyNewSourceClip);
        var snapshot = Snapshot([durationRoute, fallback]);

        var potential = XboxDvrRouteAdmission.FindPotentialRoutes(
            snapshot, XboxSourceId, "Battlefield 6", CapturedUtc, OccurrenceId,
            RevisionId);
        Assert(potential.Select(route => route.RouteId).SequenceEqual([durationRoute.RouteId]),
            "Unknown duration must keep an otherwise possible route eligible for hydration.");

        var shortFacts = XboxFacts(durationMilliseconds: 30_000);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(snapshot, shortFacts).Count == 0,
            "The exact evaluator must reject a potential route once hydrated duration disproves it, without falling back.");
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                snapshot, shortFacts with { DurationMilliseconds = 60_000 })
                .SequenceEqual([durationRoute.RouteId]),
            "The exact evaluator must remain the final authority after duration becomes known.");
    }

    private static void AssertCapturedBoundaryAndKnownOperatorsMatchExactly()
    {
        var lowerBoundaryRoute = Route(
            id: 30,
            priority: 0,
            conditions:
            [
                SourceCondition(3001, XboxSourceId),
                Condition(3002, RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual, CutoffText()),
                Condition(3003, RoutingConditionField.Game,
                    RoutingConditionOperator.DoesNotEqual, "Another game"),
                Condition(3004, RoutingConditionField.ClipSource,
                    RoutingConditionOperator.DoesNotEqual,
                    nameof(RoutingClipSource.WatchedFolder)),
                Condition(3005, RoutingConditionField.ReactionCamera,
                    RoutingConditionOperator.DoesNotEqual, "true")
            ]);
        var lowerSnapshot = Snapshot([lowerBoundaryRoute]);

        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                lowerSnapshot, XboxSourceId, "BATTLEFIELD 6", CutoffUtc, OccurrenceId,
                RevisionId)
                .Select(item => item.RouteId).SequenceEqual([lowerBoundaryRoute.RouteId]),
            "Known metadata comparisons must include an exact UTC lower boundary and use evaluator-equivalent operators.");
        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                lowerSnapshot, XboxSourceId, "Battlefield 6", CutoffUtc.AddTicks(-1),
                OccurrenceId, RevisionId).Count == 0,
            "A clip before the fixed UTC cutoff must be rejected without hydration.");

        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                lowerSnapshot, XboxSourceId, "Battlefield 6", CapturedUtc,
                OtherOccurrenceId, OtherRevisionId).Count == 0,
            "A sync-late pre-activation occurrence outside the confirmed set must stay ineligible.");
        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                lowerSnapshot, XboxSourceId, "Battlefield 6", CapturedUtc,
                OccurrenceId, OtherRevisionId).Count == 0,
            "A confirmed historical occurrence whose source revision changed must stay ineligible.");
        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                lowerSnapshot, XboxSourceId, "Battlefield 6", ActivationUtc.AddTicks(1),
                OtherOccurrenceId, OtherRevisionId)
                .Select(item => item.RouteId).SequenceEqual([lowerBoundaryRoute.RouteId]),
            "A post-activation occurrence must remain eligible without appearing in history.");

        var emptyIdentityContains = Route(
            id: 31,
            priority: 0,
            conditions:
            [
                SourceCondition(3101, XboxSourceId),
                Condition(3102, RoutingConditionField.Game,
                    RoutingConditionOperator.Contains, "™")
            ]);
        Assert(XboxDvrRouteAdmission.FindPotentialRoutes(
                Snapshot([emptyIdentityContains]), XboxSourceId, "Battlefield 6",
                CapturedUtc, OccurrenceId, RevisionId).Count == 0,
            "An Xbox game filter with no semantic canonical identity must not become a universal Contains match.");
    }

    private static void AssertInputsFailClosed()
    {
        var route = Route(
            id: 40,
            priority: 0,
            conditions: [SourceCondition(4001, XboxSourceId)]);
        var snapshot = Snapshot([route]);

        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot with { Generation = 0 }, XboxSourceId, "Battlefield 6", CapturedUtc,
                OccurrenceId, RevisionId),
            "Admission must validate the routing snapshot before selecting work.");
        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot, XboxSourceId.ToUpperInvariant(), "Battlefield 6", CapturedUtc,
                OccurrenceId, RevisionId),
            "Admission must reject a non-canonical source identity.");
        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot, XboxSourceId, "   ", CapturedUtc, OccurrenceId, RevisionId),
            "Admission must reject a missing parsed game name.");
        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot,
                XboxSourceId,
                "Battlefield 6",
                CapturedUtc.ToOffset(TimeSpan.FromHours(-4)),
                OccurrenceId,
                RevisionId),
            "Admission must require one unambiguous UTC capture timestamp.");
        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot, XboxSourceId, "Battlefield 6", CapturedUtc, "not-a-hash",
                RevisionId),
            "Admission must require one canonical occurrence identity.");
        AssertThrows<InvalidDataException>(() => XboxDvrRouteAdmission.FindPotentialRoutes(
                snapshot, XboxSourceId, "Battlefield 6", CapturedUtc, OccurrenceId,
                "not-a-hash"),
            "Admission must require one canonical revision identity.");
    }

    private static RoutingSnapshotDocument Snapshot(IReadOnlyList<RoutingRoute> routes)
    {
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 7,
            routes,
            CutoffUtc,
            ActivationUtc);
        RoutingSnapshotModel.Validate(snapshot);
        return snapshot;
    }

    private static RoutingRoute Route(
        int id,
        int priority,
        IReadOnlyList<RoutingCondition> conditions,
        bool enabled = true,
        RoutingRouteKind kind = RoutingRouteKind.Specific,
        RoutingTriggerKind trigger = RoutingTriggerKind.WatchedFolder)
    {
        var normalizedConditions = conditions.ToList();
        var hasSource = normalizedConditions.Any(condition =>
            condition.Field == RoutingConditionField.SourceConnection);
        if (hasSource && normalizedConditions.All(condition =>
                condition.Field != RoutingConditionField.CapturedAt))
        {
            normalizedConditions.Add(Condition(
                9000 + id,
                RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                CutoffText()));
        }
        return new RoutingRoute(
            Id(id),
            $"Xbox admission route {id}",
            enabled,
            priority,
            Revision: 1,
            RoutingRouteSource.User,
            kind,
            trigger,
            new RoutingPrepareSettings(
                Landscape: false,
                Portrait: false,
                RoutingMissingOutputBehavior.UseOriginal),
            normalizedConditions,
            [FileAction(id)],
            ActivationUtc,
            ActivationUtc,
            hasSource
                ? new RoutingXboxHistorySelection(
                    ActivationUtc,
                    [new RoutingXboxHistoricalOccurrence(OccurrenceId, RevisionId)])
                : null);
    }

    private static RoutingCondition SourceCondition(int id, string sourceId) =>
        Condition(
            id,
            RoutingConditionField.SourceConnection,
            RoutingConditionOperator.Equals,
            sourceId);

    private static RoutingCondition Condition(
        int id,
        RoutingConditionField field,
        RoutingConditionOperator comparison,
        string value) => new(Id(id), field, comparison, value);

    private static RoutingAction FileAction(int id) => new(
        Id(5000 + id),
        Enabled: true,
        RoutingActionKind.FileIntoLibrary,
        Destination: null,
        ConnectionId: null,
        OutputRef: null,
        OnMissingOutput: null,
        RoutingDeliveryMode.Automatic,
        RoutingLibraryArea.LocalOnly,
        DeliverySettings: null);

    private static RoutingClipFacts XboxFacts(long durationMilliseconds)
    {
        const string clipId = "clip-xbox-admission";
        return new RoutingClipFacts(
            clipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.XboxOneDrive,
            RoutingTriggerKind.WatchedFolder,
            CaptureType: null,
            "Battlefield 6",
            ReactionCamera: false,
            durationMilliseconds,
            RevisionId,
            [new RoutingClipOutputRevision(
                new RoutingOutputReference(clipId, RoutingOutputKind.Original, RevisionId),
                RoutingOutputAvailability.Ready,
                FailureCode: null)],
            XboxSourceId,
            CapturedUtc,
            OccurrenceId,
            RevisionId);
    }

    private static Guid Id(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private static string CutoffText() =>
        CutoffUtc.ToString("O", CultureInfo.InvariantCulture);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
