namespace ClipsToDiscord;

internal sealed record RoutingRuntimeCoordinatorOptions(
    bool RequestedEnabled,
    bool AuthorityAlreadyCommitted = false,
    bool RecoverCommittedMigration = false,
    bool LegacyRuntimeAllowed = false)
{
    /// <summary>
    /// Routing is intentionally inert unless a caller supplies an explicit opt-in. Constructing
    /// the coordinator, its hosts, or a routing bridge must never stop the legacy watcher.
    /// </summary>
    internal static RoutingRuntimeCoordinatorOptions Default { get; } = new(false);
}

internal enum RoutingRuntimeCoordinatorState
{
    Disabled,
    SetupReady,
    LegacyActive,
    StartingRouting,
    RoutingActive,
    StoppingRouting,
    RoutingQuiesced,
    RoutingRecoveryNeeded,
    LegacyRecoveryNeeded
}

internal enum RoutingRuntimeTransitionStatus
{
    DisabledByDefault,
    Started,
    AlreadyRunning,
    Stopped,
    AlreadyStopped,
    OwnershipUnavailable,
    LegacyStopFailed,
    GateRejectedLegacyRestored,
    GateRejectedRecoveryNeeded,
    PreparationFailedLegacyRestored,
    PreparationFailedSetupReady,
    PreparationFailedRecoveryNeeded,
    AuthorityCommitFailedRecoveryNeeded,
    RoutingStartFailedLegacyRestored,
    RoutingStartFailedRecoveryNeeded,
    RoutingStopFailed,
    LegacyRestartFailed
}

internal sealed record RoutingRuntimeTransitionResult(
    RoutingRuntimeTransitionStatus Status,
    RoutingRuntimeCoordinatorState State,
    RoutingRuntimeGateInspection? GateInspection = null,
    Exception? Error = null)
{
    internal bool RoutingRunning => State == RoutingRuntimeCoordinatorState.RoutingActive;
    internal bool LegacyRunning => State == RoutingRuntimeCoordinatorState.LegacyActive;
}

/// <summary>
/// Adapter around the one legacy watcher owned by the application context. StopAsync must not
/// return until that watcher has quiesced and released its private Legacy ownership lease.
/// StartAsync must atomically adopt the supplied Legacy lease (DiscordAwareController does this
/// through TryReissue) and must clean up any partial start before throwing.
/// </summary>
internal interface ILegacyClipProcessingRuntime
{
    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(
        ClipProcessingOwnershipLease ownership,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter around RoutingRuntimeBridge plus its event/worker registrations. InspectActivation
/// must build the bridge's live RoutingRuntimeFeatureGate from the supplied Routing lease.
/// StartAsync may borrow, but must never dispose, transfer, or reissue that lease. StopAsync must
/// quiesce every registered routing callback before it returns. Once durable Routing authority
/// commit begins, the coordinator retains Routing ownership across stops and recovery attempts.
/// </summary>
internal interface IRoutingClipProcessingRuntime
{
    /// <summary>
    /// Performs the durable, idempotent migration preparation that requires a quiesced legacy
    /// watcher. The coordinator invokes this only while it owns Transition, before any Routing
    /// lease exists. Cancellation or failure must leave state safe to resume on another attempt.
    /// </summary>
    ValueTask PrepareActivationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True once preparation has durable evidence that makes restarting Legacy unsafe, even if
    /// sticky execution authority has not yet been written. The default keeps test and shadow
    /// adapters reversible; production binds this to its validated marker/authority stores.
    /// </summary>
    bool RequiresRoutingFenceAfterPreparation() => false;

    /// <summary>
    /// Durably and idempotently commits sticky Routing execution authority. Entry into this call
    /// is the irreversible boundary: the implementation receives no caller cancellation token,
    /// and any exception is ambiguous because the authority may already be durable.
    /// </summary>
    ValueTask CommitExecutionAuthorityAsync(ClipProcessingOwnershipLease ownership);

    RoutingRuntimeGateInspection InspectActivation(
        ClipProcessingOwnershipLease ownership);

    ValueTask StartAsync(
        ClipProcessingOwnershipLease ownership,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Serializes cutover between the current watcher and RoutingRuntimeBridge.
/// Ownership is the authority: the legacy runtime is fully stopped before Transition can be
/// acquired. Preparation is reversible, but entry into durable Routing authority commit is not:
/// every later failure, stop, and retry remains fenced by Routing ownership. Cleanup ignores
/// caller cancellation so cancellation cannot allow legacy and Routing processing to overlap.
/// </summary>
internal sealed class RoutingRuntimeCoordinator
{
    private readonly ClipProcessingOwnershipCoordinator _ownership;
    private readonly ILegacyClipProcessingRuntime _legacyRuntime;
    private readonly IRoutingClipProcessingRuntime _routingRuntime;
    private readonly RoutingRuntimeCoordinatorOptions _options;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private ClipProcessingOwnershipLease? _routingOwnership;
    private bool _authorityCommitEntered;
    private bool _recoveryPreparationRequired;
    private bool _routingMayBeActive;
    private int _state;

    internal RoutingRuntimeCoordinator(
        ClipProcessingOwnershipCoordinator ownership,
        ILegacyClipProcessingRuntime legacyRuntime,
        IRoutingClipProcessingRuntime routingRuntime,
        RoutingRuntimeCoordinatorOptions? options = null,
        ClipProcessingOwnershipLease? initialRoutingOwnership = null)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _legacyRuntime = legacyRuntime ?? throw new ArgumentNullException(nameof(legacyRuntime));
        _routingRuntime = routingRuntime ?? throw new ArgumentNullException(nameof(routingRuntime));
        _options = options ?? RoutingRuntimeCoordinatorOptions.Default;
        if ((_options.AuthorityAlreadyCommitted || _options.RecoverCommittedMigration) &&
            !_options.RequestedEnabled)
        {
            throw new ArgumentException(
                "Committed Routing authority requires explicit Routing enablement.",
                nameof(options));
        }
        if (_options.AuthorityAlreadyCommitted && _options.RecoverCommittedMigration)
        {
            throw new ArgumentException(
                "Routing cannot both adopt execution authority and recover a pre-authority migration.",
                nameof(options));
        }
        if ((_options.AuthorityAlreadyCommitted || _options.RecoverCommittedMigration) !=
            (initialRoutingOwnership is not null))
        {
            throw new ArgumentException(
                "Committed Routing authority and its initial ownership lease must be supplied together.",
                initialRoutingOwnership is null ? nameof(options) : nameof(initialRoutingOwnership));
        }
        if (initialRoutingOwnership is not null)
        {
            if (initialRoutingOwnership.Owner != ClipProcessingRuntimeOwner.Routing ||
                !initialRoutingOwnership.IsCurrent ||
                !initialRoutingOwnership.IsIssuedBy(ownership) ||
                ownership.Owner != ClipProcessingRuntimeOwner.Routing)
            {
                throw new ArgumentException(
                    "The initial Routing ownership lease is not current for this coordinator.",
                    nameof(initialRoutingOwnership));
            }
            _routingOwnership = initialRoutingOwnership;
            // A committed migration marker is already a durable no-Legacy fence even before
            // the separate execution-authority document is recovered.
            _authorityCommitEntered = true;
            _recoveryPreparationRequired = _options.RecoverCommittedMigration;
            _state = (int)RoutingRuntimeCoordinatorState.RoutingQuiesced;
        }
        else
        {
            if (ownership.Owner == ClipProcessingRuntimeOwner.Routing)
            {
                throw new ArgumentException(
                    "Existing Routing ownership requires explicit committed-authority adoption.",
                    nameof(options));
            }
            if (!_options.LegacyRuntimeAllowed &&
                ownership.Owner == ClipProcessingRuntimeOwner.Legacy)
            {
                throw new ArgumentException(
                    "A no-Legacy coordinator cannot adopt existing Legacy ownership.",
                    nameof(options));
            }
            _state = (int)(_options.RequestedEnabled
                ? InitialEnabledState(ownership.Owner, _options.LegacyRuntimeAllowed)
                : RoutingRuntimeCoordinatorState.Disabled);
        }
    }

    internal RoutingRuntimeCoordinatorState State =>
        (RoutingRuntimeCoordinatorState)Volatile.Read(ref _state);

    internal bool RequestedEnabled => _options.RequestedEnabled;

    internal async Task<RoutingRuntimeTransitionResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.RequestedEnabled)
        {
            return Result(RoutingRuntimeTransitionStatus.DisabledByDefault);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_routingOwnership is { IsCurrent: true } &&
                _ownership.Owner == ClipProcessingRuntimeOwner.Routing)
            {
                if (State == RoutingRuntimeCoordinatorState.RoutingActive)
                {
                    return Result(RoutingRuntimeTransitionStatus.AlreadyRunning);
                }
                if (_recoveryPreparationRequired)
                {
                    try
                    {
                        await _routingRuntime.PrepareActivationAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                        return Result(
                            RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded,
                            error: exception);
                    }
                    _recoveryPreparationRequired = false;
                }
                else if (!_authorityCommitEntered)
                {
                    SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                    return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
                }
                if (_routingMayBeActive &&
                    !await TryQuiesceUncertainRoutingAsync().ConfigureAwait(false))
                {
                    return Result(RoutingRuntimeTransitionStatus.RoutingStopFailed);
                }
                SetState(RoutingRuntimeCoordinatorState.StartingRouting);
                return await CommitInspectAndStartRoutingAsync(
                        _routingOwnership, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
                if (_authorityCommitEntered)
                {
                    SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                    return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
                }
                if (!_options.LegacyRuntimeAllowed && _ownership.Owner is null)
                {
                    return await StartWithoutLegacyAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                if (_ownership.Owner is null)
                {
                    _ = await RestoreLegacyAsync().ConfigureAwait(false);
                }
                SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
                if (_ownership.Owner == ClipProcessingRuntimeOwner.Legacy)
                {
                    SetState(RoutingRuntimeCoordinatorState.LegacyActive);
                }
                return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
            }

            SetState(RoutingRuntimeCoordinatorState.StartingRouting);
            Exception? legacyStopError = null;
            try
            {
                // The current DiscordAwareController stop is deliberately non-cancellable: it
                // releases ownership only after its watcher task has fully unwound.
                await _legacyRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                legacyStopError = exception;
            }

            if (legacyStopError is not null)
            {
                if (_ownership.Owner == ClipProcessingRuntimeOwner.Legacy)
                {
                    SetState(RoutingRuntimeCoordinatorState.LegacyActive);
                    return Result(
                        RoutingRuntimeTransitionStatus.LegacyStopFailed,
                        error: legacyStopError);
                }

                var recovery = await RestoreLegacyAsync().ConfigureAwait(false);
                return Result(
                    recovery
                        ? RoutingRuntimeTransitionStatus.LegacyStopFailed
                        : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded,
                    error: legacyStopError);
            }

            if (_ownership.Owner is not null ||
                !_ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Transition,
                    out var transitionOwnership) ||
                transitionOwnership is null)
            {
                var recovery = await RestoreLegacyAsync().ConfigureAwait(false);
                return Result(recovery
                    ? RoutingRuntimeTransitionStatus.OwnershipUnavailable
                    : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _ = await StartLegacyFromTransitionAsync(transitionOwnership)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                await _routingRuntime.PrepareActivationAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_routingRuntime.RequiresRoutingFenceAfterPreparation())
                {
                    if (_ownership.TryTransfer(
                            transitionOwnership,
                            ClipProcessingRuntimeOwner.Routing,
                            out var fencedOwnership) && fencedOwnership is not null)
                    {
                        _routingOwnership = fencedOwnership;
                    }
                    else
                    {
                        transitionOwnership.Dispose();
                    }
                    _authorityCommitEntered = true;
                    SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                    return Result(
                        RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded,
                        error: exception);
                }
                var recovery = (await StartLegacyFromTransitionAsync(transitionOwnership)
                        .ConfigureAwait(false)).LegacyRunning;
                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested && recovery)
                {
                    throw;
                }
                return Result(
                    recovery
                        ? RoutingRuntimeTransitionStatus.PreparationFailedLegacyRestored
                        : RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded,
                    error: exception);
            }

            var durablePreparationFence =
                _routingRuntime.RequiresRoutingFenceAfterPreparation();
            if (cancellationToken.IsCancellationRequested && !durablePreparationFence)
            {
                _ = await StartLegacyFromTransitionAsync(transitionOwnership)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!_ownership.TryTransfer(
                    transitionOwnership,
                    ClipProcessingRuntimeOwner.Routing,
                    out var routingOwnership) ||
                routingOwnership is null)
            {
                transitionOwnership.Dispose();
                if (durablePreparationFence)
                {
                    _authorityCommitEntered = true;
                    SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                    return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
                }
                var recovery = await RestoreLegacyAsync().ConfigureAwait(false);
                return Result(recovery
                    ? RoutingRuntimeTransitionStatus.OwnershipUnavailable
                    : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded);
            }

            _routingOwnership = routingOwnership;
            _authorityCommitEntered = true;
            return await CommitInspectAndStartRoutingAsync(routingOwnership, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_authorityCommitEntered)
            {
                SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            }
            else if (!_options.LegacyRuntimeAllowed)
            {
                SetState(RoutingRuntimeCoordinatorState.SetupReady);
            }
            else if (_ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
                _ = await RestoreLegacyAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<RoutingRuntimeTransitionResult> StartWithoutLegacyAsync(
        CancellationToken cancellationToken)
    {
        if (_options.LegacyRuntimeAllowed)
            throw new InvalidOperationException("The no-Legacy activation path was not selected.");
        if (_ownership.Owner is not null ||
            !_ownership.TryAcquire(
                ClipProcessingRuntimeOwner.Transition,
                out var transitionOwnership) ||
            transitionOwnership is null)
        {
            SetState(RoutingRuntimeCoordinatorState.SetupReady);
            return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
        }

        SetState(RoutingRuntimeCoordinatorState.StartingRouting);
        if (cancellationToken.IsCancellationRequested)
        {
            transitionOwnership.Dispose();
            SetState(RoutingRuntimeCoordinatorState.SetupReady);
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            await _routingRuntime.PrepareActivationAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_routingRuntime.RequiresRoutingFenceAfterPreparation())
            {
                FencePreparedActivation(transitionOwnership);
                return Result(
                    RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded,
                    error: exception);
            }

            transitionOwnership.Dispose();
            SetState(RoutingRuntimeCoordinatorState.SetupReady);
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return Result(
                RoutingRuntimeTransitionStatus.PreparationFailedSetupReady,
                error: exception);
        }

        var durablePreparationFence =
            _routingRuntime.RequiresRoutingFenceAfterPreparation();
        if (cancellationToken.IsCancellationRequested && !durablePreparationFence)
        {
            transitionOwnership.Dispose();
            SetState(RoutingRuntimeCoordinatorState.SetupReady);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!_ownership.TryTransfer(
                transitionOwnership,
                ClipProcessingRuntimeOwner.Routing,
                out var routingOwnership) ||
            routingOwnership is null)
        {
            transitionOwnership.Dispose();
            if (durablePreparationFence)
            {
                _authorityCommitEntered = true;
                SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            }
            else
            {
                SetState(RoutingRuntimeCoordinatorState.SetupReady);
            }
            return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
        }

        _routingOwnership = routingOwnership;
        _authorityCommitEntered = true;
        return await CommitInspectAndStartRoutingAsync(routingOwnership, cancellationToken)
            .ConfigureAwait(false);
    }

    private void FencePreparedActivation(
        ClipProcessingOwnershipLease transitionOwnership)
    {
        if (_ownership.TryTransfer(
                transitionOwnership,
                ClipProcessingRuntimeOwner.Routing,
                out var fencedOwnership) && fencedOwnership is not null)
        {
            _routingOwnership = fencedOwnership;
        }
        else
        {
            transitionOwnership.Dispose();
        }
        _authorityCommitEntered = true;
        _recoveryPreparationRequired = true;
        SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
    }

    private async Task<RoutingRuntimeTransitionResult> CommitInspectAndStartRoutingAsync(
        ClipProcessingOwnershipLease routingOwnership,
        CancellationToken cancellationToken)
    {
        _authorityCommitEntered = true;
        try
        {
            await _routingRuntime.CommitExecutionAuthorityAsync(routingOwnership)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return Result(
                RoutingRuntimeTransitionStatus.AuthorityCommitFailedRecoveryNeeded,
                error: exception);
        }

        RoutingRuntimeGateInspection permit;
        try
        {
            permit = _routingRuntime.InspectActivation(routingOwnership);
        }
        catch (Exception exception)
        {
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return Result(
                RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded,
                error: exception);
        }
        if (!permit.Enabled || !permit.HasRequiredSourceCoverage)
        {
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return Result(
                RoutingRuntimeTransitionStatus.GateRejectedRecoveryNeeded,
                permit);
        }

        try
        {
            _routingMayBeActive = true;
            await _routingRuntime.StartAsync(routingOwnership, cancellationToken)
                .ConfigureAwait(false);
            var currentPermit = _routingRuntime.InspectActivation(routingOwnership);
            if (!routingOwnership.IsCurrent || !permit.SamePermit(currentPermit))
            {
                throw new InvalidOperationException(
                    "Routing activation changed while the runtime was starting.");
            }
        }
        catch (Exception exception)
        {
            var quiesced = await QuiesceRoutingAfterFailedStartAsync().ConfigureAwait(false);
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested && quiesced)
            {
                throw;
            }
            return Result(
                RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded,
                permit,
                exception);
        }

        SetState(RoutingRuntimeCoordinatorState.RoutingActive);
        return Result(RoutingRuntimeTransitionStatus.Started, permit);
    }

    internal async Task<RoutingRuntimeTransitionResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.RequestedEnabled)
        {
            return Result(RoutingRuntimeTransitionStatus.DisabledByDefault);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var routingOwnership = _routingOwnership;
            if (routingOwnership is null || !routingOwnership.IsCurrent ||
                _ownership.Owner != ClipProcessingRuntimeOwner.Routing)
            {
                if (_authorityCommitEntered)
                {
                    SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
                    return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
                }
                if (_ownership.Owner == ClipProcessingRuntimeOwner.Legacy)
                {
                    _routingOwnership = null;
                    SetState(RoutingRuntimeCoordinatorState.LegacyActive);
                    return Result(RoutingRuntimeTransitionStatus.AlreadyStopped);
                }

                if (_ownership.Owner is null)
                {
                    var recovered = await RestoreLegacyAsync().ConfigureAwait(false);
                    return Result(recovered
                        ? RoutingRuntimeTransitionStatus.Stopped
                        : RoutingRuntimeTransitionStatus.LegacyRestartFailed);
                }

                SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
                return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
            }

            if (_authorityCommitEntered &&
                State == RoutingRuntimeCoordinatorState.RoutingQuiesced)
            {
                return Result(RoutingRuntimeTransitionStatus.AlreadyStopped);
            }

            SetState(RoutingRuntimeCoordinatorState.StoppingRouting);
            try
            {
                // After routing owns the pipeline, a stop is an atomic convergence operation.
                // Caller cancellation is checked before it begins, not during cleanup.
                await _routingRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _routingMayBeActive = false;
            }
            catch (Exception exception)
            {
                _routingMayBeActive = true;
                SetState(_authorityCommitEntered
                    ? RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded
                    : RoutingRuntimeCoordinatorState.RoutingActive);
                return Result(
                    RoutingRuntimeTransitionStatus.RoutingStopFailed,
                error: exception);
            }

            if (_authorityCommitEntered)
            {
                SetState(RoutingRuntimeCoordinatorState.RoutingQuiesced);
                return Result(RoutingRuntimeTransitionStatus.Stopped);
            }

            if (!_ownership.TryTransfer(
                    routingOwnership,
                    ClipProcessingRuntimeOwner.Transition,
                    out var transitionOwnership) ||
                transitionOwnership is null)
            {
                SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
                return Result(RoutingRuntimeTransitionStatus.OwnershipUnavailable);
            }

            _routingOwnership = null;
            return await StartLegacyFromTransitionAsync(transitionOwnership)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<bool> QuiesceRoutingAfterFailedStartAsync()
    {
        try
        {
            await _routingRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _routingMayBeActive = false;
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return true;
        }
        catch
        {
            _routingMayBeActive = true;
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return false;
        }
    }

    private async Task<bool> TryQuiesceUncertainRoutingAsync()
    {
        try
        {
            await _routingRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _routingMayBeActive = false;
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return true;
        }
        catch
        {
            _routingMayBeActive = true;
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return false;
        }
    }

    private async Task<bool> RestoreLegacyAsync()
    {
        if (!_options.LegacyRuntimeAllowed)
        {
            SetState(_authorityCommitEntered
                ? RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded
                : RoutingRuntimeCoordinatorState.SetupReady);
            return false;
        }
        if (_authorityCommitEntered)
        {
            SetState(RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded);
            return false;
        }
        if (_ownership.Owner == ClipProcessingRuntimeOwner.Legacy)
        {
            SetState(RoutingRuntimeCoordinatorState.LegacyActive);
            return true;
        }
        if (_ownership.Owner is not null ||
            !_ownership.TryAcquire(
                ClipProcessingRuntimeOwner.Transition,
                out var transitionOwnership) ||
            transitionOwnership is null)
        {
            SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
            return false;
        }

        return (await StartLegacyFromTransitionAsync(transitionOwnership)
                .ConfigureAwait(false)).LegacyRunning;
    }

    private async Task<RoutingRuntimeTransitionResult> StartLegacyFromTransitionAsync(
        ClipProcessingOwnershipLease transitionOwnership)
    {
        if (!_options.LegacyRuntimeAllowed)
            throw new InvalidOperationException("Legacy processing is disabled for this profile.");
        if (_authorityCommitEntered)
        {
            throw new InvalidOperationException(
                "Legacy processing cannot resume after Routing authority commit begins.");
        }
        if (!_ownership.TryTransfer(
                transitionOwnership,
                ClipProcessingRuntimeOwner.Legacy,
                out var legacyOwnership) ||
            legacyOwnership is null)
        {
            transitionOwnership.Dispose();
            SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
            return Result(RoutingRuntimeTransitionStatus.LegacyRestartFailed);
        }

        try
        {
            await _legacyRuntime.StartAsync(legacyOwnership, CancellationToken.None)
                .ConfigureAwait(false);
            if (legacyOwnership.IsCurrent ||
                _ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
                throw new InvalidOperationException(
                    "The legacy runtime did not adopt its ownership lease.");
            }
            SetState(RoutingRuntimeCoordinatorState.LegacyActive);
            return Result(RoutingRuntimeTransitionStatus.Stopped);
        }
        catch (Exception exception)
        {
            legacyOwnership.Dispose();
            try
            {
                await _legacyRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The result remains recovery-needed; never start routing over an uncertain
                // legacy runtime merely to hide the restart failure.
            }
            SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
            return Result(
                RoutingRuntimeTransitionStatus.LegacyRestartFailed,
                error: exception);
        }
    }

    private RoutingRuntimeTransitionResult Result(
        RoutingRuntimeTransitionStatus status,
        RoutingRuntimeGateInspection? inspection = null,
        Exception? error = null) => new(status, State, inspection, error);

    private void SetState(RoutingRuntimeCoordinatorState state) =>
        Volatile.Write(ref _state, (int)state);

    private static RoutingRuntimeCoordinatorState InitialEnabledState(
        ClipProcessingRuntimeOwner? owner,
        bool legacyRuntimeAllowed) => owner switch
    {
        ClipProcessingRuntimeOwner.Legacy => RoutingRuntimeCoordinatorState.LegacyActive,
        ClipProcessingRuntimeOwner.Routing =>
            RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded,
        _ => legacyRuntimeAllowed
            ? RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded
            : RoutingRuntimeCoordinatorState.SetupReady
    };
}
