using System.Globalization;
using ClipsToDiscord;

internal static class XboxRoutingContractTests
{
    private const string XboxSourceId = "source.11111111222233334444555555555555";
    private const string OtherSourceId = "source.aaaaaaaa222233334444555555555555";
    private const string OccurrenceId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RevisionId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRevisionId =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset CutoffUtc =
        new(2026, 8, 25, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ActivationUtc = CutoffUtc.AddDays(7);

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        AssertRouteManagerFreezesSourceAndCutoff(root);
        AssertRouteManagerRequiresLiveSourceMembership(root);
        AssertXboxRequiresExactSpecificSourceBinding();
        AssertXboxNeverFallsThroughToFallback();
        AssertLegacyWatchedFolderBehaviorIsPreserved();
        AssertSourceConditionShapeIsStrict();
        AssertXboxFactsAreComplete();
    }

    private static void AssertRouteManagerFreezesSourceAndCutoff(string root)
    {
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var manager = new RoutingRouteManager(
            store,
            () => now,
            TestRouteMutationAuthority.Allowed,
            connectionMembership: null,
            inputSourceMembership: new TestInputSourceMembership(
                _ => true));

        var route = manager.AddAsync(new RoutingRouteDraft(
                "Xbox Battlefield highlights",
                RoutingTriggerKind.WatchedFolder,
                "Battlefield 6",
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: XboxSourceId,
                EarliestCapturedUtc: CutoffUtc,
                XboxHistorySelection: new RoutingXboxHistorySelection(
                    ActivationUtc,
                    [new RoutingXboxHistoricalOccurrence(OccurrenceId, RevisionId)])))
            .GetAwaiter().GetResult();

        Assert(route.Kind == RoutingRouteKind.Specific &&
               route.Trigger == RoutingTriggerKind.WatchedFolder &&
               route.Conditions.Count == 3 &&
               route.Conditions.Select(condition => condition.ConditionId).Distinct().Count() == 3,
            "A source-bound Xbox draft must become one deterministic specific route with unique conditions.");
        Assert(route.Conditions.Any(condition =>
                   condition.Field == RoutingConditionField.Game &&
                   condition.Operator == RoutingConditionOperator.Equals &&
                   condition.Value == "Battlefield 6") &&
               route.Conditions.Any(condition =>
                   condition.Field == RoutingConditionField.SourceConnection &&
                   condition.Operator == RoutingConditionOperator.Equals &&
                   condition.Value == XboxSourceId) &&
               route.Conditions.Any(condition =>
                   condition.Field == RoutingConditionField.CapturedAt &&
                   condition.Operator == RoutingConditionOperator.GreaterThanOrEqual &&
                   condition.Value == CutoffUtc.ToString("O", CultureInfo.InvariantCulture)) &&
               route.XboxHistorySelection is
               {
                   ActivationUtc: var activation,
                   HistoricalOccurrences:
                   [{ OccurrenceId: OccurrenceId, RevisionId: RevisionId }]
               } && activation == ActivationUtc,
            "Route creation must freeze the exact source, UTC cutoff, activation, and approved occurrences.");

        AssertThrows<InvalidDataException>(() => manager.AddAsync(new RoutingRouteDraft(
                "Invalid source on capture",
                RoutingTriggerKind.InstantReplay,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: XboxSourceId))
            .GetAwaiter().GetResult(),
            "A non-watched route must not accept a watched-source binding.");

        AssertThrows<InvalidDataException>(() => manager.AddAsync(new RoutingRouteDraft(
                "Invalid unbound cutoff",
                RoutingTriggerKind.WatchedFolder,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                EarliestCapturedUtc: CutoffUtc))
            .GetAwaiter().GetResult(),
            "A fixed capture cutoff must not exist without an exact watched source.");
    }

    private static void AssertRouteManagerRequiresLiveSourceMembership(string root)
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 5, 0, TimeSpan.Zero);
        var unavailableStates = new[]
        {
            "missing",
            "disabled",
            "needs-attention",
            "retired",
            "replaced"
        };
        foreach (var state in unavailableStates)
        {
            var store = new RoutingSnapshotStore(Path.Combine(
                root, "membership-" + state, RoutingSnapshotStore.FileName));
            var manager = new RoutingRouteManager(
                store,
                () => now,
                TestRouteMutationAuthority.Allowed,
                inputSourceMembership: new TestInputSourceMembership(_ => false));
            AssertThrows<InvalidOperationException>(
                () => manager.AddAsync(XboxDraft()).GetAwaiter().GetResult(),
                $"A {state} Xbox source must not be frozen into a new route.");
            var loaded = store.Load();
            Assert(loaded.Document?.Routes.Count is null or 0,
                $"Rejecting a {state} Xbox source must leave no dormant route behind.");
        }

        var calls = 0;
        var raceStore = new RoutingSnapshotStore(Path.Combine(
            root, "membership-save-race", RoutingSnapshotStore.FileName));
        var raceManager = new RoutingRouteManager(
            raceStore,
            () => now,
            TestRouteMutationAuthority.Allowed,
            inputSourceMembership: new TestInputSourceMembership(_ => ++calls == 1));
        AssertThrows<InvalidOperationException>(
            () => raceManager.AddAsync(XboxDraft()).GetAwaiter().GetResult(),
            "A source disabled or replaced after editor preflight but before the route CAS must fail closed.");
        Assert(calls == 2 && raceStore.Load().Document?.Routes.Count is null or 0,
            "Route save must recheck source membership at the atomic commit boundary.");

        var serializedStore = new RoutingSnapshotStore(Path.Combine(
            root, "membership-serialized-race", RoutingSnapshotStore.FileName));
        var serializedMembership = new SerializedInputSourceMembership();
        var serializedManager = new RoutingRouteManager(
            serializedStore,
            () => now,
            TestRouteMutationAuthority.Allowed,
            inputSourceMembership: serializedMembership);
        _ = serializedManager.AddAsync(XboxDraft()).GetAwaiter().GetResult();
        serializedMembership.MutationTask?.GetAwaiter().GetResult();
        Assert(serializedStore.Load().Document?.Routes.Count == 1 &&
               !serializedMembership.Ready,
            "A disable that starts after the final source check must wait for route publication, producing one linear order instead of interleaving the documents.");
    }

    private static RoutingRouteDraft XboxDraft() => new(
        "Xbox Battlefield highlights",
        RoutingTriggerKind.WatchedFolder,
        "Battlefield 6",
        Destination: null,
        ConnectionId: null,
        RoutingOutputKind.Original,
        RoutingDeliveryMode.Automatic,
        RoutingMissingOutputBehavior.UseOriginal,
        FileIntoLibrary: true,
        WatchedSourceId: XboxSourceId,
        EarliestCapturedUtc: CutoffUtc,
        XboxHistorySelection: new RoutingXboxHistorySelection(ActivationUtc, []));

    private static void AssertXboxRequiresExactSpecificSourceBinding()
    {
        var exact = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [
                Condition(RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual, CutoffText())
            ]);
        var different = Route(
            1,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals, OtherSourceId)]);
        var genericSpecific = Route(
            2,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.Game,
                RoutingConditionOperator.Equals, "Battlefield 6")]);
        var fallback = Route(
            3,
            RoutingTriggerKind.AnyNewSourceClip,
            [],
            RoutingRouteKind.Fallback);
        var snapshot = Snapshot([exact, different, genericSpecific, fallback]);

        var matched = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc) with { Game = "Battlefield™ 6" });
        Assert(matched.SequenceEqual([exact.RouteId]),
            "Xbox facts must normalize console trademark marks and match only the specific route bound to their exact source connection.");

        var rainbow = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [
                Condition(RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(RoutingConditionField.Game,
                    RoutingConditionOperator.Equals, "Rainbow Six Siege")
            ]);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                Snapshot([rainbow]),
                XboxFacts(CutoffUtc) with { Game = "Rainbow Six ® Siege" })
                .SequenceEqual([rainbow.RouteId]),
            "Xbox game matching must normalize registered-mark variants consistently.");

        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    XboxFacts(CutoffUtc) with
                    {
                        SourceConnectionId = XboxSourceId.ToUpperInvariant()
                    }),
            "Source connection facts must use the catalog's one canonical identity shape.");
    }

    private static void AssertXboxNeverFallsThroughToFallback()
    {
        var exact = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [
                Condition(RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual, CutoffText())
            ]);
        var fallback = Route(
            1,
            RoutingTriggerKind.AnyNewSourceClip,
            [],
            RoutingRouteKind.Fallback);
        var snapshot = Snapshot([exact, fallback]);

        var beforeCutoff = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc.AddTicks(-1)));
        Assert(beforeCutoff.Count == 0,
            "An Xbox clip outside its fixed history window must not fall through to the global fallback.");

        var unknownSource = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc) with { SourceConnectionId = OtherSourceId });
        Assert(unknownSource.Count == 0,
            "An Xbox clip from an unbound source must produce no match instead of entering fallback.");

        var exactBoundary = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc));
        Assert(exactBoundary.SequenceEqual([exact.RouteId]),
            "A greater-than-or-equal capture cutoff must include its exact UTC boundary.");

        var unconfirmedHistorical = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc.AddHours(1)) with
            {
                SourceOccurrenceId = new string('c', 64)
            });
        Assert(unconfirmedHistorical.Count == 0,
            "A sync-late pre-activation occurrence that was not confirmed must stay excluded.");

        var replacedHistorical = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(CutoffUtc.AddHours(1)) with
            {
                SourceRevisionId = OtherRevisionId
            });
        Assert(replacedHistorical.Count == 0,
            "A confirmed historical occurrence whose bytes changed must stay excluded.");

        var futureArrival = RoutingEvaluator.GetOrderedMatchedRouteIds(
            snapshot,
            XboxFacts(ActivationUtc.AddSeconds(1)) with
            {
                SourceOccurrenceId = new string('d', 64)
            });
        Assert(futureArrival.SequenceEqual([exact.RouteId]),
            "A genuinely new post-activation Xbox occurrence must remain eligible.");
    }

    private static void AssertLegacyWatchedFolderBehaviorIsPreserved()
    {
        var generic = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.Game,
                RoutingConditionOperator.Equals, "Battlefield 6")]);
        var fallback = Route(
            1,
            RoutingTriggerKind.AnyNewSourceClip,
            [],
            RoutingRouteKind.Fallback);
        var snapshot = Snapshot([generic, fallback]);

        // This deliberately uses the original positional constructor shape.
        var legacyFacts = Facts(
            RoutingClipSource.WatchedFolder,
            RoutingTriggerKind.WatchedFolder,
            sourceConnectionId: null,
            capturedUtc: null);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(snapshot, legacyFacts)
                .SequenceEqual([generic.RouteId]),
            "Existing watched-folder facts must keep matching existing specific routes.");

        var otherGame = legacyFacts with { Game = "Another game" };
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(snapshot, otherGame)
                .SequenceEqual([fallback.RouteId]),
            "Existing non-Xbox facts must retain ordinary fallback behavior.");

        var spaced = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.Game,
                RoutingConditionOperator.Equals, "A B")]);
        var literalDash = Route(
            1,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.Game,
                RoutingConditionOperator.Contains, "-")]);
        var literalFallback = Route(
            2,
            RoutingTriggerKind.AnyNewSourceClip,
            [],
            RoutingRouteKind.Fallback);
        var literalSnapshot = Snapshot([spaced, literalDash, literalFallback]);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                literalSnapshot,
                legacyFacts with { Game = "AB" })
                .SequenceEqual([literalFallback.RouteId]) &&
               RoutingEvaluator.GetOrderedMatchedRouteIds(
                literalSnapshot,
                legacyFacts with { Game = "AlphaBeta" })
                .SequenceEqual([literalFallback.RouteId]),
            "Xbox game-name normalization must not change literal equality or Contains semantics for existing watched-folder routes.");
    }

    private static void AssertSourceConditionShapeIsStrict()
    {
        var capturedWithoutSource = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual, CutoffText())]);
        AssertThrows<InvalidDataException>(() => RoutingSnapshotModel.Validate(
                SnapshotUnchecked([capturedWithoutSource])),
            "A capture cutoff without an exact source binding must be rejected.");

        var sourceOnWrongTrigger = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [Condition(RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals, XboxSourceId)]);
        AssertThrows<InvalidDataException>(() => RoutingSnapshotModel.Validate(
                SnapshotUnchecked([sourceOnWrongTrigger])),
            "A source connection condition must be confined to a watched-folder route.");

        var nonCanonicalCutoff = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [
                Condition(RoutingConditionField.SourceConnection,
                    RoutingConditionOperator.Equals, XboxSourceId),
                Condition(RoutingConditionField.CapturedAt,
                    RoutingConditionOperator.GreaterThanOrEqual, "2026-08-25T16:30:00Z")
            ]);
        AssertThrows<InvalidDataException>(() => RoutingSnapshotModel.Validate(
                SnapshotUnchecked([nonCanonicalCutoff])),
            "Capture cutoffs must use one canonical round-trip UTC representation.");
    }

    private static void AssertXboxFactsAreComplete()
    {
        var route = Route(
            0,
            RoutingTriggerKind.WatchedFolder,
            [Condition(RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals, XboxSourceId)]);
        var snapshot = Snapshot([route]);
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    XboxFacts(CutoffUtc) with { SourceConnectionId = null }),
            "Xbox facts without a source connection must fail closed.");
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    XboxFacts(CutoffUtc) with { CapturedUtc = null }),
            "Xbox facts without a capture timestamp must fail closed.");
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    XboxFacts(CutoffUtc) with { SourceOccurrenceId = null }),
            "Xbox facts without an occurrence identity must fail closed.");
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    XboxFacts(CutoffUtc) with { SourceRevisionId = null }),
            "Xbox facts without a source revision identity must fail closed.");
    }

    private static RoutingSnapshotDocument Snapshot(IReadOnlyList<RoutingRoute> routes)
    {
        var snapshot = SnapshotUnchecked(routes);
        RoutingSnapshotModel.Validate(snapshot);
        return snapshot;
    }

    private static RoutingSnapshotDocument SnapshotUnchecked(
        IReadOnlyList<RoutingRoute> routes) => new(
        RoutingSnapshotStore.CurrentSchemaVersion,
        Generation: 7,
        routes,
        CutoffUtc,
        CutoffUtc);

    private static RoutingRoute Route(
        int priority,
        RoutingTriggerKind trigger,
        IReadOnlyList<RoutingCondition> conditions,
        RoutingRouteKind kind = RoutingRouteKind.Specific)
    {
        var normalizedConditions = conditions.ToList();
        var hasSource = normalizedConditions.Any(condition =>
            condition.Field == RoutingConditionField.SourceConnection);
        if (hasSource && normalizedConditions.All(condition =>
                condition.Field != RoutingConditionField.CapturedAt))
        {
            normalizedConditions.Add(Condition(
                RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                CutoffText()));
        }
        return new RoutingRoute(
            Guid.NewGuid(),
            $"Xbox route {priority}",
            Enabled: true,
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
            [FileAction()],
            CutoffUtc,
            CutoffUtc,
            hasSource
                ? new RoutingXboxHistorySelection(
                    ActivationUtc,
                    [new RoutingXboxHistoricalOccurrence(OccurrenceId, RevisionId)])
                : null);
    }

    private static RoutingCondition Condition(
        RoutingConditionField field,
        RoutingConditionOperator comparison,
        string value) => new(Guid.NewGuid(), field, comparison, value);

    private static RoutingAction FileAction() => new(
        Guid.NewGuid(),
        Enabled: true,
        RoutingActionKind.FileIntoLibrary,
        Destination: null,
        ConnectionId: null,
        OutputRef: null,
        OnMissingOutput: null,
        RoutingDeliveryMode.Automatic,
        RoutingLibraryArea.LocalOnly,
        DeliverySettings: null);

    private static RoutingClipFacts XboxFacts(DateTimeOffset capturedUtc) =>
        Facts(
            RoutingClipSource.XboxOneDrive,
            RoutingTriggerKind.WatchedFolder,
            XboxSourceId,
            capturedUtc);

    private static RoutingClipFacts Facts(
        RoutingClipSource source,
        RoutingTriggerKind trigger,
        string? sourceConnectionId,
        DateTimeOffset? capturedUtc)
    {
        const string clipId = "clip-xbox-contract";
        return new RoutingClipFacts(
            clipId,
            RoutingEvaluationEventKind.SourceArrival,
            source,
            trigger,
            CaptureType: null,
            "Battlefield 6",
            ReactionCamera: false,
            DurationMilliseconds: 30_000,
            RevisionId,
            [new RoutingClipOutputRevision(
                new RoutingOutputReference(clipId, RoutingOutputKind.Original, RevisionId),
                RoutingOutputAvailability.Ready,
                FailureCode: null)],
            sourceConnectionId,
            capturedUtc,
            source == RoutingClipSource.XboxOneDrive ? OccurrenceId : null,
            source == RoutingClipSource.XboxOneDrive ? RevisionId : null);
    }

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

    private sealed class TestInputSourceMembership : IRoutingInputSourceMembership
    {
        private readonly Func<string, bool> _isReady;

        internal TestInputSourceMembership(Func<string, bool> isReady) =>
            _isReady = isReady;

        public bool IsReady(
            string sourceId,
            RoutingInputSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return kind == RoutingInputSourceKind.XboxGameDvrOneDrive &&
                   _isReady(sourceId);
        }
    }

    private sealed class SerializedInputSourceMembership : IRoutingInputSourceMembership
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _checks;

        internal bool Ready { get; private set; } = true;
        internal Task? MutationTask { get; private set; }

        public bool IsReady(
            string sourceId,
            RoutingInputSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _checks) == 3)
            {
                MutationTask = Task.Run(async () =>
                {
                    using var lease = await EnterExecutionGateAsync();
                    Ready = false;
                });
            }
            return Ready && sourceId == XboxSourceId &&
                   kind == RoutingInputSourceKind.XboxGameDvrOneDrive;
        }

        public async ValueTask<IDisposable> EnterExecutionGateAsync(
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new GateLease(_gate);
        }

        private sealed class GateLease : IDisposable
        {
            private SemaphoreSlim? _gate;

            internal GateLease(SemaphoreSlim gate) => _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
