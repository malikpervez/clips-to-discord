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
    bool FileIntoLibrary);

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

    internal RoutingRouteManager(
        RoutingSnapshotStore? store = null,
        Func<DateTimeOffset>? clock = null,
        IRoutingRouteMutationAuthority? mutationAuthority = null,
        IRoutingConnectionMembership? connectionMembership = null)
    {
        _store = store ?? new RoutingSnapshotStore();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _mutationAuthority = mutationAuthority ?? CreateDefaultMutationAuthority(_store.Path);
        _connectionMembership = connectionMembership;
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
        var conditionId = string.IsNullOrWhiteSpace(draft.Game) ? (Guid?)null : Guid.NewGuid();
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

            var route = CreateRoute(
                draft,
                routeId,
                actionIds,
                conditionId,
                NextPriority(current.Routes),
                createdUtc);
            var next = RoutingSnapshotModel.ReplaceRoutes(
                current,
                current.Routes.Concat([route]).ToArray(),
                RoutingValidation.Utc(_clock()));
            try
            {
                await _store.SaveAsync(next, current.Generation, cancellationToken)
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
        Guid? conditionId,
        int priority,
        DateTimeOffset createdUtc)
    {
        var game = draft.Game?.Trim();
        var conditions = string.IsNullOrWhiteSpace(game)
            ? Array.Empty<RoutingCondition>()
            : [new RoutingCondition(
                conditionId!.Value,
                RoutingConditionField.Game,
                RoutingConditionOperator.Equals,
                game)];
        var kind = draft.Trigger == RoutingTriggerKind.AnyNewSourceClip && conditions.Length == 0
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
            createdUtc);
    }

    private static IReadOnlyList<Guid> CreateActionIds(RoutingRouteDraft draft)
    {
        var count = (draft.Destination is null ? 0 : 1) + (draft.FileIntoLibrary ? 1 : 0);
        return Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
    }

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
    }
}
