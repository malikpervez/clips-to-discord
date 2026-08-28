namespace ClipsToDiscord;

internal sealed record RoutingRuntimeCoordinatorOptions(bool RequestedEnabled)
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
    LegacyActive,
    StartingRouting,
    RoutingActive,
    StoppingRouting,
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
    PreparationFailedRecoveryNeeded,
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
/// quiesce every registered routing callback before it returns; the coordinator then transfers
/// ownership back to the legacy runtime.
/// </summary>
internal interface IRoutingClipProcessingRuntime
{
    /// <summary>
    /// Performs the durable, idempotent migration preparation that requires a quiesced legacy
    /// watcher. The coordinator invokes this only while it owns Transition, before any Routing
    /// lease exists. Cancellation or failure must leave state safe to resume on another attempt.
    /// </summary>
    ValueTask PrepareActivationAsync(CancellationToken cancellationToken);

    RoutingRuntimeGateInspection InspectActivation(
        ClipProcessingOwnershipLease ownership);

    ValueTask StartAsync(
        ClipProcessingOwnershipLease ownership,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Serializes the reversible cutover between the current watcher and RoutingRuntimeBridge.
/// Ownership is the authority: the legacy runtime is fully stopped before Transition can be
/// acquired, and routing remains the owner until its callbacks are fully stopped. Once a
/// transition begins, cleanup and rollback ignore caller cancellation so cancellation cannot
/// strand both runtimes stopped or allow them to overlap.
/// </summary>
internal sealed class RoutingRuntimeCoordinator
{
    private readonly ClipProcessingOwnershipCoordinator _ownership;
    private readonly ILegacyClipProcessingRuntime _legacyRuntime;
    private readonly IRoutingClipProcessingRuntime _routingRuntime;
    private readonly RoutingRuntimeCoordinatorOptions _options;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private ClipProcessingOwnershipLease? _routingOwnership;
    private int _state;

    internal RoutingRuntimeCoordinator(
        ClipProcessingOwnershipCoordinator ownership,
        ILegacyClipProcessingRuntime legacyRuntime,
        IRoutingClipProcessingRuntime routingRuntime,
        RoutingRuntimeCoordinatorOptions? options = null)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _legacyRuntime = legacyRuntime ?? throw new ArgumentNullException(nameof(legacyRuntime));
        _routingRuntime = routingRuntime ?? throw new ArgumentNullException(nameof(routingRuntime));
        _options = options ?? RoutingRuntimeCoordinatorOptions.Default;
        _state = (int)(_options.RequestedEnabled
            ? InitialEnabledState(ownership.Owner)
            : RoutingRuntimeCoordinatorState.Disabled);
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
                SetState(RoutingRuntimeCoordinatorState.RoutingActive);
                return Result(RoutingRuntimeTransitionStatus.AlreadyRunning);
            }

            if (_ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
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

            if (!_ownership.TryTransfer(
                    transitionOwnership,
                    ClipProcessingRuntimeOwner.Routing,
                    out var routingOwnership) ||
                routingOwnership is null)
            {
                transitionOwnership.Dispose();
                var recovery = await RestoreLegacyAsync().ConfigureAwait(false);
                return Result(recovery
                    ? RoutingRuntimeTransitionStatus.OwnershipUnavailable
                    : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded);
            }

            _routingOwnership = routingOwnership;
            RoutingRuntimeGateInspection permit;
            try
            {
                permit = _routingRuntime.InspectActivation(routingOwnership);
            }
            catch (Exception exception)
            {
                var recovery = await RollBackRoutingStartAsync(runtimeMayBeActive: false)
                    .ConfigureAwait(false);
                return Result(
                    recovery
                        ? RoutingRuntimeTransitionStatus.RoutingStartFailedLegacyRestored
                        : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded,
                    error: exception);
            }
            if (!permit.Enabled)
            {
                var recovery = await RollBackRoutingStartAsync(runtimeMayBeActive: false)
                    .ConfigureAwait(false);
                return Result(
                    recovery
                        ? RoutingRuntimeTransitionStatus.GateRejectedLegacyRestored
                        : RoutingRuntimeTransitionStatus.GateRejectedRecoveryNeeded,
                    permit);
            }

            try
            {
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
                var recovery = await RollBackRoutingStartAsync(runtimeMayBeActive: true)
                    .ConfigureAwait(false);
                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested && recovery)
                {
                    throw;
                }
                return Result(
                    recovery
                        ? RoutingRuntimeTransitionStatus.RoutingStartFailedLegacyRestored
                        : RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded,
                    permit,
                    exception);
            }

            SetState(RoutingRuntimeCoordinatorState.RoutingActive);
            return Result(RoutingRuntimeTransitionStatus.Started, permit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
                _ = _routingOwnership is null
                    ? await RestoreLegacyAsync().ConfigureAwait(false)
                    : await RollBackRoutingStartAsync(runtimeMayBeActive: true)
                        .ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _transition.Release();
        }
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

            SetState(RoutingRuntimeCoordinatorState.StoppingRouting);
            try
            {
                // After routing owns the pipeline, a stop is an atomic convergence operation.
                // Caller cancellation is checked before it begins, not during cleanup.
                await _routingRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetState(RoutingRuntimeCoordinatorState.RoutingActive);
                return Result(
                    RoutingRuntimeTransitionStatus.RoutingStopFailed,
                    error: exception);
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

    private async Task<bool> RollBackRoutingStartAsync(bool runtimeMayBeActive)
    {
        if (runtimeMayBeActive)
        {
            try
            {
                await _routingRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Do not transfer ownership over an uncertain partial routing start. Keeping
                // Routing authority blocks the legacy watcher until an operator retries cleanup.
                SetState(RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded);
                return false;
            }
        }

        var routingOwnership = _routingOwnership;
        ClipProcessingOwnershipLease? transitionOwnership = null;
        if (routingOwnership is { IsCurrent: true } &&
            _ownership.TryTransfer(
                routingOwnership,
                ClipProcessingRuntimeOwner.Transition,
                out transitionOwnership) &&
            transitionOwnership is not null)
        {
            _routingOwnership = null;
            return (await StartLegacyFromTransitionAsync(transitionOwnership)
                    .ConfigureAwait(false)).LegacyRunning;
        }

        _routingOwnership = null;
        return await RestoreLegacyAsync().ConfigureAwait(false);
    }

    private async Task<bool> RestoreLegacyAsync()
    {
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
        ClipProcessingRuntimeOwner? owner) => owner switch
    {
        ClipProcessingRuntimeOwner.Legacy => RoutingRuntimeCoordinatorState.LegacyActive,
        _ => RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded
    };
}
