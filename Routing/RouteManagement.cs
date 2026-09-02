using System.Globalization;

namespace ClipsToDiscord;

internal sealed record RoutingRouteDraft(
    string Name,
    RoutingTriggerKind Trigger,
    string? Game,
    RoutingDestinationKind? Destination,
    string? ConnectionId,
    RoutingOutputKind Output,
    RoutingDeliveryMode DeliveryMode,
    RoutingMissingOutputBehavior OnMissingOutput,
    bool FileIntoLibrary,
    string? WatchedSourceId = null,
    DateTimeOffset? EarliestCapturedUtc = null,
    RoutingXboxHistorySelection? XboxHistorySelection = null,
    RoutingInputSourceKind? WatchedSourceKind = null);

internal interface IRoutingRouteMutationAuthority
{
    bool CanMutate(
        RoutingSnapshotDocument snapshot,
        CancellationToken cancellationToken = default);
}

internal interface IRoutingConnectionMembership
{
    bool IsReady(
        RoutingDestinationKind destination,
        string connectionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The durable cutover marker and the exact migration route it committed are the authority for
/// route editing. Evaluating an already-loaded snapshot keeps this check on the same generation
/// that a route command is about to replace.
/// </summary>
internal sealed class CommittedRoutingRouteMutationAuthority : IRoutingRouteMutationAuthority
{
    private readonly LegacyRoutingMigrationMarkerStore _markers;

    internal CommittedRoutingRouteMutationAuthority(LegacyRoutingMigrationMarkerStore markers)
    {
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
    }

    public bool CanMutate(
        RoutingSnapshotDocument snapshot,
        CancellationToken cancellationToken = default) =>
        IsCutoverCommitted(_markers, snapshot, cancellationToken);

    internal static bool IsCutoverCommitted(
        LegacyRoutingMigrationMarkerStore markers,
        RoutingSnapshotDocument snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(snapshot);
        var loaded = markers.Load(cancellationToken);
        if (!loaded.LoadedFromDisk ||
            loaded.Document?.Phase != LegacyRoutingMigrationMarkerPhase.Committed)
        {
            return false;
        }

        var migrationRoute = snapshot.Routes.SingleOrDefault(route =>
            route.RouteId == loaded.Document.Route.RouteId);
        return migrationRoute is not null &&
               LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                   migrationRoute, loaded.Document.Route);
    }
}

/// <summary>
/// Small CAS-safe command surface for route configuration. It deliberately owns only the
/// immutable routing snapshot; starting or stopping a processing runtime is a separate opt-in
/// operation so editing a draft can never silently take ownership from the legacy watcher.
/// </summary>
internal sealed class RoutingRouteManager
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingSnapshotStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IRoutingRouteMutationAuthority _mutationAuthority;
    private readonly IRoutingConnectionMembership? _connectionMembership;
    private readonly IRoutingInputSourceMembership? _inputSourceMembership;

    internal RoutingRouteManager(
        RoutingSnapshotStore? store = null,
        Func<DateTimeOffset>? clock = null,
        IRoutingRouteMutationAuthority? mutationAuthority = null,
        IRoutingConnectionMembership? connectionMembership = null,
        IRoutingInputSourceMembership? inputSourceMembership = null)
    {
        _store = store ?? new RoutingSnapshotStore();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _mutationAuthority = mutationAuthority ?? CreateDefaultMutationAuthority(_store.Path);
        _connectionMembership = connectionMembership;
        _inputSourceMembership = inputSourceMembership;
    }

    internal RoutingDocumentLoadResult<RoutingSnapshotDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal string SnapshotPath => _store.Path;

    internal bool CanMutate(CancellationToken cancellationToken = default)
    {
        var loaded = _store.Load(cancellationToken);
        return loaded.LoadedFromDisk && loaded.Document is not null &&
               _mutationAuthority.CanMutate(loaded.Document, cancellationToken);
    }

    internal Task<RoutingSnapshotDocument> LoadOrCreateAsync(
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(_clock(), cancellationToken);

    internal async Task<RoutingRoute> AddAsync(
        RoutingRouteDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        var routeId = Guid.NewGuid();
        var actionIds = CreateActionIds(draft);
        var conditionIds = Enumerable.Range(0, ConditionCount(draft))
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var createdUtc = RoutingValidation.Utc(_clock());

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _store.Load(cancellationToken);
            var current = loaded.Status switch
            {
                RoutingDocumentLoadStatus.Loaded when loaded.Document is not null => loaded.Document,
                RoutingDocumentLoadStatus.Missing when _mutationAuthority.CanMutate(
                    RoutingSnapshotModel.CreateEmpty(createdUtc), cancellationToken) =>
                        await _store.LoadOrCreateAsync(createdUtc, cancellationToken)
                            .ConfigureAwait(false),
                RoutingDocumentLoadStatus.Missing => throw new InvalidOperationException(
                    "Routes cannot be changed until the safe legacy cutover is committed."),
                _ => throw new InvalidDataException(
                    $"Routes cannot be changed because routing state is {loaded.Status}.")
            };

            if (current.Routes.Any(route => route.RouteId == routeId))
                return current.Routes.Single(route => route.RouteId == routeId);

            RequireMutationAllowed(current, cancellationToken);
            RequireConnectionReady(draft, cancellationToken);
            RequireInputSourceBindable(draft, cancellationToken);

            var route = CreateRoute(
                draft,
                routeId,
                actionIds,
                conditionIds,
                NextPriority(current.Routes),
                createdUtc);
            var next = RoutingSnapshotModel.ReplaceRoutes(
                current,
                current.Routes.Concat([route]).ToArray(),
                RoutingValidation.Utc(_clock()));
            try
            {
                using var sourceGate = await EnterInputSourceExecutionGateAsync(
                        draft, cancellationToken)
                    .ConfigureAwait(false);
                RequireInputSourceBindable(draft, cancellationToken);
                await _store.SaveAsync(
                        next,
                        current.Generation,
                        cancellationToken,
                        beforeCommit: () =>
                        {
                            RequireConnectionReady(draft, CancellationToken.None);
                            RequireInputSourceBindable(draft, CancellationToken.None);
                        })
                    .ConfigureAwait(false);
                return route;
            }
            catch (RoutingConcurrencyException) when (attempt + 1 < MaximumSaveAttempts)
            {
                // Reload and rebuild the priority against the winner.
            }
        }

        throw new RoutingConcurrencyException("Routes kept changing while the route was added.");
    }

    internal Task SetEnabledAsync(
        Guid routeId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            routes => routes.Select(route => route.RouteId == routeId && route.Enabled != enabled
                ? Revise(route, route with { Enabled = enabled })
                : route).ToArray(),
            routes => RequireUserRoute(routes, routeId),
            "Only a user-created route can be changed.",
            cancellationToken);

    internal Task DeleteAsync(
        Guid routeId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            routes => routes.Where(route => route.RouteId != routeId).ToArray(),
            routes => RequireUserRoute(routes, routeId),
            "Only a user-created route can be deleted.",
            cancellationToken);

    internal Task MoveAsync(
        Guid routeId,
        int delta,
        CancellationToken cancellationToken = default)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));
        return MutateAsync(
            routes => Move(routes, routeId, delta),
            routes => RequireUserRoute(routes, routeId),
            "Only a user-created route can be reordered.",
            cancellationToken);
    }

    private async Task MutateAsync(
        Func<IReadOnlyList<RoutingRoute>, IReadOnlyList<RoutingRoute>> mutate,
        Func<IReadOnlyList<RoutingRoute>, bool> precondition,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _store.Load(cancellationToken);
            if (!loaded.LoadedFromDisk || loaded.Document is null)
                throw new InvalidDataException(
                    $"Routes cannot be changed because routing state is {loaded.Status}.");
            var current = loaded.Document;
            RequireMutationAllowed(current, cancellationToken);
            if (!precondition(current.Routes)) throw new InvalidOperationException(failureMessage);
            var routes = mutate(current.Routes);
            if (RoutingStructural.SequenceEqual(current.Routes, routes)) return;
            var next = RoutingSnapshotModel.ReplaceRoutes(
                current,
                routes,
                RoutingValidation.Utc(_clock()));
            try
            {
                using var sourceGate = await EnterInputSourceExecutionGateAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.SaveAsync(next, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (RoutingConcurrencyException) when (attempt + 1 < MaximumSaveAttempts)
            {
                // A different editor won. Apply this command to the fresh generation.
            }
        }

        throw new RoutingConcurrencyException("Routes kept changing while the command was saved.");
    }

    private IReadOnlyList<RoutingRoute> Move(
        IReadOnlyList<RoutingRoute> routes,
        Guid routeId,
        int delta)
    {
        var ordered = routes.OrderBy(route => route.Priority).ToList();
        var currentIndex = ordered.FindIndex(route => route.RouteId == routeId);
        var targetIndex = Math.Clamp(currentIndex + delta, 0, ordered.Count - 1);
        if (currentIndex < 0 || currentIndex == targetIndex) return routes;
        if (ordered[targetIndex].Source != RoutingRouteSource.User)
        {
            throw new InvalidOperationException(
                "The legacy-preserving fallback cannot be reordered or changed.");
        }
        (ordered[currentIndex], ordered[targetIndex]) = (ordered[targetIndex], ordered[currentIndex]);
        var now = RoutingValidation.Utc(_clock());
        return ordered.Select((route, index) => route.Priority == index
                ? route
                : route with
                {
                    Priority = index,
                    Revision = RoutingValidation.NextGeneration(route.Revision),
                    ModifiedUtc = now
                })
            .ToArray();
    }

    private RoutingRoute Revise(RoutingRoute current, RoutingRoute revised) => revised with
    {
        Revision = RoutingValidation.NextGeneration(current.Revision),
        ModifiedUtc = RoutingValidation.Utc(_clock())
    };

    private static bool RequireUserRoute(
        IReadOnlyList<RoutingRoute> routes,
        Guid routeId)
    {
        var route = routes.SingleOrDefault(candidate => candidate.RouteId == routeId)
            ?? throw new InvalidOperationException("The route no longer exists.");
        return route.Source == RoutingRouteSource.User;
    }

    private static RoutingRoute CreateRoute(
        RoutingRouteDraft draft,
        Guid routeId,
        IReadOnlyList<Guid> actionIds,
        IReadOnlyList<Guid> conditionIds,
        int priority,
        DateTimeOffset createdUtc)
    {
        var game = draft.Game?.Trim();
        var conditions = new List<RoutingCondition>(conditionIds.Count);
        var conditionIndex = 0;
        if (!string.IsNullOrWhiteSpace(game))
        {
            conditions.Add(new RoutingCondition(
                conditionIds[conditionIndex++],
                RoutingConditionField.Game,
                RoutingConditionOperator.Equals,
                game));
        }
        if (draft.WatchedSourceId is { } watchedSourceId)
        {
            conditions.Add(new RoutingCondition(
                conditionIds[conditionIndex++],
                RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals,
                watchedSourceId));
        }
        if (draft.EarliestCapturedUtc is { } earliestCapturedUtc)
        {
            conditions.Add(new RoutingCondition(
                conditionIds[conditionIndex],
                RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                earliestCapturedUtc.ToString("O", CultureInfo.InvariantCulture)));
        }
        var kind = draft.Trigger == RoutingTriggerKind.AnyNewSourceClip && conditions.Count == 0
            ? RoutingRouteKind.Fallback
            : RoutingRouteKind.Specific;
        var prepare = new RoutingPrepareSettings(
            Landscape: draft.Output == RoutingOutputKind.Landscape,
            Portrait: draft.Output == RoutingOutputKind.Portrait,
            draft.OnMissingOutput);
        var actions = new List<RoutingAction>(2);
        var actionIndex = 0;
        if (draft.Destination is { } destination)
        {
            actions.Add(new RoutingAction(
                actionIds[actionIndex++],
                Enabled: true,
                RoutingActionKind.Deliver,
                destination,
                draft.ConnectionId,
                draft.Output,
                draft.OnMissingOutput,
                draft.DeliveryMode,
                LibraryArea: null,
                new RoutingDeliverySettings(
                    Message: null,
                    Title: null,
                    Caption: null,
                    RoutingVisibility.Unspecified,
                    NotifyFollowers: false)));
        }
        if (draft.FileIntoLibrary)
        {
            actions.Add(new RoutingAction(
                actionIds[actionIndex],
                Enabled: true,
                RoutingActionKind.FileIntoLibrary,
                Destination: null,
                ConnectionId: null,
                OutputRef: null,
                OnMissingOutput: null,
                RoutingDeliveryMode.Automatic,
                draft.Destination is null ? RoutingLibraryArea.LocalOnly : RoutingLibraryArea.Uploaded,
                DeliverySettings: null));
        }

        var xboxHistory = draft.XboxHistorySelection is null
            ? null
            : new RoutingXboxHistorySelection(
                draft.XboxHistorySelection.ActivationUtc,
                draft.XboxHistorySelection.HistoricalOccurrences
                    .OrderBy(item => item.OccurrenceId, StringComparer.Ordinal)
                    .ToArray());

        return new RoutingRoute(
            routeId,
            draft.Name.Trim(),
            Enabled: true,
            priority,
            Revision: 1,
            RoutingRouteSource.User,
            kind,
            draft.Trigger,
            prepare,
            conditions,
            actions,
            createdUtc,
            createdUtc,
            xboxHistory);
    }

    private static IReadOnlyList<Guid> CreateActionIds(RoutingRouteDraft draft)
    {
        var count = (draft.Destination is null ? 0 : 1) + (draft.FileIntoLibrary ? 1 : 0);
        return Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
    }

    private static int ConditionCount(RoutingRouteDraft draft) =>
        (string.IsNullOrWhiteSpace(draft.Game) ? 0 : 1) +
        (draft.WatchedSourceId is null ? 0 : 1) +
        (draft.EarliestCapturedUtc is null ? 0 : 1);

    private static int NextPriority(IReadOnlyList<RoutingRoute> routes) =>
        routes.Count == 0 ? 0 : checked(routes.Max(route => route.Priority) + 1);

    private void RequireMutationAllowed(
        RoutingSnapshotDocument snapshot,
        CancellationToken cancellationToken)
    {
        if (!_mutationAuthority.CanMutate(snapshot, cancellationToken))
        {
            throw new InvalidOperationException(
                "Routes cannot be changed until the safe legacy cutover is committed.");
        }
    }

    private void RequireConnectionReady(
        RoutingRouteDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.Destination is not { } destination) return;
        var connectionId = draft.ConnectionId!;
        if (_connectionMembership is null ||
            !_connectionMembership.IsReady(destination, connectionId, cancellationToken))
        {
            throw new InvalidOperationException(
                "The selected destination connection is missing or needs attention.");
        }
    }

    private static IRoutingRouteMutationAuthority CreateDefaultMutationAuthority(
        string snapshotPath)
    {
        var directory = Path.GetDirectoryName(snapshotPath)
            ?? throw new InvalidOperationException("The routing state directory is unavailable.");
        return new CommittedRoutingRouteMutationAuthority(
            new LegacyRoutingMigrationMarkerStore(Path.Combine(
                directory,
                LegacyRoutingMigrationMarkerStore.FileName)));
    }

    private static void ValidateDraft(RoutingRouteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RoutingValidation.RequireText(draft.Name?.Trim(), 1, 80, "route name");
        RoutingValidation.Require(Enum.IsDefined(draft.Trigger) && Enum.IsDefined(draft.Output) &&
                                  Enum.IsDefined(draft.DeliveryMode) &&
                                  Enum.IsDefined(draft.OnMissingOutput),
            "The route draft contains an unsupported option.");
        RoutingValidation.Require(draft.Destination is null || Enum.IsDefined(draft.Destination.Value),
            "The route destination is unsupported.");
        RoutingValidation.Require(draft.Destination is not null || draft.FileIntoLibrary,
            "A route needs at least one action.");
        RoutingValidation.Require(draft.FileIntoLibrary,
            "Every route must finish by filing its source clip into the Library.");
        if (draft.Destination is not null)
            RoutingValidation.RequireOpaqueId(draft.ConnectionId, 128, "connection id");
        else
            RoutingValidation.Require(string.IsNullOrWhiteSpace(draft.ConnectionId),
                "A local-management route cannot reference a connection.");
        if (draft.Trigger == RoutingTriggerKind.AnyNewSourceClip &&
            !string.IsNullOrWhiteSpace(draft.Game))
            RoutingValidation.RequireText(draft.Game.Trim(), 1, 256, "game condition");
        if (draft.WatchedSourceId is not null)
            RoutingInputSourceCatalogModel.ValidateSourceId(draft.WatchedSourceId);
        if (draft.WatchedSourceKind is { } watchedSourceKind)
            RoutingValidation.Require(Enum.IsDefined(watchedSourceKind),
                "The watched source kind is unsupported.");
        RoutingValidation.RequireOptionalUtc(
            draft.EarliestCapturedUtc, "earliest capture timestamp");
        var xboxHistory = draft.XboxHistorySelection;
        if (xboxHistory is not null)
        {
            RoutingValidation.RequireUtc(
                xboxHistory.ActivationUtc, "Xbox history activation timestamp");
            var occurrences = xboxHistory.HistoricalOccurrences ??
                throw new InvalidDataException(
                    "The Xbox historical occurrence selection is missing.");
            RoutingValidation.Require(
                occurrences.Count <= XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips,
                "The Xbox historical occurrence selection is too large.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var occurrence in occurrences)
            {
                if (occurrence is null)
                    throw new InvalidDataException(
                        "An Xbox historical occurrence selection entry is missing.");
                RoutingValidation.RequireSha256(
                    occurrence.OccurrenceId, "Xbox historical occurrence identity");
                RoutingValidation.RequireSha256(
                    occurrence.RevisionId, "Xbox historical revision identity");
                RoutingValidation.Require(
                    occurrence.OccurrenceId.All(character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                    occurrence.RevisionId.All(character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                    "Xbox historical identities must use canonical lowercase SHA-256 text.");
                RoutingValidation.Require(unique.Add(occurrence.OccurrenceId),
                    "Xbox historical occurrence identities must be unique.");
            }
        }
        RoutingValidation.Require(
            draft.WatchedSourceId is null || draft.Trigger == RoutingTriggerKind.WatchedFolder,
            "A watched source can be bound only to a watched-folder route.");
        RoutingValidation.Require(
            (draft.WatchedSourceId is null) == (draft.WatchedSourceKind is null) ||
            draft.WatchedSourceId is not null &&
            draft.WatchedSourceKind is null &&
            xboxHistory is not null,
            "A named watched source requires its exact source kind.");

        var effectiveSourceKind = ResolveWatchedSourceKind(draft);
        if (effectiveSourceKind == RoutingInputSourceKind.XboxGameDvrOneDrive)
        {
            RoutingValidation.Require(
                draft.WatchedSourceId is not null &&
                draft.EarliestCapturedUtc is not null &&
                xboxHistory is not null,
                "An Xbox source, cutoff, and frozen history selection must be configured together.");
            var cutoff = draft.EarliestCapturedUtc ??
                         throw new InvalidDataException(
                             "The Xbox capture cutoff is missing.");
            var selection = xboxHistory ??
                            throw new InvalidDataException(
                                "The Xbox history selection is missing.");
            RoutingValidation.Require(cutoff <= selection.ActivationUtc,
                "The Xbox history cutoff cannot be after route activation.");
        }
        else
        {
            RoutingValidation.Require(
                draft.EarliestCapturedUtc is null && xboxHistory is null,
                "Only an Xbox source can carry a capture cutoff or frozen history selection.");
        }
    }

    private void RequireInputSourceBindable(
        RoutingRouteDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.WatchedSourceId is not { } sourceId) return;
        var sourceKind = ResolveWatchedSourceKind(draft) ??
                         throw new InvalidDataException(
                             "The watched source kind is missing.");
        if (_inputSourceMembership is null ||
            !_inputSourceMembership.IsRouteBindable(
                sourceId,
                sourceKind,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The selected input source is missing, replaced, retired, or needs attention.");
        }
    }

    private ValueTask<IDisposable> EnterInputSourceExecutionGateAsync(
        RoutingRouteDraft draft,
        CancellationToken cancellationToken) =>
        draft.WatchedSourceId is null || _inputSourceMembership is null
            ? ValueTask.FromResult(RoutingInputSourceExecutionGate.NoopLease)
            : _inputSourceMembership.EnterExecutionGateAsync(cancellationToken);

    private ValueTask<IDisposable> EnterInputSourceExecutionGateAsync(
        CancellationToken cancellationToken) =>
        _inputSourceMembership is null
            ? ValueTask.FromResult(RoutingInputSourceExecutionGate.NoopLease)
            : _inputSourceMembership.EnterExecutionGateAsync(cancellationToken);

    private static RoutingInputSourceKind? ResolveWatchedSourceKind(
        RoutingRouteDraft draft)
    {
        if (draft.WatchedSourceId is null) return null;
        if (draft.WatchedSourceKind is { } explicitKind) return explicitKind;

        // Xbox drafts written before named watched sources carried no explicit kind. Their
        // immutable cutoff/history pair is sufficient to retain exact compatibility without
        // permitting an ordinary source id to masquerade as Xbox (or vice versa).
        return draft.XboxHistorySelection is not null ||
               draft.EarliestCapturedUtc is not null
            ? RoutingInputSourceKind.XboxGameDvrOneDrive
            : null;
    }
}
