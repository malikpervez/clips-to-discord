namespace ClipsToDiscord;

internal interface IRoutingActivationPreparer
{
    ValueTask<LegacyRoutingMigrationMarker> PrepareAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Imports the current legacy Discord credential when needed, then commits the exact quiesced
/// watcher decision as the migration route. It never changes process ownership.
/// </summary>
internal sealed class LegacyRoutingActivationPreparer : IRoutingActivationPreparer
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly WatchStateStore _watchState;
    private readonly LegacyDiscordConnectionCutoverAdapter _cutover;
    private readonly LegacyIgnoredBaselineReconciler _ignoredBaseline;
    private readonly Func<RoutingCaptureLibraryBinding> _captureLibraryBindingProvider;
    private readonly Func<LegacyRoutingMigrationAdmission> _admissionProvider;
    private readonly Func<int> _importedRouteLabelVersionProvider;
    private readonly Func<DateTimeOffset> _utcNow;

    internal LegacyRoutingActivationPreparer(
        Func<AppSettings> settingsProvider,
        WatchStateStore watchState,
        LegacyDiscordConnectionCutoverAdapter cutover,
        Func<RoutingCaptureLibraryBinding> captureLibraryBindingProvider,
        Func<LegacyRoutingMigrationAdmission> admissionProvider,
        Func<DateTimeOffset>? utcNow = null,
        LegacyIgnoredBaselineReconciler? ignoredBaseline = null,
        Func<int>? importedRouteLabelVersionProvider = null)
    {
        _settingsProvider = settingsProvider ??
                            throw new ArgumentNullException(nameof(settingsProvider));
        _watchState = watchState ?? throw new ArgumentNullException(nameof(watchState));
        _cutover = cutover ?? throw new ArgumentNullException(nameof(cutover));
        _captureLibraryBindingProvider = captureLibraryBindingProvider ??
                                         throw new ArgumentNullException(
                                             nameof(captureLibraryBindingProvider));
        _ignoredBaseline = ignoredBaseline ?? new LegacyIgnoredBaselineReconciler(_watchState);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _admissionProvider = admissionProvider ??
                             throw new ArgumentNullException(nameof(admissionProvider));
        _importedRouteLabelVersionProvider = importedRouteLabelVersionProvider ?? (() =>
            LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion);
    }

    public async ValueTask<LegacyRoutingMigrationMarker> PrepareAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsProvider() ??
                       throw new InvalidDataException(
                           "The legacy settings needed for Routing activation are unavailable.");
        if (!settings.IsValid)
        {
            throw new InvalidDataException(
                "Routing activation requires a valid legacy ClipCord configuration.");
        }

        var state = await _ignoredBaseline.ReconcileAsync(
                settings,
                legacyWorkerQuiesced: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!state.Loaded || state.State is null)
        {
            throw new InvalidDataException(
                $"The legacy watcher state cannot be migrated safely ({state.Status}).");
        }
        var cutover = await _cutover.ExecuteAsync(
                settings,
                state.State,
                legacyWorkerQuiesced: true,
                _captureLibraryBindingProvider(),
                _admissionProvider(),
                RoutingValidation.Utc(_utcNow()),
                cancellationToken,
                _importedRouteLabelVersionProvider())
            .ConfigureAwait(false);
        if (!cutover.MayReleaseLegacyOwnership || cutover.Cutover?.Marker is not
            { Phase: LegacyRoutingMigrationMarkerPhase.Committed } marker)
        {
            throw new InvalidDataException(
                $"The legacy watcher decision could not be migrated safely ({cutover.Status}).");
        }
        return marker;
    }
}

/// <summary>
/// Production adapter for the coordinator's irreversible boundary. Migration preparation is the
/// only reversible operation. Authority is created from freshly re-read committed marker and
/// watcher evidence; the injected work host is entered only under the resulting exact gate.
/// </summary>
internal sealed class RoutingProductionRuntime : IRoutingClipProcessingRuntime
{
    private readonly IRoutingActivationPreparer _preparer;
    private readonly LegacyRoutingMigrationMarkerStore _markers;
    private readonly RoutingSnapshotStore _snapshots;
    private readonly ILegacyRoutingActivationEvidenceSource _activationEvidence;
    private readonly IFreshRoutingActivationEvidenceSource? _freshActivationEvidence;
    private readonly IRoutingConnectionMembership? _connectionMembership;
    private readonly IRoutingInputSourceMembership? _inputSourceMembership;
    private readonly RoutingExecutionAuthorityStore _executionAuthority;
    private readonly IReadOnlySet<ClipCaptureSource> _coveredSources;
    private readonly Func<ClipProcessingOwnershipLease, RoutingRuntimeFeatureGate,
        CancellationToken, ValueTask> _startWorkHost;
    private readonly Func<CancellationToken, ValueTask> _stopWorkHost;
    private readonly Func<CancellationToken, ValueTask> _activationPreflight;

    internal RoutingProductionRuntime(
        IRoutingActivationPreparer preparer,
        LegacyRoutingMigrationMarkerStore markers,
        RoutingSnapshotStore snapshots,
        ILegacyRoutingActivationEvidenceSource activationEvidence,
        RoutingExecutionAuthorityStore executionAuthority,
        IReadOnlySet<ClipCaptureSource> coveredSources,
        Func<ClipProcessingOwnershipLease, RoutingRuntimeFeatureGate,
            CancellationToken, ValueTask> startWorkHost,
        Func<CancellationToken, ValueTask> stopWorkHost,
        Func<CancellationToken, ValueTask>? activationPreflight = null,
        Func<DateTimeOffset>? utcNow = null,
        IFreshRoutingActivationEvidenceSource? freshActivationEvidence = null,
        IRoutingConnectionMembership? connectionMembership = null,
        IRoutingInputSourceMembership? inputSourceMembership = null)
    {
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _activationEvidence = activationEvidence ??
                              throw new ArgumentNullException(nameof(activationEvidence));
        _freshActivationEvidence = freshActivationEvidence;
        _connectionMembership = connectionMembership;
        _inputSourceMembership = inputSourceMembership;
        _executionAuthority = executionAuthority ??
                              throw new ArgumentNullException(nameof(executionAuthority));
        ArgumentNullException.ThrowIfNull(coveredSources);
        foreach (var source in coveredSources)
        {
            RoutingValidation.Require(
                Enum.IsDefined(source) && AppSettings.NormalizeCaptureSource(source) == source,
                "Routing production source coverage contains an unsupported adapter.");
        }
        _coveredSources = coveredSources.ToHashSet();
        _startWorkHost = startWorkHost ??
                         throw new ArgumentNullException(nameof(startWorkHost));
        _stopWorkHost = stopWorkHost ?? throw new ArgumentNullException(nameof(stopWorkHost));
        _activationPreflight = activationPreflight ?? (_ => ValueTask.CompletedTask);
        // Kept as a compatibility seam for callers that supplied a clock while this runtime was
        // introduced. Durable authority time is instead bound to committed migration evidence,
        // so a crash retry or cold restart constructs byte-exact authority.
        _ = utcNow;
    }

    public async ValueTask PrepareActivationAsync(CancellationToken cancellationToken)
    {
        var authority = _executionAuthority.Inspect(cancellationToken);
        if (authority.Blocked)
        {
            throw new InvalidDataException(
                $"Routing execution authority cannot be trusted ({authority.LoadStatus}).");
        }
        if (authority.RoutingRequired)
        {
            // A cold-start coordinator skips preparation. Treat an accidental repeat as a strict
            // verification-only no-op rather than attempting to recreate legacy state.
            RequireCommittedEvidence(authority.Document!);
            return;
        }

        await _activationPreflight(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var marker = await _preparer.PrepareAsync(cancellationToken).ConfigureAwait(false);
        LegacyRoutingMigrationMarkerModel.Validate(marker);
        if (marker.Phase != LegacyRoutingMigrationMarkerPhase.Committed)
        {
            throw new InvalidDataException(
                "Routing activation preparation did not commit its migration marker.");
        }
        var durable = _markers.Load(cancellationToken);
        if (!durable.LoadedFromDisk || durable.Document != marker)
        {
            throw new InvalidDataException(
                "Routing activation preparation did not return its exact durable migration marker.");
        }
    }

    public bool RequiresRoutingFenceAfterPreparation()
    {
        var authority = _executionAuthority.Inspect(CancellationToken.None);
        if (!authority.LegacyPermitted) return true;
        var marker = _markers.Load(CancellationToken.None);
        return marker.LoadedFromDisk
            ? marker.Document!.Origin == RoutingActivationOrigin.FreshSetup ||
              marker.Document.Phase == LegacyRoutingMigrationMarkerPhase.Committed
            : marker.Status != RoutingDocumentLoadStatus.Missing;
    }

    public async ValueTask CommitExecutionAuthorityAsync(
        ClipProcessingOwnershipLease ownership)
    {
        RequireRoutingOwnership(ownership);
        var authority = _executionAuthority.Inspect(CancellationToken.None);
        if (authority.Blocked)
        {
            throw new InvalidDataException(
                $"Routing execution authority cannot be trusted ({authority.LoadStatus}).");
        }
        var requireFrozenFreshRoute = !authority.RoutingRequired;
        using var connectionGate = await EnterFreshConnectionExecutionGateAsync(
                requireFrozenFreshRoute)
            .ConfigureAwait(false);
        using var inputSourceGate = await EnterFreshInputSourceExecutionGateAsync(
                requireFrozenFreshRoute)
            .ConfigureAwait(false);
        var prerequisites = RequireCommitPrerequisites(requireFrozenFreshRoute);
        if (authority.RoutingRequired)
        {
            RequireCommittedEvidence(authority.Document!);
            if (authority.Document!.RequiredLegacySource != prerequisites.RequiredSource)
            {
                throw new InvalidDataException(
                    "Committed Routing authority no longer matches its legacy source evidence.");
            }
            return;
        }

        var candidate = RoutingExecutionAuthorityModel.Create(
            prerequisites.Marker,
            prerequisites.RequiredSource,
            prerequisites.Marker.UpdatedUtc);
        var validateFreshDependencies = prerequisites.Marker.Origin ==
                                        RoutingActivationOrigin.FreshSetup;
        if (validateFreshDependencies)
            RequireFreshRouteDependencies(prerequisites.Marker.Route);
        var committed = await _executionAuthority.CommitAsync(
                candidate,
                CancellationToken.None,
                beforeCommit: validateFreshDependencies
                    ? () =>
                    {
                        RequireRoutingOwnership(ownership);
                        RequireFreshRouteDependencies(prerequisites.Marker.Route);
                    }
                    : null)
            .ConfigureAwait(false);
        if (committed.Document != candidate)
        {
            throw new InvalidDataException(
                "The committed Routing execution authority differs from this activation.");
        }
    }

    public RoutingRuntimeGateInspection InspectActivation(
        ClipProcessingOwnershipLease ownership) => CreateGate(ownership).Inspect();

    public async ValueTask StartAsync(
        ClipProcessingOwnershipLease ownership,
        CancellationToken cancellationToken)
    {
        RequireRoutingOwnership(ownership);
        var gate = CreateGate(ownership);
        var permit = gate.Inspect();
        if (!permit.Enabled)
        {
            throw new InvalidOperationException(
                $"Routing work cannot start because activation is {permit.State}.");
        }
        await _startWorkHost(ownership, gate, cancellationToken).ConfigureAwait(false);
        RequireRoutingOwnership(ownership);
        if (!permit.SamePermit(gate.Inspect()))
        {
            throw new InvalidOperationException(
                "Routing activation authority changed while the work host was starting.");
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) =>
        _stopWorkHost(cancellationToken);

    private RoutingRuntimeFeatureGate CreateGate(ClipProcessingOwnershipLease ownership)
    {
        RequireRoutingOwnership(ownership);
        return RoutingRuntimeFeatureGate.Evaluate(
            requestedEnabled: true,
            _markers,
            _snapshots,
            _activationEvidence,
            ownership,
            _coveredSources,
            _executionAuthority,
            _freshActivationEvidence);
    }

    private void RequireCommittedEvidence(RoutingExecutionAuthorityDocument authority)
    {
        RoutingExecutionAuthorityModel.Validate(authority);
        var marker = _markers.Load(CancellationToken.None);
        if (!marker.LoadedFromDisk || marker.Document is null ||
            marker.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed ||
            marker.Document.MigrationId != authority.MigrationId ||
            !marker.Document.PayloadFingerprint.Equals(
                authority.MigrationPayloadFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !marker.Document.SourceFingerprint.Equals(
                authority.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            marker.Document.CaptureLibraryBinding != authority.CaptureLibraryBinding)
        {
            throw new InvalidDataException(
                "Committed Routing authority no longer matches its migration marker.");
        }
    }

    /// <summary>
    /// Re-reads every reversible prerequisite immediately before the sticky authority write. A
    /// failed preflight leaves Legacy recoverable; after the write, these same facts are enforced
    /// continuously by RoutingRuntimeFeatureGate and can only pause Routing, never revive Legacy.
    /// </summary>
    private RoutingCommitPrerequisites RequireCommitPrerequisites(
        bool requireFrozenFreshRoute)
    {
        var markerLoad = _markers.Load(CancellationToken.None);
        if (!markerLoad.LoadedFromDisk || markerLoad.Document is not
            { Phase: LegacyRoutingMigrationMarkerPhase.Committed } marker)
        {
            throw new InvalidDataException(
                $"The committed routing migration marker is unavailable ({markerLoad.Status}).");
        }
        LegacyRoutingMigrationMarkerModel.Validate(marker);

        if (marker.Origin == RoutingActivationOrigin.FreshSetup)
        {
            return RequireFreshCommitPrerequisites(marker, requireFrozenFreshRoute);
        }

        var evidence = _activationEvidence.Inspect();
        if (!evidence.Loaded || evidence.LegacyState.State is null ||
            evidence.Readiness is null)
        {
            throw new InvalidDataException(
                $"The quiesced legacy activation evidence is unavailable ({evidence.Status}).");
        }
        var legacyState = evidence.LegacyState;
        if (legacyState.PendingMoves != 0 || legacyState.PendingLocalOnlyMoves != 0 ||
            legacyState.PendingEditedUploads != 0 || legacyState.IgnoredFileKeys != 0)
        {
            throw new InvalidDataException(
                "Legacy queues or ignored baseline evidence appeared before Routing authority commit.");
        }
        var requiredSource = AppSettings.NormalizeCaptureSource(
            legacyState.State.CaptureSource);
        if (!Enum.IsDefined(requiredSource) ||
            legacyState.State.CaptureSource != requiredSource)
        {
            throw new InvalidDataException(
                "The legacy activation source is not canonical.");
        }

        var readiness = evidence.Readiness;
        var plan = readiness.Plan;
        if (!readiness.CanCommit || plan is null ||
            plan.MigrationId != marker.MigrationId ||
            plan.Mode != marker.Mode || plan.Scope != marker.Scope ||
            plan.CaptureLibraryBinding != marker.CaptureLibraryBinding ||
            !plan.SourceFingerprint.Equals(marker.SourceFingerprint, StringComparison.Ordinal) ||
            plan.ContentHashExclusions != marker.ContentHashExclusions ||
            !LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(plan.Route, marker.Route))
        {
            throw new InvalidDataException(
                "The current legacy activation plan no longer matches its committed migration.");
        }

        var snapshot = _snapshots.Load(CancellationToken.None);
        if (!snapshot.LoadedFromDisk || snapshot.Document is null)
        {
            throw new InvalidDataException(
                $"The committed Routing snapshot is unavailable ({snapshot.Status}).");
        }
        var migrationRoutes = snapshot.Document.Routes
            .Where(route => route.RouteId == marker.Route.RouteId)
            .ToArray();
        if (migrationRoutes.Length != 1 ||
            !LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                migrationRoutes[0], marker.Route))
        {
            throw new InvalidDataException(
                "The committed Routing snapshot no longer contains the exact migration route.");
        }
        if (!_coveredSources.Contains(requiredSource))
        {
            throw new InvalidDataException(
                "Routing does not cover the legacy capture source at the authority boundary.");
        }
        return new RoutingCommitPrerequisites(marker, requiredSource);
    }

    private RoutingCommitPrerequisites RequireFreshCommitPrerequisites(
        LegacyRoutingMigrationMarker marker,
        bool requireFrozenRoute)
    {
        var evidence = _freshActivationEvidence?.Inspect(marker);
        if (evidence is null || !evidence.Loaded || evidence.WatchState.State is null ||
            evidence.Plan is null)
        {
            throw new InvalidDataException(
                $"The fresh Routing activation evidence is unavailable ({evidence?.Status}).");
        }

        var state = evidence.WatchState.State;
        var requiredSource = AppSettings.NormalizeCaptureSource(state.CaptureSource);
        if (!Enum.IsDefined(requiredSource) || state.CaptureSource != requiredSource)
        {
            throw new InvalidDataException(
                "The fresh Routing activation source is not canonical.");
        }
        var plan = evidence.Plan;
        if (plan.Origin != RoutingActivationOrigin.FreshSetup ||
            plan.MigrationId != marker.MigrationId ||
            plan.Mode != marker.Mode || plan.Scope != marker.Scope ||
            plan.CaptureLibraryBinding != marker.CaptureLibraryBinding ||
            !plan.SourceFingerprint.Equals(marker.SourceFingerprint, StringComparison.Ordinal) ||
            plan.ContentHashExclusions != marker.ContentHashExclusions ||
            plan.Route != marker.Route)
        {
            throw new InvalidDataException(
                "The current fresh Routing activation plan no longer matches its committed marker.");
        }

        var snapshot = _snapshots.Load(CancellationToken.None);
        if (!snapshot.LoadedFromDisk || snapshot.Document is null)
        {
            throw new InvalidDataException(
                $"The committed Routing snapshot is unavailable ({snapshot.Status}).");
        }

        // Before authority is written, the first route is part of the exact frozen setup
        // transaction. Once authority exists, ordinary route mutations are governed by the
        // snapshot store and the runtime gate only requires a valid live generation.
        if (requireFrozenRoute)
        {
            var initialRoutes = snapshot.Document.Routes
                .Where(route => route.RouteId == marker.Route.RouteId)
                .ToArray();
            if (initialRoutes.Length != 1 || initialRoutes[0] != marker.Route)
            {
                throw new InvalidDataException(
                    "The committed Routing snapshot no longer contains the exact fresh first route.");
            }
        }
        if (!_coveredSources.Contains(requiredSource))
        {
            throw new InvalidDataException(
                "Routing does not cover the fresh capture source at the authority boundary.");
        }
        return new RoutingCommitPrerequisites(marker, requiredSource);
    }

    private async ValueTask<IDisposable> EnterFreshConnectionExecutionGateAsync(
        bool requireFrozenFreshRoute)
    {
        if (!requireFrozenFreshRoute)
            return RoutingConnectionExecutionGate.NoopLease;

        var marker = _markers.Load(CancellationToken.None);
        if (!marker.LoadedFromDisk || marker.Document is not
            { Origin: RoutingActivationOrigin.FreshSetup } freshMarker ||
            !freshMarker.Route.Actions.Any(action =>
                action.Enabled && action.Kind == RoutingActionKind.Deliver))
        {
            return RoutingConnectionExecutionGate.NoopLease;
        }
        if (_connectionMembership is null)
        {
            throw new InvalidDataException(
                "The fresh route's destination authority cannot be revalidated.");
        }
        return await _connectionMembership.EnterExecutionGateAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async ValueTask<IDisposable> EnterFreshInputSourceExecutionGateAsync(
        bool requireFrozenFreshRoute)
    {
        if (!requireFrozenFreshRoute)
            return RoutingInputSourceExecutionGate.NoopLease;

        var marker = _markers.Load(CancellationToken.None);
        if (!marker.LoadedFromDisk || marker.Document is not
            { Origin: RoutingActivationOrigin.FreshSetup } freshMarker ||
            GetFrozenSourceConnectionId(freshMarker.Route) is null)
        {
            return RoutingInputSourceExecutionGate.NoopLease;
        }
        if (_inputSourceMembership is null)
        {
            throw new InvalidDataException(
                "The fresh route's input-source authority cannot be revalidated.");
        }
        return await _inputSourceMembership.EnterExecutionGateAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The first route's external dependencies remain reversible until sticky execution
    /// authority is written. This check runs once after the source gate is acquired and again in
    /// the authority store's atomic before-commit callback. Holding the source gate through both
    /// checks prevents Disable, retirement, replacement, or health changes from racing the write.
    /// </summary>
    private void RequireFreshRouteDependencies(RoutingRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        foreach (var delivery in route.Actions.Where(action =>
                     action.Enabled && action.Kind == RoutingActionKind.Deliver))
        {
            if (delivery.Destination is not { } destination ||
                delivery.ConnectionId is not { } connectionId ||
                _connectionMembership is null ||
                !_connectionMembership.IsReady(
                    destination,
                    connectionId,
                    CancellationToken.None))
            {
                throw new InvalidDataException(
                    "The fresh route's destination connection is missing or needs attention.");
            }
        }

        var sourceId = GetFrozenSourceConnectionId(route);
        if (sourceId is null) return;
        if (_inputSourceMembership is null)
        {
            throw new InvalidDataException(
                "The fresh route's input-source authority cannot be revalidated.");
        }

        var allowedKinds = route.XboxHistorySelection is null
            ? new[] { RoutingInputSourceKind.SteelSeriesGg, RoutingInputSourceKind.Nvidia }
            : new[] { RoutingInputSourceKind.XboxGameDvrOneDrive };
        if (!allowedKinds.Any(kind => _inputSourceMembership.IsReady(
                sourceId,
                kind,
                CancellationToken.None)))
        {
            throw new InvalidDataException(
                "The fresh route's input source is missing, disabled, replaced, retired, or needs attention.");
        }
    }

    private static string? GetFrozenSourceConnectionId(RoutingRoute route) =>
        route.Conditions.SingleOrDefault(condition =>
            condition.Field == RoutingConditionField.SourceConnection &&
            condition.Operator == RoutingConditionOperator.Equals)?.Value;

    private static void RequireRoutingOwnership(ClipProcessingOwnershipLease ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (ownership.Owner != ClipProcessingRuntimeOwner.Routing || !ownership.IsCurrent)
        {
            throw new InvalidOperationException(
                "The Routing production runtime does not own clip processing.");
        }
    }

    private sealed record RoutingCommitPrerequisites(
        LegacyRoutingMigrationMarker Marker,
        ClipCaptureSource RequiredSource);
}
