using System.Globalization;
using ClipsToDiscord;

internal static class NamedWatchedSourceRouteTests
{
    private const string SteelSourceId =
        "source.11111111222233334444555555555555";
    private const string NvidiaSourceId =
        "source.aaaaaaaa222233334444555555555555";
    private const string XboxSourceId =
        "source.bbbbbbbb222233334444555555555555";
    private const string OtherSourceId =
        "source.cccccccc222233334444555555555555";
    private const string ContentRevision =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string OccurrenceId =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CutoffUtc = Now.AddDays(-1);

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        AssertPersistedSourceKindsAreStable();
        AssertManagerCreatesExactNamedSourceRoutes(root);
        AssertSnapshotSeparatesOrdinaryAndXboxContracts();
        AssertEvaluatorMatchesNamedSourcesExactly();
        AssertFactProvenanceBoundaries();
    }

    private static void AssertPersistedSourceKindsAreStable()
    {
        Assert((int)RoutingInputSourceKind.XboxGameDvrOneDrive == 0 &&
               (int)RoutingInputSourceKind.SteelSeriesGg == 1 &&
               (int)RoutingInputSourceKind.Nvidia == 2,
            "Persisted input-source kind values must never be reordered or renumbered.");
    }

    private static void AssertManagerCreatesExactNamedSourceRoutes(string root)
    {
        foreach (var (name, sourceId, kind) in new[]
                 {
                     ("SteelSeries", SteelSourceId, RoutingInputSourceKind.SteelSeriesGg),
                     ("NVIDIA", NvidiaSourceId, RoutingInputSourceKind.Nvidia)
                 })
        {
            var store = new RoutingSnapshotStore(Path.Combine(
                root, name, RoutingSnapshotStore.FileName));
            var membership = new RecordingMembership(sourceId, kind);
            var manager = new RoutingRouteManager(
                store,
                () => Now,
                AlwaysAllowedMutationAuthority.Instance,
                inputSourceMembership: membership);
            var route = manager.AddAsync(NamedDraft(name, sourceId, kind))
                .GetAwaiter().GetResult();

            Assert(route.Kind == RoutingRouteKind.Specific &&
                   route.Trigger == RoutingTriggerKind.WatchedFolder &&
                   route.XboxHistorySelection is null &&
                   route.Conditions is
                   [
                       {
                           Field: RoutingConditionField.SourceConnection,
                           Operator: RoutingConditionOperator.Equals,
                           Value: var persistedSourceId
                       }
                   ] && persistedSourceId == sourceId &&
                   membership.Calls.Count >= 3 &&
                   membership.Calls.All(call =>
                       call.SourceId == sourceId && call.Kind == kind) &&
                   membership.ReadyCalls == 0,
                $"A {name} draft must freeze only its exact source id and check route binding under its exact source kind without requiring the source to be enabled yet.");
        }

        var wrongKindStore = new RoutingSnapshotStore(Path.Combine(
            root, "wrong-kind", RoutingSnapshotStore.FileName));
        var wrongKindManager = new RoutingRouteManager(
            wrongKindStore,
            () => Now,
            AlwaysAllowedMutationAuthority.Instance,
            inputSourceMembership: new RecordingMembership(
                SteelSourceId, RoutingInputSourceKind.SteelSeriesGg));
        AssertThrows<InvalidOperationException>(() => wrongKindManager.AddAsync(
                NamedDraft(
                    "Wrong kind",
                    SteelSourceId,
                    RoutingInputSourceKind.Nvidia))
            .GetAwaiter().GetResult(),
            "A source id must not pass readiness through another recorder kind.");
        Assert(wrongKindStore.Load().Document?.Routes.Count is null or 0,
            "Rejecting the wrong recorder kind must not publish a dormant route.");

        var missingKindManager = new RoutingRouteManager(
            new RoutingSnapshotStore(Path.Combine(
                root, "missing-kind", RoutingSnapshotStore.FileName)),
            () => Now,
            AlwaysAllowedMutationAuthority.Instance,
            inputSourceMembership: new RecordingMembership(
                SteelSourceId, RoutingInputSourceKind.SteelSeriesGg));
        AssertThrows<InvalidDataException>(() => missingKindManager.AddAsync(
                NamedDraft("Missing kind", SteelSourceId, kind: null))
            .GetAwaiter().GetResult(),
            "An ordinary named source must declare its exact kind at route-save admission.");

        var xboxMembership = new RecordingMembership(
            XboxSourceId, RoutingInputSourceKind.XboxGameDvrOneDrive);
        var xboxManager = new RoutingRouteManager(
            new RoutingSnapshotStore(Path.Combine(
                root, "xbox-compatibility", RoutingSnapshotStore.FileName)),
            () => Now,
            AlwaysAllowedMutationAuthority.Instance,
            inputSourceMembership: xboxMembership);
        var xbox = xboxManager.AddAsync(new RoutingRouteDraft(
                "Xbox compatibility",
                RoutingTriggerKind.WatchedFolder,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: XboxSourceId,
                EarliestCapturedUtc: CutoffUtc,
                XboxHistorySelection: new RoutingXboxHistorySelection(Now, [])))
            .GetAwaiter().GetResult();
        Assert(xbox.XboxHistorySelection is not null &&
               xboxMembership.Calls.All(call =>
                   call.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive),
            "An existing Xbox draft may infer its persisted kind only from its complete cutoff/history contract.");
    }

    private static void AssertSnapshotSeparatesOrdinaryAndXboxContracts()
    {
        var named = Route(
            priority: 0,
            [SourceCondition(SteelSourceId)]);
        RoutingSnapshotModel.Validate(SnapshotUnchecked([named]));

        var nonExact = Route(
            priority: 0,
            [new RoutingCondition(
                Guid.NewGuid(),
                RoutingConditionField.SourceConnection,
                RoutingConditionOperator.DoesNotEqual,
                SteelSourceId)]);
        AssertThrows<InvalidDataException>(() =>
                RoutingSnapshotModel.Validate(SnapshotUnchecked([nonExact])),
            "A named watched source must use an exact source-connection condition.");

        var cutoffWithoutHistory = Route(
            priority: 0,
            [
                SourceCondition(XboxSourceId),
                CapturedAtCondition(CutoffUtc)
            ]);
        AssertThrows<InvalidDataException>(() =>
                RoutingSnapshotModel.Validate(SnapshotUnchecked([cutoffWithoutHistory])),
            "An Xbox capture cutoff without frozen history must fail closed.");

        var historyWithoutCutoff = Route(
            priority: 0,
            [SourceCondition(XboxSourceId)],
            new RoutingXboxHistorySelection(Now, []));
        AssertThrows<InvalidDataException>(() =>
                RoutingSnapshotModel.Validate(SnapshotUnchecked([historyWithoutCutoff])),
            "Xbox history without its exact capture cutoff must fail closed.");

        var completeXbox = Route(
            priority: 0,
            [
                SourceCondition(XboxSourceId),
                CapturedAtCondition(CutoffUtc)
            ],
            new RoutingXboxHistorySelection(Now, []));
        RoutingSnapshotModel.Validate(SnapshotUnchecked([completeXbox]));
    }

    private static void AssertEvaluatorMatchesNamedSourcesExactly()
    {
        var steel = Route(priority: 0, [SourceCondition(SteelSourceId)]);
        var nvidia = Route(priority: 1, [SourceCondition(NvidiaSourceId)]);
        var fallback = Route(
            priority: 2,
            conditions: [],
            history: null,
            kind: RoutingRouteKind.Fallback,
            trigger: RoutingTriggerKind.AnyNewSourceClip);
        var snapshot = SnapshotUnchecked([steel, nvidia, fallback]);
        RoutingSnapshotModel.Validate(snapshot);

        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot, WatchedFacts(SteelSourceId))
                .SequenceEqual([steel.RouteId]) &&
               RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot, WatchedFacts(NvidiaSourceId))
                .SequenceEqual([nvidia.RouteId]),
            "SteelSeries and NVIDIA facts must match only the route bound to their exact source id.");
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot, WatchedFacts(OtherSourceId))
                .SequenceEqual([fallback.RouteId]) &&
               RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot, WatchedFacts(sourceId: null))
                .SequenceEqual([fallback.RouteId]),
            "An unbound or legacy watched source must retain ordinary fallback behavior.");

        var sourceOnlyXboxRoute = Route(
            priority: 0,
            [SourceCondition(XboxSourceId)]);
        var xboxSnapshot = SnapshotUnchecked([sourceOnlyXboxRoute, fallback with
        {
            Priority = 1
        }]);
        RoutingSnapshotModel.Validate(xboxSnapshot);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                xboxSnapshot, XboxFacts()).Count == 0,
            "Xbox facts must never enter a source-only named route or fall through to fallback without frozen history authority.");
    }

    private static void AssertFactProvenanceBoundaries()
    {
        var named = Route(priority: 0, [SourceCondition(SteelSourceId)]);
        var snapshot = SnapshotUnchecked([named]);
        RoutingSnapshotModel.Validate(snapshot);
        Assert(RoutingEvaluator.GetOrderedMatchedRouteIds(
                snapshot, WatchedFacts(SteelSourceId)).Single() == named.RouteId,
            "A watched-folder fact may carry one canonical source connection id.");

        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    BaseFacts(
                        RoutingClipSource.ClipCordCapture,
                        RoutingTriggerKind.InstantReplay,
                        RoutingCaptureType.InstantReplay,
                        SteelSourceId)),
            "ClipCord Capture facts must not impersonate an external source connection.");
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    BaseFacts(
                        RoutingClipSource.ManualImport,
                        RoutingTriggerKind.AnyNewSourceClip,
                        captureType: null,
                        SteelSourceId)),
            "Manual-import facts must not impersonate an external source connection.");
        AssertThrows<InvalidDataException>(() =>
                RoutingEvaluator.GetOrderedMatchedRouteIds(
                    snapshot,
                    WatchedFacts(SteelSourceId) with { CapturedUtc = Now }),
            "Ordinary watched-folder facts must not acquire Xbox-only capture-history provenance.");
    }

    private static RoutingRouteDraft NamedDraft(
        string name,
        string sourceId,
        RoutingInputSourceKind? kind) => new(
        name,
        RoutingTriggerKind.WatchedFolder,
        Game: null,
        Destination: null,
        ConnectionId: null,
        RoutingOutputKind.Original,
        RoutingDeliveryMode.Automatic,
        RoutingMissingOutputBehavior.UseOriginal,
        FileIntoLibrary: true,
        WatchedSourceId: sourceId,
        WatchedSourceKind: kind);

    private static RoutingRoute Route(
        int priority,
        IReadOnlyList<RoutingCondition> conditions,
        RoutingXboxHistorySelection? history = null,
        RoutingRouteKind kind = RoutingRouteKind.Specific,
        RoutingTriggerKind trigger = RoutingTriggerKind.WatchedFolder) => new(
        Guid.NewGuid(),
        $"Named source route {priority}",
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
        conditions,
        [FileAction()],
        Now,
        Now,
        history);

    private static RoutingCondition SourceCondition(string sourceId) => new(
        Guid.NewGuid(),
        RoutingConditionField.SourceConnection,
        RoutingConditionOperator.Equals,
        sourceId);

    private static RoutingCondition CapturedAtCondition(DateTimeOffset cutoff) => new(
        Guid.NewGuid(),
        RoutingConditionField.CapturedAt,
        RoutingConditionOperator.GreaterThanOrEqual,
        cutoff.ToString("O", CultureInfo.InvariantCulture));

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

    private static RoutingSnapshotDocument SnapshotUnchecked(
        IReadOnlyList<RoutingRoute> routes) => new(
        RoutingSnapshotStore.CurrentSchemaVersion,
        Generation: 1,
        routes,
        Now,
        Now);

    private static RoutingClipFacts WatchedFacts(string? sourceId) => BaseFacts(
        RoutingClipSource.WatchedFolder,
        RoutingTriggerKind.WatchedFolder,
        captureType: null,
        sourceId);

    private static RoutingClipFacts XboxFacts() => BaseFacts(
        RoutingClipSource.XboxOneDrive,
        RoutingTriggerKind.WatchedFolder,
        captureType: null,
        XboxSourceId) with
    {
        CapturedUtc = Now.AddSeconds(1),
        SourceOccurrenceId = OccurrenceId,
        SourceRevisionId = ContentRevision
    };

    private static RoutingClipFacts BaseFacts(
        RoutingClipSource source,
        RoutingTriggerKind trigger,
        RoutingCaptureType? captureType,
        string? sourceId)
    {
        var clipId = "clip-" + source.ToString().ToLowerInvariant();
        return new RoutingClipFacts(
            clipId,
            RoutingEvaluationEventKind.SourceArrival,
            source,
            trigger,
            captureType,
            "Battlefield 6",
            ReactionCamera: false,
            DurationMilliseconds: 30_000,
            ContentRevision,
            [new RoutingClipOutputRevision(
                new RoutingOutputReference(
                    clipId,
                    RoutingOutputKind.Original,
                    ContentRevision),
                RoutingOutputAvailability.Ready,
                FailureCode: null)],
            SourceConnectionId: sourceId);
    }

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

    private sealed class RecordingMembership(
        string readySourceId,
        RoutingInputSourceKind readyKind) : IRoutingInputSourceMembership
    {
        private readonly List<MembershipCall> _calls = [];
        internal IReadOnlyList<MembershipCall> Calls => _calls;
        internal int ReadyCalls { get; private set; }

        public bool IsReady(
            string sourceId,
            RoutingInputSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadyCalls++;
            return false;
        }

        public bool IsRouteBindable(
            string sourceId,
            RoutingInputSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add(new MembershipCall(sourceId, kind));
            return sourceId == readySourceId && kind == readyKind;
        }
    }

    private sealed record MembershipCall(
        string SourceId,
        RoutingInputSourceKind Kind);

    private sealed class AlwaysAllowedMutationAuthority : IRoutingRouteMutationAuthority
    {
        internal static readonly AlwaysAllowedMutationAuthority Instance = new();

        public bool CanMutate(
            RoutingSnapshotDocument snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
